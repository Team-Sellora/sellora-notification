using System.Text.Json;
using Sellora.NotificationService.Application.Events;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Application.Notifications;

/// <summary>One record as it came off Kafka.</summary>
/// <param name="StrictEventTypes">
/// True on the order topic (an unknown type there is a mistake → dead-letter).
/// False on shared topics (inventory, delivery) that carry events meant for
/// other consumers too → unknown types are ignored.
/// </param>
public sealed record ConsumedMessage(
    string Topic,
    int Partition,
    long Offset,
    string? Key,
    string? Value,
    bool StrictEventTypes = true);

/// <summary>US-E5-4: a name or address learned from an event, for later lookups.</summary>
public sealed record DirectoryUpdate(DirectoryEntryKind Kind, Guid CompanyId, Guid EntryId, string? Name, string? Email);

public enum PlanKind
{
    /// <summary>A notifiable event: create one request with these recipients.</summary>
    Notify,

    /// <summary>A known event that notifies nobody (e.g. OrderApproved): commit and move on.</summary>
    Ignore,

    /// <summary>Unreadable, unknown or incomplete: park it on the dead-letter topic.</summary>
    DeadLetter
}

/// <param name="Recipients">A recipient with a null email is resolved by the intake (directory or settings).</param>
/// <param name="LowStock">Set for low stock: what the intake looks up to name the product and agency.</param>
public sealed record NotificationDraft(
    Guid CompanyId,
    Guid EventId,
    string EventType,
    string TemplateKey,
    Guid? OrderId,
    string OrderReference,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    IReadOnlyList<NewRecipient> Recipients,
    LowStockEventMessage? LowStock = null);

public sealed record NotificationPlan(
    PlanKind Kind,
    NotificationDraft? Draft,
    string Reason,
    IReadOnlyList<DirectoryUpdate>? DirectoryUpdates = null)
{
    public static NotificationPlan Notify(NotificationDraft draft, IReadOnlyList<DirectoryUpdate>? updates = null) =>
        new(PlanKind.Notify, draft, "notifiable", updates);

    public static NotificationPlan Ignore(string reason, IReadOnlyList<DirectoryUpdate>? updates = null) =>
        new(PlanKind.Ignore, null, reason, updates);

    public static NotificationPlan DeadLetter(string reason) => new(PlanKind.DeadLetter, null, reason);
}

