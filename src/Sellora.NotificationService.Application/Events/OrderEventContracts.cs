namespace Sellora.NotificationService.Application.Events;

// Wire contract copied from sellora-order docs/contracts/order-events.v1.md
// (Application/Events/OrderEvents.cs). Only the fields this service reads to
// decide who to notify; the full payload is stored as-is for composing.
// Do not rename: System.Text.Json binds these by name (case-insensitive).

/// <summary>Event types on <c>sellora.order.v1</c> this service knows about.</summary>
public static class OrderEventTypes
{
    public const string SupportedSchemaVersion = "1.0";

    public const string OrderPlaced = "OrderPlaced";
    public const string OrderConfirmed = "OrderConfirmed";
    public const string PaymentRecorded = "PaymentRecorded";
    public const string OrderCancelled = "OrderCancelled";

    /// <summary>US-E4-5. Always followed by OrderConfirmed, which is the one that notifies.</summary>
    public const string OrderApproved = "OrderApproved";

    /// <summary>US-E4-6. A stock movement between van and agency; nobody outside is told.</summary>
    public const string VanStockReturned = "VanStockReturned";
}

public sealed record OrderEventMessage(
    Guid EventId,
    string? EventType,
    string? SchemaVersion,
    Guid CompanyId,
    Guid OrderId,
    string? OrderReference,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? FulfilmentType,
    EventShop? Shop,
    EventAgency? Agency,
    EventPayment? Payment,
    EventLocation? CheckInLocation);

public sealed record EventShop(Guid ShopId, string? Name, string? OwnerName, string? OwnerEmail);

public sealed record EventAgency(Guid AgencyId, string? Name, string? Email);

public sealed record EventPayment(Guid PaymentId, decimal Amount, string? Method, DateTimeOffset RecordedAt);

public sealed record EventLocation(double Latitude, double Longitude);
