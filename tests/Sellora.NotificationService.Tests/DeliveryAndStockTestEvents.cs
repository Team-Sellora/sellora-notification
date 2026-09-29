using System.Text.Json.Nodes;
using Sellora.NotificationService.Application.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-4 events: delivery (the E6 contract) and low stock (as sellora-inventory publishes it).</summary>
internal static class DeliveryAndStockTestEvents
{
    public static readonly Guid ProductId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static JsonObject Delivery(
        string eventType = "DeliveryStatusChanged",
        string status = "InTransit",
        Guid? companyId = null,
        string? shopEmail = "owner@lakshmi-stores.lk",
        string? agencyEmail = "orders@colombo-agency.lk") => new()
    {
        ["eventId"] = Guid.NewGuid().ToString(),
        ["eventType"] = eventType,
        ["schemaVersion"] = "1.0",
        ["companyId"] = (companyId ?? Guid.NewGuid()).ToString(),
        ["deliveryId"] = Guid.NewGuid().ToString(),
        ["orderId"] = Guid.NewGuid().ToString(),
        ["orderReference"] = "ORD-260929-DLV001",
        ["previousStatus"] = "Assigned",
        ["status"] = status,
        ["occurredAt"] = "2026-09-29T05:00:00+00:00",
        ["correlationId"] = "delivery-correlation",
        ["reason"] = status == "Failed" ? "Shop closed on arrival" : null,
        ["shop"] = new JsonObject
        {
            ["shopId"] = TestEvents.ShopId.ToString(),
            ["name"] = "Lakshmi Stores",
            ["ownerName"] = "Nimal Perera",
            ["ownerEmail"] = shopEmail
        },
        ["agency"] = new JsonObject
        {
            ["agencyId"] = TestEvents.AgencyId.ToString(),
            ["name"] = "Colombo Agency",
            ["email"] = agencyEmail
        },
        ["disputeReason"] = eventType == "DeliveryDisputed" ? "Two cases missing" : null,
        ["raisedByRole"] = eventType == "DeliveryDisputed" ? "ShopOwner" : null
    };

    /// <summary>Exactly sellora-inventory's LowStockDetectedEvent, with the US-E5-4 owner fields.</summary>
    public static JsonObject LowStock(Guid? companyId = null, string ownerType = "Agency", Guid? externalOwnerId = null) => new()
    {
        ["EventId"] = Guid.NewGuid().ToString(),
        ["EventType"] = "LowStockDetected",
        ["SchemaVersion"] = "1.0",
        ["CompanyId"] = (companyId ?? Guid.NewGuid()).ToString(),
        ["StockItemId"] = Guid.NewGuid().ToString(),
        ["InventoryOwnerId"] = Guid.NewGuid().ToString(),
        ["ProductId"] = ProductId.ToString(),
        ["BatchId"] = null,
        ["AvailableQuantity"] = 3,
        ["ReorderThreshold"] = 10,
        ["DetectedAt"] = "2026-09-29T05:30:00+00:00",
        ["OwnerType"] = ownerType,
        ["ExternalOwnerId"] = (externalOwnerId ?? (ownerType == "Agency" ? TestEvents.AgencyId : Guid.NewGuid())).ToString(),
        ["OwnerDisplayName"] = ownerType == "Agency" ? "Colombo Agency" : "Ruwan's van"
    };

    public static ConsumedMessage OnSharedTopic(JsonNode @event, string topic) =>
        new(topic, 0, 0, null, @event.ToJsonString(), StrictEventTypes: false);
}
