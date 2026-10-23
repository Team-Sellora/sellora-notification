using System.Text.Json.Nodes;
using Sellora.NotificationService.Application.Rendering;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-2-T1: the one message, composed from the stored event.</summary>
public sealed class NotificationRendererTests
{
    private static JsonObject Payment() => TestEvents.Order("PaymentRecorded");

    [Fact]
    public void The_subject_leads_with_the_order_reference()
    {
        var message = NotificationRenderer.Render("payment-recorded.v1", Payment().ToJsonString());

        Assert.StartsWith("[ORD-260926-K7MQ4R] Payment recorded", message.Subject);
        Assert.Contains("LKR 240.00", message.Subject);
        Assert.Contains("Lakshmi Stores", message.Subject);
    }

    // Scenario 1: identical business content — items, total, timestamp.
    [Fact]
    public void The_body_carries_the_order_items_total_method_rep_and_time()
    {
        var message = NotificationRenderer.Render("payment-recorded.v1", Payment().ToJsonString());

        foreach (var body in new[] { message.Html, message.Text })
        {
            Assert.Contains("ORD-260926-K7MQ4R", body);
            Assert.Contains("Lakshmi Stores", body);
            Assert.Contains("Colombo Agency", body);
            Assert.Contains("Ruwan Dias", body);
            Assert.Contains("Sunlight Soap 100g", body);
            Assert.Contains("LKR 120.00", body);  // unit price
            Assert.Contains("LKR 240.00", body);  // line total and total
            Assert.Contains("Cash", body);
            Assert.Contains("26 Sep 2026, 10:00:00", body);        // paid at, Sri Lanka time
            Assert.Contains("UTC 2026-09-26 04:30:00Z", body);     // and exactly, in UTC
        }
    }

    // Scenario 2: coordinates and a map link that match the payment record.
    [Fact]
    public void The_check_in_coordinates_and_map_link_match_the_payment_record()
    {
        var @event = Payment();
        var location = @event["checkInLocation"]!.AsObject();
        location["latitude"] = 6.927123;
        location["longitude"] = 79.861235;

        var message = NotificationRenderer.Render("payment-recorded.v1", @event.ToJsonString());

        const string expectedLink = "https://www.google.com/maps/search/?api=1&query=6.927123,79.861235";
        Assert.Equal(expectedLink, NotificationRenderer.MapLink(6.927123, 79.861235));
        Assert.Contains("6.927123, 79.861235", message.Html);
        Assert.Contains("6.927123, 79.861235", message.Text);
        Assert.Contains(expectedLink.Replace("&", "&amp;"), message.Html);
        Assert.Contains(expectedLink, message.Text);
        Assert.Contains("12.5 m", message.Text);  // distance from the shop
        Assert.Contains("±8 m", message.Text);    // GPS accuracy
    }

    [Fact]
    public void The_body_does_not_depend_on_who_receives_it()
    {
        var first = NotificationRenderer.Render("payment-recorded.v1", Payment().ToJsonString());
        var @event = Payment();
        var again = NotificationRenderer.Render("payment-recorded.v1", @event.ToJsonString());

        // Same event content → same bytes (event IDs are not rendered).
        Assert.Equal(first.Html, again.Html);
        Assert.Equal(first.Text, again.Text);
        Assert.Equal(first.BodySha256, again.BodySha256);
        Assert.Equal(NotificationRenderer.Sha256(first.Subject, first.Html, first.Text), first.BodySha256);
    }

    [Fact]
    public void Event_text_is_shown_not_executed()
    {
        var @event = Payment();
        @event["shop"]!["name"] = "<script>alert('x')</script>";
        @event["salesRep"]!["name"] = "Line1\r\nBcc: attacker@example.com";

        var message = NotificationRenderer.Render("payment-recorded.v1", @event.ToJsonString());

        Assert.DoesNotContain("<script>", message.Html);
        Assert.Contains("&lt;script&gt;", message.Html);
        Assert.DoesNotContain('\n', message.Subject);
        Assert.DoesNotContain('\r', message.Subject);
    }

    [Theory]
    [InlineData("OrderPlaced", "order-placed.v1", "Order placed")]
    [InlineData("OrderConfirmed", "order-confirmed.v1", "Order confirmed")]
    [InlineData("OrderCancelled", "order-cancelled.v1", "Order cancelled")]
    public void The_other_order_events_render_too(string eventType, string template, string headline)
    {
        var message = NotificationRenderer.Render(template, TestEvents.Order(eventType).ToJsonString());

        Assert.StartsWith("[ORD-260926-K7MQ4R]", message.Subject);
        Assert.Contains(headline, message.Html);
    }

    [Fact]
    public void A_payment_without_its_location_cannot_be_rendered()
    {
        var @event = Payment().Without("checkInLocation");

        Assert.Throws<NotificationRenderException>(() =>
            NotificationRenderer.Render("payment-recorded.v1", @event.ToJsonString()));
    }

    [Fact]
    public void An_unknown_template_cannot_be_rendered()
    {
        Assert.Throws<NotificationRenderException>(() =>
            NotificationRenderer.Render("mystery.v1", Payment().ToJsonString()));
    }
}
