using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-1: which events notify whom — pure, no Kafka, no database.</summary>
public sealed class NotificationPlannerTests
{
    // Q1: each event type, exactly one request, shop and agency addressed.
    [Theory]
    [InlineData("OrderPlaced", "order-placed.v1")]
    [InlineData("OrderConfirmed", "order-confirmed.v1")]
    [InlineData("PaymentRecorded", "payment-recorded.v1")]
    [InlineData("OrderCancelled", "order-cancelled.v1")]
    public void Each_order_event_is_addressed_to_the_shop_and_the_agency(string eventType, string template)
    {
        var @event = TestEvents.Order(eventType);

        var plan = NotificationPlanner.Plan(TestEvents.Message(@event));

        Assert.Equal(PlanKind.Notify, plan.Kind);
        var draft = plan.Draft!;
        Assert.Equal(eventType, draft.EventType);
        Assert.Equal(template, draft.TemplateKey);
        Assert.Equal(Guid.Parse(TestEvents.EventId(@event)), draft.EventId);
        Assert.Equal("ORD-260926-K7MQ4R", draft.OrderReference);
        Assert.Equal(2, draft.Recipients.Count);

        var shop = Assert.Single(draft.Recipients, recipient => recipient.Kind == RecipientKind.Shop);
        Assert.Equal(TestEvents.ShopId, shop.RecipientId);
        Assert.Equal("Nimal Perera", shop.Name);
        Assert.Equal("owner@lakshmi-stores.lk", shop.Email);

        var agency = Assert.Single(draft.Recipients, recipient => recipient.Kind == RecipientKind.Agency);
        Assert.Equal(TestEvents.AgencyId, agency.RecipientId);
        Assert.Equal("Colombo Agency", agency.Name);
        Assert.Equal("orders@colombo-agency.lk", agency.Email);
    }

    [Fact]
    public void A_missing_email_keeps_the_recipient_without_an_address()
    {
        var plan = NotificationPlanner.Plan(TestEvents.Message(TestEvents.Order("PaymentRecorded", shopEmail: null)));

        Assert.Equal(PlanKind.Notify, plan.Kind);
        Assert.Null(plan.Draft!.Recipients.Single(recipient => recipient.Kind == RecipientKind.Shop).Email);
    }

    [Theory]
    [InlineData("OrderApproved")]
    [InlineData("VanStockReturned")]
    public void Known_events_that_notify_nobody_are_ignored_not_dead_lettered(string eventType)
    {
        var plan = NotificationPlanner.Plan(TestEvents.Message(TestEvents.Order(eventType)));

        Assert.Equal(PlanKind.Ignore, plan.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{\"eventType\": ")]
    [InlineData("[1, 2, 3]")]
    [InlineData("\"OrderPlaced\"")]
    public void Unreadable_payloads_are_dead_lettered(string? payload)
    {
        Assert.Equal(PlanKind.DeadLetter, NotificationPlanner.Plan(TestEvents.Message(payload)).Kind);
    }

    [Fact]
    public void An_unrecognised_event_type_is_dead_lettered()
    {
        var @event = TestEvents.Order("OrderPlaced");
        @event["eventType"] = "OrderTeleported";

        var plan = NotificationPlanner.Plan(TestEvents.Message(@event));

        Assert.Equal(PlanKind.DeadLetter, plan.Kind);
        Assert.Contains("OrderTeleported", plan.Reason);
    }

    [Fact]
    public void A_different_schema_version_is_dead_lettered_rather_than_guessed()
    {
        var @event = TestEvents.Order("OrderPlaced");
        @event["schemaVersion"] = "2.0";

        Assert.Equal(PlanKind.DeadLetter, NotificationPlanner.Plan(TestEvents.Message(@event)).Kind);
    }

    [Theory]
    [InlineData("eventId")]
    [InlineData("companyId")]
    [InlineData("orderId")]
    [InlineData("orderReference")]
    [InlineData("shop")]
    [InlineData("agency")]
    public void An_event_missing_what_addressing_needs_is_dead_lettered(string field)
    {
        var plan = NotificationPlanner.Plan(TestEvents.Message(TestEvents.Order("OrderConfirmed").Without(field)));

        Assert.Equal(PlanKind.DeadLetter, plan.Kind);
    }

    [Fact]
    public void A_payment_without_payment_details_or_location_is_dead_lettered()
    {
        var noPayment = TestEvents.Order("PaymentRecorded").Without("payment");
        var noLocation = TestEvents.Order("PaymentRecorded").Without("checkInLocation");

        Assert.Contains("payment", NotificationPlanner.Plan(TestEvents.Message(noPayment)).Reason);
        Assert.Contains("checkInLocation", NotificationPlanner.Plan(TestEvents.Message(noLocation)).Reason);
    }
}