/// <summary>
/// US-E5-1: decides, from the raw message alone, whether an event becomes a
/// notification request and who it is addressed to. Pure — no I/O — so the
/// rules are unit-tested without Kafka or a database.
///
/// Recipient rule: every notifiable order event goes to both the shop and
/// the agency. Telling the shop about every order placed in its name is the
/// point (US-E4-5: orders entered without the shop's knowledge), and the
/// agency is the party that fulfils, approves and collects.
/// </summary>
public static class NotificationPlanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Template per notifiable event type. Anything not here and not ignored is dead-lettered.</summary>
    public static readonly IReadOnlyDictionary<string, string> Templates = new Dictionary<string, string>
    {
        [OrderEventTypes.OrderPlaced] = "order-placed.v1",
        [OrderEventTypes.OrderConfirmed] = "order-confirmed.v1",
        [OrderEventTypes.PaymentRecorded] = "payment-recorded.v1",
        [OrderEventTypes.OrderCancelled] = "order-cancelled.v1",
        // US-E5-4
        [DeliveryEventTypes.DeliveryStatusChanged] = "delivery-status.v1",
        [DeliveryEventTypes.DeliveryDisputed] = "delivery-disputed.v1",
        [InventoryEventTypes.LowStockDetected] = "low-stock.v1"
    };

    public static readonly IReadOnlySet<string> Ignored = new HashSet<string>
    {
        OrderEventTypes.OrderApproved,
        OrderEventTypes.VanStockReturned
    };

    public static NotificationPlan Plan(ConsumedMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Value))
        {
            return NotificationPlan.DeadLetter("The message has no payload.");
        }

        JsonElement root;

        try
        {
            using var document = JsonDocument.Parse(message.Value);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return NotificationPlan.DeadLetter("The payload is not a JSON object.");
            }

            root = document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            return NotificationPlan.DeadLetter($"The payload is not valid JSON: {exception.Message}");
        }

        // Case-insensitive: sellora-inventory serialises with default options,
        // so its events arrive as "EventType", not "eventType".
        var eventType = root.EnumerateObject()
            .Where(property => string.Equals(property.Name, "eventType", StringComparison.OrdinalIgnoreCase) &&
                               property.Value.ValueKind == JsonValueKind.String)
            .Select(property => property.Value.GetString()?.Trim())
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(eventType))
        {
            return NotificationPlan.DeadLetter("eventType is missing.");
        }

        try
        {
            return eventType switch
            {
                DeliveryEventTypes.DeliveryStatusChanged or DeliveryEventTypes.DeliveryDisputed =>
                    PlanDelivery(eventType, root.Deserialize<DeliveryEventMessage>(Json)!),
                InventoryEventTypes.LowStockDetected =>
                    PlanLowStock(root.Deserialize<LowStockEventMessage>(Json)!),
                _ when Ignored.Contains(eventType) || Templates.ContainsKey(eventType) =>
                    PlanOrder(eventType, root.Deserialize<OrderEventMessage>(Json)!),
                _ when message.StrictEventTypes =>
                    NotificationPlan.DeadLetter($"Unrecognised event type '{eventType}'."),
                _ => NotificationPlan.Ignore($"{eventType} is not an event this service notifies about.")
            };
        }
        catch (JsonException exception)
        {
            return NotificationPlan.DeadLetter($"{eventType} does not match its contract: {exception.Message}");
        }
    }

    private static NotificationPlan PlanOrder(string eventType, OrderEventMessage parsed)
    {
        var updates = OrderDirectoryUpdates(parsed);

        if (Ignored.Contains(eventType))
        {
            return NotificationPlan.Ignore($"{eventType} does not notify anyone.", updates);
        }

        var templateKey = Templates[eventType];

        // A new major schema could mean something different; park it rather than guess.
        if (parsed.SchemaVersion != OrderEventTypes.SupportedSchemaVersion)
        {
            return NotificationPlan.DeadLetter(
                $"Unsupported schemaVersion '{parsed.SchemaVersion}' (expected {OrderEventTypes.SupportedSchemaVersion}).");
        }

        var missing = Missing(parsed, eventType);

        if (missing.Count > 0)
        {
            return NotificationPlan.DeadLetter($"{eventType} is missing: {string.Join(", ", missing)}.");
        }

        var recipients = new List<NewRecipient>
        {
            new(RecipientKind.Shop, parsed.Shop!.ShopId, parsed.Shop.OwnerName ?? parsed.Shop.Name, parsed.Shop.OwnerEmail),
            new(RecipientKind.Agency, parsed.Agency!.AgencyId, parsed.Agency.Name, parsed.Agency.Email)
        };

        return NotificationPlan.Notify(new NotificationDraft(
            parsed.CompanyId,
            parsed.EventId,
            eventType,
            templateKey,
            parsed.OrderId,
            parsed.OrderReference!.Trim(),
            parsed.CorrelationId,
            parsed.OccurredAt,
            recipients), updates);
    }

    /// <summary>
    /// US-E5-4-T2: delivery news goes to the destination shop and the owning
    /// agency — but a status change only when they can act on it.
    /// </summary>
    private static NotificationPlan PlanDelivery(string eventType, DeliveryEventMessage parsed)
    {
        if (parsed.SchemaVersion != OrderEventTypes.SupportedSchemaVersion)
        {
            return NotificationPlan.DeadLetter(
                $"Unsupported schemaVersion '{parsed.SchemaVersion}' (expected {OrderEventTypes.SupportedSchemaVersion}).");
        }

        if (eventType == DeliveryEventTypes.DeliveryStatusChanged &&
            (string.IsNullOrWhiteSpace(parsed.Status) || !DeliveryEventTypes.NotifiableStatuses.Contains(parsed.Status.Trim())))
        {
            // Recorded by Delivery; nothing a shop or agency can do about it.
            return NotificationPlan.Ignore($"Delivery status '{parsed.Status}' is an internal step; not notified.");
        }

        var missing = new List<string>();
        if (parsed.EventId == Guid.Empty) missing.Add("eventId");
        if (parsed.CompanyId == Guid.Empty) missing.Add("companyId");
        if (parsed.DeliveryId == Guid.Empty) missing.Add("deliveryId");
        if (string.IsNullOrWhiteSpace(parsed.OrderReference) ||
            parsed.OrderReference.Trim().Length > NotificationRequest.MaxReferenceLength) missing.Add("orderReference");
        if (parsed.OccurredAt == default) missing.Add("occurredAt");
        if (parsed.Shop is null || parsed.Shop.ShopId == Guid.Empty) missing.Add("shop.shopId");
        if (parsed.Agency is null || parsed.Agency.AgencyId == Guid.Empty) missing.Add("agency.agencyId");

        if (missing.Count > 0)
        {
            return NotificationPlan.DeadLetter($"{eventType} is missing: {string.Join(", ", missing)}.");
        }

        // Emails may be omitted by the producer; the intake fills them from
        // the directory learned from order events.
        var recipients = new List<NewRecipient>
        {
            new(RecipientKind.Shop, parsed.Shop!.ShopId, parsed.Shop.OwnerName ?? parsed.Shop.Name, parsed.Shop.OwnerEmail),
            new(RecipientKind.Agency, parsed.Agency!.AgencyId, parsed.Agency.Name, parsed.Agency.Email)
        };

        return NotificationPlan.Notify(new NotificationDraft(
            parsed.CompanyId,
            parsed.EventId,
            eventType,
            Templates[eventType],
            parsed.OrderId,
            parsed.OrderReference!.Trim(),
            parsed.CorrelationId,
            parsed.OccurredAt,
            recipients));
    }

    /// <summary>
    /// US-E5-4-T2: low stock goes to the agency that holds the stock and to
    /// the company's alert address — explicitly never to a shop owner.
    /// </summary>
    private static NotificationPlan PlanLowStock(LowStockEventMessage parsed)
    {
        if (parsed.SchemaVersion != OrderEventTypes.SupportedSchemaVersion)
        {
            return NotificationPlan.DeadLetter(
                $"Unsupported schemaVersion '{parsed.SchemaVersion}' (expected {OrderEventTypes.SupportedSchemaVersion}).");
        }

        var missing = new List<string>();
        if (parsed.EventId == Guid.Empty) missing.Add("eventId");
        if (parsed.CompanyId == Guid.Empty) missing.Add("companyId");
        if (parsed.StockItemId == Guid.Empty) missing.Add("stockItemId");
        if (parsed.ProductId == Guid.Empty) missing.Add("productId");
        if (parsed.DetectedAt == default) missing.Add("detectedAt");

        if (missing.Count > 0)
        {
            return NotificationPlan.DeadLetter($"LowStockDetected is missing: {string.Join(", ", missing)}.");
        }

        var recipients = new List<NewRecipient>();

        // Agency-held stock: its agency hears about it. Van (SalesRep) and
        // company-warehouse stock have no agency on the event, so only the
        // company is told.
        if (string.Equals(parsed.OwnerType, "Agency", StringComparison.OrdinalIgnoreCase) &&
            parsed.ExternalOwnerId is { } agencyId && agencyId != Guid.Empty)
        {
            recipients.Add(new NewRecipient(RecipientKind.Agency, agencyId, parsed.OwnerDisplayName, null));
        }

        recipients.Add(new NewRecipient(RecipientKind.CompanyAdmin, parsed.CompanyId, "Company admin", null));

        var reference = $"STOCK-{parsed.DetectedAt.UtcDateTime:yyMMdd}-{parsed.StockItemId.ToString("N")[..6].ToUpperInvariant()}";

        return NotificationPlan.Notify(new NotificationDraft(
            parsed.CompanyId,
            parsed.EventId,
            InventoryEventTypes.LowStockDetected,
            Templates[InventoryEventTypes.LowStockDetected],
            null,
            reference,
            null,
            parsed.DetectedAt,
            recipients,
            parsed));
    }

    /// <summary>Names and addresses every order event carries, remembered for low-stock and delivery emails.</summary>
    private static IReadOnlyList<DirectoryUpdate> OrderDirectoryUpdates(OrderEventMessage parsed)
    {
        var updates = new List<DirectoryUpdate>();

        if (parsed.CompanyId == Guid.Empty)
        {
            return updates;
        }

        if (parsed.Agency is { AgencyId: var agencyId } agency && agencyId != Guid.Empty)
        {
            updates.Add(new DirectoryUpdate(DirectoryEntryKind.Agency, parsed.CompanyId, agencyId, agency.Name, agency.Email));
        }

        if (parsed.Shop is { ShopId: var shopId } shop && shopId != Guid.Empty)
        {
            updates.Add(new DirectoryUpdate(DirectoryEntryKind.Shop, parsed.CompanyId, shopId, shop.OwnerName ?? shop.Name, shop.OwnerEmail));
        }

        foreach (var line in parsed.Lines ?? Array.Empty<EventLine>())
        {
            if (line is not null && line.ProductId != Guid.Empty && !string.IsNullOrWhiteSpace(line.ProductName))
            {
                updates.Add(new DirectoryUpdate(DirectoryEntryKind.Product, parsed.CompanyId, line.ProductId, line.ProductName, null));
            }
        }

        return updates;
    }

    private static List<string> Missing(OrderEventMessage parsed, string eventType)
    {
        var missing = new List<string>();

        if (parsed.EventId == Guid.Empty) missing.Add("eventId");
        if (parsed.CompanyId == Guid.Empty) missing.Add("companyId");
        if (parsed.OrderId == Guid.Empty) missing.Add("orderId");
        if (string.IsNullOrWhiteSpace(parsed.OrderReference) ||
            parsed.OrderReference.Trim().Length > NotificationRequest.MaxReferenceLength) missing.Add("orderReference");
        if (parsed.OccurredAt == default) missing.Add("occurredAt");
        if (parsed.Shop is null || parsed.Shop.ShopId == Guid.Empty) missing.Add("shop.shopId");
        if (parsed.Agency is null || parsed.Agency.AgencyId == Guid.Empty) missing.Add("agency.agencyId");

        // The payment message (US-E5-2) is composed from these; without them
        // it could not say what was paid or where.
        if (eventType == OrderEventTypes.PaymentRecorded)
        {
            if (parsed.Payment is null || parsed.Payment.PaymentId == Guid.Empty) missing.Add("payment");
            if (parsed.CheckInLocation is null) missing.Add("checkInLocation");
        }

        return missing;
    }
}
