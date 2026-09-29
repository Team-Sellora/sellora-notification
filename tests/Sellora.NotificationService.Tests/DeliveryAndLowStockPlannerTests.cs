using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Application.Rendering;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-4-T2/T3: routing and templates for delivery and low stock — no Kafka, no database.</summary>
public sealed class DeliveryAndLowStockPlannerTests
{
    private static NotificationPlan Plan(System.Text.Json.Nodes.JsonObject @event, string topic) =>
        NotificationPlanner.Plan(DeliveryAndStockTestEvents.OnSharedTopic(@event, topic));

    [Theory]
    [InlineData("InTransit")]
    [InlineData("Delivered")]
    [InlineData("Failed")]
    [InlineData("Cancelled")]
    public void An_actionable_delivery_status_notifies_the_shop_and_the_agency(string status)
    {
        var plan = Plan(DeliveryAndStockTestEvents.Delivery(status: status), "sellora.delivery.v1");

        Assert.Equal(PlanKind.Notify, plan.Kind);
        Assert.Equal("delivery-status.v1", plan.Draft!.TemplateKey);
        Assert.Equal("ORD-260929-DLV001", plan.Draft.OrderReference);
        Assert.Equal(new[] { RecipientKind.Shop, RecipientKind.Agency }, plan.Draft.Recipients.Select(r => r.Kind));
        Assert.Equal("owner@lakshmi-stores.lk", plan.Draft.Recipients[0].Email);
    }

    // Q1: an internal transition is not emailed.
    [Theory]
    [InlineData("Pending")]
    [InlineData("Assigned")]
    [InlineData("Scheduled")]
    [InlineData("PickedUp")]
    public void An_internal_delivery_step_is_not_notified(string status)
    {
        var plan = Plan(DeliveryAndStockTestEvents.Delivery(status: status), "sellora.delivery.v1");

        Assert.Equal(PlanKind.Ignore, plan.Kind);
    }

    [Fact]
    public void A_dispute_notifies_the_shop_and_the_agency()
    {
        var plan = Plan(DeliveryAndStockTestEvents.Delivery(eventType: "DeliveryDisputed", status: "Delivered"), "sellora.delivery.v1");

        Assert.Equal("delivery-disputed.v1", plan.Draft!.TemplateKey);
        Assert.Equal(2, plan.Draft.Recipients.Count);
    }

    [Fact]
    public void A_delivery_event_missing_its_order_or_parties_is_dead_lettered()
    {
        Assert.Equal(PlanKind.DeadLetter, Plan(DeliveryAndStockTestEvents.Delivery().Without("orderReference"), "sellora.delivery.v1").Kind);
        Assert.Equal(PlanKind.DeadLetter, Plan(DeliveryAndStockTestEvents.Delivery().Without("shop"), "sellora.delivery.v1").Kind);
    }

    // Q2: low stock goes to the agency and the company admin — never a shop.
    [Fact]
    public void Low_agency_stock_notifies_the_agency_and_the_company_admin_but_no_shop()
    {
        var plan = Plan(DeliveryAndStockTestEvents.LowStock(), "sellora.inventory.v1");

        Assert.Equal(PlanKind.Notify, plan.Kind);
        Assert.Equal("low-stock.v1", plan.Draft!.TemplateKey);
        Assert.Equal(new[] { RecipientKind.Agency, RecipientKind.CompanyAdmin }, plan.Draft.Recipients.Select(r => r.Kind));
        Assert.DoesNotContain(plan.Draft.Recipients, r => r.Kind == RecipientKind.Shop);
        Assert.Null(plan.Draft.OrderId);
        Assert.StartsWith("STOCK-260929-", plan.Draft.OrderReference);
    }

    [Fact]
    public void Low_van_stock_notifies_only_the_company_admin()
    {
        var plan = Plan(DeliveryAndStockTestEvents.LowStock(ownerType: "SalesRep"), "sellora.inventory.v1");

        Assert.Equal(RecipientKind.CompanyAdmin, Assert.Single(plan.Draft!.Recipients).Kind);
    }

    [Fact]
    public void Other_events_on_shared_topics_are_ignored_but_unknown_order_events_still_dead_letter()
    {
        var other = DeliveryAndStockTestEvents.LowStock();
        other["EventType"] = "StockAdjusted";

        Assert.Equal(PlanKind.Ignore, Plan(other, "sellora.inventory.v1").Kind);
        Assert.Equal(PlanKind.DeadLetter, NotificationPlanner.Plan(TestEvents.Message(other)).Kind); // order topic: strict
    }

    [Fact]
    public void Order_events_teach_the_directory_agency_shop_and_product_names()
    {
        var plan = NotificationPlanner.Plan(TestEvents.Message(TestEvents.Order("OrderPlaced")));

        var updates = plan.DirectoryUpdates!;
        Assert.Contains(updates, u => u.Kind == Domain.Entities.DirectoryEntryKind.Agency && u.Email == "orders@colombo-agency.lk");
        Assert.Contains(updates, u => u.Kind == Domain.Entities.DirectoryEntryKind.Shop && u.Email == "owner@lakshmi-stores.lk");
        Assert.Contains(updates, u => u.Kind == Domain.Entities.DirectoryEntryKind.Product && u.Name == "Sunlight Soap 100g");
    }

    // T3: same render-once pipeline, a template per event type.
    [Fact]
    public void Delivery_messages_lead_with_the_order_reference_and_say_what_happened()
    {
        var failed = NotificationRenderer.Render("delivery-status.v1", DeliveryAndStockTestEvents.Delivery(status: "Failed").ToJsonString());
        var disputed = NotificationRenderer.Render(
            "delivery-disputed.v1", DeliveryAndStockTestEvents.Delivery(eventType: "DeliveryDisputed").ToJsonString());

        Assert.Equal("[ORD-260929-DLV001] Delivery failed — Lakshmi Stores", failed.Subject);
        Assert.Contains("Shop closed on arrival", failed.Text);
        Assert.Contains("sent at the same moment to Lakshmi Stores and Colombo Agency", failed.Text);
        Assert.Contains("Two cases missing", disputed.Text);
    }

    [Fact]
    public void The_low_stock_message_names_the_product_and_quantities()
    {
        var context = """{"productName":"Sunlight Soap 100g","agencyName":"Colombo Agency","ownerDisplayName":"Colombo Agency"}""";

        var message = NotificationRenderer.Render("low-stock.v1", DeliveryAndStockTestEvents.LowStock().ToJsonString(), context);

        Assert.Equal("[Low stock] Sunlight Soap 100g — 3 left at Colombo Agency", message.Subject);
        Assert.Contains("Reorder threshold: 10", message.Text);
        Assert.Contains("sent at the same moment to Colombo Agency and the company admin", message.Text);
        Assert.DoesNotContain("Lakshmi", message.Text); // no shop
    }
}
