using System.Text.Json;
using System.Text.Json.Nodes;
using Sellora.NotificationService.Application.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>
/// Order events shaped exactly like sellora-order publishes them
/// (docs/contracts/order-events.v1.md), with optional tweaks.
/// </summary>
internal static class TestEvents
{
    public static readonly Guid ShopId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid AgencyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    public static JsonObject Order(
        string eventType,
        Guid? eventId = null,
        Guid? companyId = null,
        string orderReference = "ORD-260926-K7MQ4R",
        string? shopEmail = "owner@lakshmi-stores.lk",
        string? agencyEmail = "orders@colombo-agency.lk")
    {
        var orderId = Guid.NewGuid();
        var @event = new JsonObject
        {
            ["eventId"] = (eventId ?? Guid.NewGuid()).ToString(),
            ["eventType"] = eventType,
            ["schemaVersion"] = "1.0",
            ["companyId"] = (companyId ?? Guid.NewGuid()).ToString(),
            ["entityId"] = orderId.ToString(),
            ["orderId"] = orderId.ToString(),
            ["orderReference"] = orderReference,
            ["reservationId"] = Guid.NewGuid().ToString(),
            ["occurredAt"] = "2026-09-26T04:30:00+00:00",
            ["correlationId"] = "test-correlation",
            ["fulfilmentType"] = "ImmediateCashSale",
            ["status"] = "Confirmed",
            ["orderDate"] = "2026-09-26T04:20:00+00:00",
            ["shop"] = new JsonObject
            {
                ["shopId"] = ShopId.ToString(),
                ["name"] = "Lakshmi Stores",
                ["ownerName"] = "Nimal Perera",
                ["ownerEmail"] = shopEmail
            },
            ["agency"] = new JsonObject
            {
                ["agencyId"] = AgencyId.ToString(),
                ["name"] = "Colombo Agency",
                ["email"] = agencyEmail
            },
            ["territoryId"] = Guid.NewGuid().ToString(),
            ["provinceId"] = Guid.NewGuid().ToString(),
            ["salesRep"] = new JsonObject { ["salesRepId"] = Guid.NewGuid().ToString(), ["name"] = "Ruwan Dias" },
            ["lines"] = new JsonArray(new JsonObject
            {
                ["productId"] = Guid.NewGuid().ToString(),
                ["productName"] = "Sunlight Soap 100g",
                ["quantity"] = 2,
                ["unitPrice"] = 120m,
                ["lineTotal"] = 240m
            }),
            ["subtotal"] = 240m,
            ["total"] = 240m,
            ["currency"] = "LKR",
            ["checkoutLocation"] = null
        };

        switch (eventType)
        {
            case "OrderConfirmed":
                @event["confirmedAt"] = "2026-09-26T04:30:00+00:00";
                break;
            case "PaymentRecorded":
                @event["payment"] = new JsonObject
                {
                    ["paymentId"] = Guid.NewGuid().ToString(),
                    ["amount"] = 240m,
                    ["method"] = "Cash",
                    ["recordedAt"] = "2026-09-26T04:30:00+00:00",
                    ["checkInId"] = Guid.NewGuid().ToString()
                };
                @event["checkInLocation"] = new JsonObject
                {
                    ["latitude"] = 6.896,
                    ["longitude"] = 79.8556,
                    ["distanceMeters"] = 12.5,
                    ["accuracyMeters"] = 8.0,
                    ["checkedInAt"] = "2026-09-26T04:25:00+00:00"
                };
                break;
            case "OrderCancelled":
                @event["cancelledAt"] = "2026-09-26T04:30:00+00:00";
                @event["reason"] = "Cancelled by the shop owner.";
                @event["source"] = "ShopCancellation";
                break;
        }

        return @event;
    }

    public static ConsumedMessage Message(JsonNode @event, long offset = 0) =>
        Message(@event.ToJsonString(), offset);

    public static ConsumedMessage Message(string? value, long offset = 0) =>
        new("sellora.order.v1", 0, offset, "ORD-260926-K7MQ4R", value);

    public static string EventId(JsonObject @event) => @event["eventId"]!.GetValue<string>();

    public static JsonObject Without(this JsonObject @event, params string[] path)
    {
        var copy = JsonNode.Parse(@event.ToJsonString())!.AsObject();
        var target = copy;

        for (var i = 0; i < path.Length - 1; i++)
        {
            target = target[path[i]]!.AsObject();
        }

        target.Remove(path[^1]);
        return copy;
    }

    public static string Serialize(object value) => JsonSerializer.Serialize(value);
}
