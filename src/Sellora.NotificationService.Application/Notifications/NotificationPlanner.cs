using System.Text.Json;
using Sellora.NotificationService.Application.Events;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Application.Notifications;

/// <summary>One record as it came off Kafka.</summary>
public sealed record ConsumedMessage(string Topic, int Partition, long Offset, string? Key, string? Value);

public enum PlanKind
{
    /// <summary>A notifiable event: create one request with these recipients.</summary>
    Notify,

    /// <summary>A known event that notifies nobody (e.g. OrderApproved): commit and move on.</summary>
    Ignore,

    /// <summary>Unreadable, unknown or incomplete: park it on the dead-letter topic.</summary>
    DeadLetter
}

public sealed record NotificationDraft(
    Guid CompanyId,
    Guid EventId,
    string EventType,
    string TemplateKey,
    Guid OrderId,
    string OrderReference,
    string? CorrelationId,
    DateTimeOffset OccurredAt,
    IReadOnlyList<NewRecipient> Recipients);

public sealed record NotificationPlan(PlanKind Kind, NotificationDraft? Draft, string Reason)
{
    public static NotificationPlan Notify(NotificationDraft draft) => new(PlanKind.Notify, draft, "notifiable");

    public static NotificationPlan Ignore(string reason) => new(PlanKind.Ignore, null, reason);

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
        [OrderEventTypes.OrderCancelled] = "order-cancelled.v1"
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

        OrderEventMessage? parsed;

        try
        {
            using var document = JsonDocument.Parse(message.Value);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return NotificationPlan.DeadLetter("The payload is not a JSON object.");
            }

            parsed = document.RootElement.Deserialize<OrderEventMessage>(Json);
        }
        catch (JsonException exception)
        {
            return NotificationPlan.DeadLetter($"The payload is not valid JSON for an order event: {exception.Message}");
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.EventType))
        {
            return NotificationPlan.DeadLetter("eventType is missing.");
        }

        var eventType = parsed.EventType.Trim();

        if (Ignored.Contains(eventType))
        {
            return NotificationPlan.Ignore($"{eventType} does not notify anyone.");
        }

        if (!Templates.TryGetValue(eventType, out var templateKey))
        {
            return NotificationPlan.DeadLetter($"Unrecognised event type '{eventType}'.");
        }

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
            recipients));
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
