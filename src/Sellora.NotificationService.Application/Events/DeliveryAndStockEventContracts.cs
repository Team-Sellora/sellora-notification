namespace Sellora.NotificationService.Application.Events;

// US-E5-4 wire contracts.
//
// LowStockDetected: published today by sellora-inventory on
// sellora.inventory.v1 (Application/Events/LowStockDetectedEvent.cs). The
// owner fields are additive and arrive with the sellora-inventory change in
// this story; older events simply lack them.
//
// DeliveryStatusChanged / DeliveryDisputed: Delivery & Returns (E6) is not
// built yet. These records ARE the contract E6 must publish on
// sellora.delivery.v1 — see docs/contracts/delivery-events.v1.md.

public static class DeliveryEventTypes
{
    public const string DeliveryStatusChanged = "DeliveryStatusChanged";
    public const string DeliveryDisputed = "DeliveryDisputed";

    /// <summary>
    /// The transitions a shop or agency can act on. Internal steps
    /// (Pending, Assigned, Scheduled, PickedUp …) are recorded by Delivery
    /// but are not emailed — a shop cannot do anything about them.
    /// </summary>
    public static readonly IReadOnlySet<string> NotifiableStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "InTransit", "Delivered", "Failed", "Cancelled" };
}

public static class InventoryEventTypes
{
    public const string LowStockDetected = "LowStockDetected";
}

public sealed record DeliveryEventMessage(
    Guid EventId,
    string? EventType,
    string? SchemaVersion,
    Guid CompanyId,
    Guid DeliveryId,
    Guid? OrderId,
    string? OrderReference,
    string? PreviousStatus,
    string? Status,
    DateTimeOffset OccurredAt,
    string? CorrelationId,
    string? Reason,
    DateTimeOffset? ScheduledFor,
    EventShop? Shop,
    EventAgency? Agency,
    string? DisputeReason,
    string? RaisedByRole);

public sealed record LowStockEventMessage(
    Guid EventId,
    string? EventType,
    string? SchemaVersion,
    Guid CompanyId,
    Guid StockItemId,
    Guid InventoryOwnerId,
    Guid ProductId,
    Guid? BatchId,
    int AvailableQuantity,
    int ReorderThreshold,
    DateTimeOffset DetectedAt,
    string? OwnerType,
    Guid? ExternalOwnerId,
    string? OwnerDisplayName);
