using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Application.Rendering;

/// <summary>The stored event could not be turned into a message; retrying will not change that.</summary>
public sealed class NotificationRenderException(string message) : Exception(message);

/// <summary>
/// US-E5-2-T1: composes the one message for a notification request from the
/// stored event. Pure and deterministic — the same payload always renders
/// the same bytes — and it knows nothing about who receives it: the body
/// names both the shop and the agency, so the identical text goes to both.
///
/// Every value from the event is HTML-encoded; a shop called
/// "&lt;script&gt;" is shown, never run.
/// </summary>
public static class NotificationRenderer
{
    /// <summary>Sri Lanka has no daylight saving, so a fixed offset is exact and needs no tz database.</summary>
    private static readonly TimeSpan SriLankaOffset = TimeSpan.FromHours(5.5);

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string AgencyLabel = "Agency";
    private const string CloseDiv = "</div>";
    private const string CloseRow = "</td></tr>";

    /// <param name="context">US-E5-4: names looked up at intake (low stock), as JSON; null otherwise.</param>
    public static RenderedMessage Render(string templateKey, string payload, string? context = null)
    {
        switch (templateKey)
        {
            case "delivery-status.v1":
            case "delivery-disputed.v1":
                return RenderDelivery(templateKey, payload);
            case "low-stock.v1":
                return RenderLowStock(payload, context);
        }

        OrderEventDetails details;

        try
        {
            details = JsonSerializer.Deserialize<OrderEventDetails>(payload, Json)
                ?? throw new NotificationRenderException("The stored event is empty.");
        }
        catch (JsonException exception)
        {
            throw new NotificationRenderException($"The stored event could not be read: {exception.Message}");
        }

        var template = templateKey switch
        {
            "payment-recorded.v1" => Template.PaymentRecorded,
            "order-placed.v1" => Template.OrderPlaced,
            "order-confirmed.v1" => Template.OrderConfirmed,
            "order-cancelled.v1" => Template.OrderCancelled,
            _ => throw new NotificationRenderException($"No template for '{templateKey}'.")
        };

        if (string.IsNullOrWhiteSpace(details.OrderReference))
        {
            throw new NotificationRenderException("The stored event has no order reference.");
        }

        if (template == Template.PaymentRecorded && (details.Payment is null || details.CheckInLocation is null))
        {
            throw new NotificationRenderException("A payment notification needs the payment and the check-in location.");
        }

        var content = Compose(template, details);
        var subject = Subject(template, details);
        var html = Html(content);
        var text = Text(content);

        return new RenderedMessage(subject, html, text, Sha256(subject, html, text));
    }

    /// <summary>Google Maps "search" URL (the documented Maps URLs API), pinned to the exact coordinates.</summary>
    public static string MapLink(double latitude, double longitude) =>
        $"https://www.google.com/maps/search/?api=1&query={Coordinate(latitude)},{Coordinate(longitude)}";

    public static string Coordinate(double value) => value.ToString("F6", Invariant);

    public static string Sha256(string subject, string html, string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{subject}\n\n{html}\n\n{text}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ── What the message says (shared by HTML and text) ──────────────────

    private enum Template
    {
        PaymentRecorded,
        OrderPlaced,
        OrderConfirmed,
        OrderCancelled
    }

    private sealed record Content(
        string Headline,
        string Intro,
        IReadOnlyList<(string Label, string Value)> Facts,
        IReadOnlyList<DetailsLine> Lines,
        string Currency,
        decimal Total,
        DetailsLocation? Location,
        string ShopName,
        string AgencyName,
        string Reference);

    private static Content Compose(Template template, OrderEventDetails details)
    {
        var reference = details.OrderReference!.Trim();
        var shop = Name(details.Shop?.Name, "the shop");
        var agency = Name(details.Agency?.Name, "the agency");
        var rep = Name(details.SalesRep?.Name, "the sales rep");
        var currency = string.IsNullOrWhiteSpace(details.Currency) ? "LKR" : details.Currency.Trim();

        var facts = new List<(string, string)>
        {
            ("Order reference", reference),
            ("Shop", shop),
            (AgencyLabel, agency),
            ("Sales rep", rep),
            ("Order type", details.FulfilmentType == "ScheduledDelivery" ? "Scheduled delivery" : "Cash sale")
        };

        string headline;
        string intro;

        switch (template)
        {
            case Template.PaymentRecorded:
                var payment = details.Payment!;
                headline = "Payment recorded";
                intro = $"{rep} recorded a payment of {Money(currency, payment.Amount)} from {shop} for order {reference}.";
                facts.Add(("Amount paid", Money(currency, payment.Amount)));
                facts.Add(("Payment method", Name(payment.Method, "Cash")));
                facts.Add(("Paid at", When(payment.RecordedAt)));
                break;

            case Template.OrderPlaced:
                headline = details.FulfilmentType == "ScheduledDelivery" ? "Order awaiting agency approval" : "Order placed";
                intro = $"{rep} placed order {reference} for {shop}.";
                facts.Add(("Placed at", When(details.OrderDate)));
                break;

            case Template.OrderConfirmed:
                headline = "Order confirmed";
                intro = $"Order {reference} for {shop} is confirmed.";
                facts.Add(("Confirmed at", When(details.ConfirmedAt ?? details.OccurredAt)));
                break;

            default:
                headline = "Order cancelled";
                intro = $"Order {reference} for {shop} was cancelled.";
                facts.Add(("Cancelled at", When(details.CancelledAt ?? details.OccurredAt)));
                if (!string.IsNullOrWhiteSpace(details.Reason))
                {
                    facts.Add(("Reason", details.Reason.Trim()));
                }
                break;
        }

        return new Content(
            headline,
            intro,
            facts,
            details.Lines ?? Array.Empty<DetailsLine>(),
            currency,
            details.Total,
            template == Template.PaymentRecorded ? details.CheckInLocation : details.CheckInLocation ?? details.CheckoutLocation,
            shop,
            agency,
            reference);
    }

    private static string Subject(Template template, OrderEventDetails details)
    {
        var reference = details.OrderReference!.Trim();
        var shop = Name(details.Shop?.Name, "shop");
        var currency = string.IsNullOrWhiteSpace(details.Currency) ? "LKR" : details.Currency.Trim();

        var subject = template switch
        {
            Template.PaymentRecorded => $"[{reference}] Payment recorded — {Money(currency, details.Payment!.Amount)} — {shop}",
            Template.OrderPlaced => $"[{reference}] Order placed — {shop}",
            Template.OrderConfirmed => $"[{reference}] Order confirmed — {shop}",
            _ => $"[{reference}] Order cancelled — {shop}"
        };

        // Headers must be one line; event text never gets to add another.
        return subject.Replace('\r', ' ').Replace('\n', ' ');
    }

    // ── HTML ────────────────────────────────────────────────────────────

    private static string Html(Content content)
    {
        var html = new StringBuilder();
        string E(string value) => WebUtility.HtmlEncode(value);

        html.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(E(content.Headline)).Append("</title></head>")
            .Append("<body style=\"margin:0;padding:0;background:#f4f6f8;font-family:Segoe UI,Arial,sans-serif;color:#0f172a;\">")
            .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#f4f6f8;padding:24px 12px;\"><tr><td align=\"center\">")
            .Append("<table role=\"presentation\" width=\"600\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:600px;width:100%;background:#ffffff;border:1px solid #e2e8f0;border-radius:12px;\">");

        // Header band
        html.Append("<tr><td style=\"background:#0d9488;color:#ffffff;padding:20px 24px;border-radius:12px 12px 0 0;\">")
            .Append("<div style=\"font-size:13px;opacity:.85;\">Sellora</div>")
            .Append("<div style=\"font-size:22px;font-weight:600;margin-top:4px;\">").Append(E(content.Headline)).Append(CloseDiv)
            .Append("<div style=\"font-size:14px;margin-top:4px;font-family:Consolas,monospace;\">").Append(E(content.Reference)).Append(CloseDiv)
            .Append(CloseRow);

        // Intro + who received it
        html.Append("<tr><td style=\"padding:20px 24px 8px;font-size:15px;line-height:1.5;\">")
            .Append(E(content.Intro))
            .Append(CloseRow)
            .Append("<tr><td style=\"padding:0 24px 16px;font-size:13px;color:#475569;line-height:1.5;\">")
            .Append(content.AgencyName.Length == 0 ? "This message was sent to <strong>" : "This same message was sent at the same moment to <strong>")
            .Append(E(content.ShopName))
            .Append(content.AgencyName.Length == 0 ? "" : "</strong> and <strong>").Append(E(content.AgencyName))
            .Append("</strong>, so both hold an identical record. Quote the order reference if you need to raise a query.")
            .Append(CloseRow);

        // Facts
        html.Append("<tr><td style=\"padding:0 24px 16px;\"><table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:14px;\">");
        foreach (var (label, value) in content.Facts)
        {
            html.Append("<tr><td style=\"padding:6px 0;color:#64748b;width:40%;border-bottom:1px solid #f1f5f9;\">").Append(E(label))
                .Append("</td><td style=\"padding:6px 0;font-weight:600;border-bottom:1px solid #f1f5f9;\">").Append(E(value)).Append(CloseRow);
        }
        html.Append("</table></td></tr>");

        // Lines
        if (content.Lines.Count > 0)
        {
            html.Append("<tr><td style=\"padding:0 24px 16px;\">")
                .Append("<div style=\"font-size:13px;font-weight:600;text-transform:uppercase;letter-spacing:.04em;color:#64748b;margin-bottom:6px;\">Items</div>")
                .Append("<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"font-size:14px;border:1px solid #e2e8f0;border-radius:8px;\">")
                .Append("<tr style=\"background:#f8fafc;color:#64748b;font-size:12px;\"><td style=\"padding:8px;\">Product</td><td style=\"padding:8px;text-align:right;\">Qty</td><td style=\"padding:8px;text-align:right;\">Unit price</td><td style=\"padding:8px;text-align:right;\">Line total</td></tr>");
            foreach (var line in content.Lines)
            {
                html.Append("<tr><td style=\"padding:8px;border-top:1px solid #e2e8f0;\">").Append(E(Name(line.ProductName, line.ProductId.ToString())))
                    .Append("</td><td style=\"padding:8px;border-top:1px solid #e2e8f0;text-align:right;\">").Append(line.Quantity.ToString(Invariant))
                    .Append("</td><td style=\"padding:8px;border-top:1px solid #e2e8f0;text-align:right;\">").Append(E(Money(content.Currency, line.UnitPrice)))
                    .Append("</td><td style=\"padding:8px;border-top:1px solid #e2e8f0;text-align:right;\">").Append(E(Money(content.Currency, line.LineTotal)))
                    .Append(CloseRow);
            }
            html.Append("<tr><td colspan=\"3\" style=\"padding:8px;border-top:1px solid #e2e8f0;text-align:right;font-weight:600;\">Total</td>")
                .Append("<td style=\"padding:8px;border-top:1px solid #e2e8f0;text-align:right;font-weight:700;\">").Append(E(Money(content.Currency, content.Total)))
                .Append("</td></tr></table></td></tr>");
        }

        // Location evidence
        if (content.Location is { } location)
        {
            var link = MapLink(location.Latitude, location.Longitude);
            html.Append("<tr><td style=\"padding:0 24px 20px;\">")
                .Append("<div style=\"border:1px solid #99f6e4;background:#f0fdfa;border-radius:8px;padding:14px 16px;font-size:14px;line-height:1.6;\">")
                .Append("<div style=\"font-weight:600;margin-bottom:4px;\">Where the rep was standing</div>")
                .Append("<div>Coordinates: <span style=\"font-family:Consolas,monospace;\">")
                .Append(Coordinate(location.Latitude)).Append(", ").Append(Coordinate(location.Longitude)).Append("</span></div>")
                .Append("<div>Distance from the shop: ").Append(E(Meters(location.DistanceMeters))).Append(CloseDiv);
            if (location.AccuracyMeters is { } accuracy)
            {
                html.Append("<div>GPS accuracy: ±").Append(E(Meters(accuracy))).Append(CloseDiv);
            }
            html.Append("<div>Checked in at: ").Append(E(When(location.CheckedInAt))).Append(CloseDiv)
                .Append("<div style=\"margin-top:10px;\"><a href=\"").Append(E(link))
                .Append("\" style=\"display:inline-block;background:#0d9488;color:#ffffff;text-decoration:none;padding:8px 14px;border-radius:6px;font-weight:600;\">Open location in Maps</a></div>")
                .Append("<div style=\"font-size:12px;color:#64748b;margin-top:6px;word-break:break-all;\">").Append(E(link)).Append(CloseDiv)
                .Append("</div></td></tr>");
        }

        html.Append("<tr><td style=\"padding:14px 24px;border-top:1px solid #e2e8f0;font-size:12px;color:#94a3b8;\">")
            .Append("Sent automatically by Sellora. Times are Sri Lanka time (UTC+05:30).")
            .Append("</td></tr></table></td></tr></table></body></html>");

        return html.ToString();
    }

    // ── Plain text (same content) ───────────────────────────────────────

    private static string Text(Content content)
    {
        var text = new StringBuilder();
        text.Append("SELLORA — ").Append(content.Headline).Append(" (").Append(content.Reference).Append(")\n\n")
            .Append(content.Intro).Append("\n\n")
            .Append(content.AgencyName.Length == 0 ? "This message was sent to " : "This same message was sent at the same moment to ")
            .Append(content.ShopName)
            .Append(content.AgencyName.Length == 0 ? ".\n\n" : $" and {content.AgencyName}, so both hold an identical record.\n\n");

        foreach (var (label, value) in content.Facts)
        {
            text.Append(label).Append(": ").Append(value).Append('\n');
        }

        if (content.Lines.Count > 0)
        {
            text.Append("\nItems\n");
            foreach (var line in content.Lines)
            {
                text.Append("- ").Append(Name(line.ProductName, line.ProductId.ToString()))
                    .Append(" x").Append(line.Quantity.ToString(Invariant))
                    .Append(" @ ").Append(Money(content.Currency, line.UnitPrice))
                    .Append(" = ").Append(Money(content.Currency, line.LineTotal)).Append('\n');
            }
            text.Append("Total: ").Append(Money(content.Currency, content.Total)).Append('\n');
        }

        if (content.Location is { } location)
        {
            text.Append("\nWhere the rep was standing\n")
                .Append("Coordinates: ").Append(Coordinate(location.Latitude)).Append(", ").Append(Coordinate(location.Longitude)).Append('\n')
                .Append("Distance from the shop: ").Append(Meters(location.DistanceMeters)).Append('\n');
            if (location.AccuracyMeters is { } accuracy)
            {
                text.Append("GPS accuracy: ±").Append(Meters(accuracy)).Append('\n');
            }
            text.Append("Checked in at: ").Append(When(location.CheckedInAt)).Append('\n')
                .Append("Map: ").Append(MapLink(location.Latitude, location.Longitude)).Append('\n');
        }

        text.Append("\nSent automatically by Sellora. Times are Sri Lanka time (UTC+05:30).\n");
        return text.ToString();
    }

    // ── US-E5-4: delivery and low stock (same layout, same render-once pipeline) ──

    private sealed record DeliveryDetails(
        string? EventType,
        string? OrderReference,
        string? PreviousStatus,
        string? Status,
        DateTimeOffset OccurredAt,
        string? Reason,
        DateTimeOffset? ScheduledFor,
        DetailsShop? Shop,
        DetailsAgency? Agency,
        string? DisputeReason,
        string? RaisedByRole);

    private static readonly Dictionary<string, (string Headline, string Intro)> DeliveryWording =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["InTransit"] = ("Delivery on its way", "The delivery for order {0} to {1} has left the agency and is on its way."),
            ["Delivered"] = ("Delivery completed", "The delivery for order {0} was delivered to {1}."),
            ["Failed"] = ("Delivery failed", "The delivery for order {0} to {1} could not be completed."),
            ["Cancelled"] = ("Delivery cancelled", "The delivery for order {0} to {1} was cancelled.")
        };

    /// <summary>Reads the stored delivery event; it must carry an order reference.</summary>
    private static (DeliveryDetails Details, string Reference) ReadDelivery(string payload)
    {
        DeliveryDetails details;

        try
        {
            details = JsonSerializer.Deserialize<DeliveryDetails>(payload, Json)
                ?? throw new NotificationRenderException("The stored event is empty.");
        }
        catch (JsonException exception)
        {
            throw new NotificationRenderException($"The stored event could not be read: {exception.Message}");
        }

        if (string.IsNullOrWhiteSpace(details.OrderReference))
        {
            throw new NotificationRenderException("The stored delivery event has no order reference.");
        }

        return (details, details.OrderReference.Trim());
    }

    private static RenderedMessage RenderDelivery(string templateKey, string payload)
    {
        var (details, reference) = ReadDelivery(payload);
        var shop = Name(details.Shop?.Name, "the shop");
        var agency = Name(details.Agency?.Name, "the agency");
        var facts = new List<(string, string)> { ("Order reference", reference), ("Shop", shop), (AgencyLabel, agency) };
        string headline, intro, subject;

        if (templateKey == "delivery-disputed.v1")
        {
            headline = "Delivery disputed";
            intro = $"The delivery for order {reference} to {shop} has been disputed.";
            facts.Add(("Disputed at", When(details.OccurredAt)));
            if (!string.IsNullOrWhiteSpace(details.RaisedByRole)) facts.Add(("Raised by", details.RaisedByRole.Trim()));
            if (!string.IsNullOrWhiteSpace(details.DisputeReason)) facts.Add(("Reason", details.DisputeReason.Trim()));
            subject = $"[{reference}] Delivery disputed — {shop}";
        }
        else
        {
            var status = (details.Status ?? string.Empty).Trim();

            if (!DeliveryWording.TryGetValue(status, out var wording))
            {
                throw new NotificationRenderException($"'{status}' is not a delivery status that is notified.");
            }

            headline = wording.Headline;
            intro = string.Format(Invariant, wording.Intro, reference, shop);
            facts.Add(("Status", status + (string.IsNullOrWhiteSpace(details.PreviousStatus) ? "" : $" (was {details.PreviousStatus.Trim()})")));
            facts.Add(("Updated at", When(details.OccurredAt)));
            if (details.ScheduledFor is { } scheduled) facts.Add(("Scheduled for", When(scheduled)));
            if (!string.IsNullOrWhiteSpace(details.Reason)) facts.Add(("Reason", details.Reason.Trim()));
            subject = $"[{reference}] {headline} — {shop}";
        }

        var content = new Content(headline, intro, facts, Array.Empty<DetailsLine>(), "LKR", 0, null, shop, agency, reference);
        return Finish(Clean(subject), content);
    }

    private sealed record LowStockDetails(
        Guid ProductId,
        int AvailableQuantity,
        int ReorderThreshold,
        DateTimeOffset DetectedAt,
        string? OwnerType,
        string? OwnerDisplayName);

    /// <summary>What the intake looked up for a low-stock message.</summary>
    public sealed record LowStockContext(string? ProductName, string? AgencyName, string? OwnerDisplayName);

    private static RenderedMessage RenderLowStock(string payload, string? context)
    {
        LowStockDetails details;
        LowStockContext? names;

        try
        {
            details = JsonSerializer.Deserialize<LowStockDetails>(payload, Json)
                ?? throw new NotificationRenderException("The stored event is empty.");
            names = string.IsNullOrWhiteSpace(context) ? null : JsonSerializer.Deserialize<LowStockContext>(context, Json);
        }
        catch (JsonException exception)
        {
            throw new NotificationRenderException($"The stored event could not be read: {exception.Message}");
        }

        var product = Name(names?.ProductName, $"Product {details.ProductId}");
        var holder = Name(names?.OwnerDisplayName ?? details.OwnerDisplayName, "the stock holder");
        var holderKind = details.OwnerType switch
        {
            AgencyLabel => AgencyLabel,
            "SalesRep" => "Sales rep's van",
            "Company" => "Company warehouse",
            _ => "Held by"
        };

        var facts = new List<(string, string)>
        {
            ("Product", product),
            (holderKind, holder),
            ("Available now", details.AvailableQuantity.ToString(Invariant)),
            ("Reorder threshold", details.ReorderThreshold.ToString(Invariant)),
            ("Detected at", When(details.DetectedAt))
        };

        var agencyName = names?.AgencyName;
        var reference = $"STOCK-{details.DetectedAt.UtcDateTime:yyMMdd}";
        var content = new Content(
            "Low stock",
            $"{product} is running low: {details.AvailableQuantity} available at {holder}, below the reorder threshold of {details.ReorderThreshold}.",
            facts,
            Array.Empty<DetailsLine>(),
            "LKR",
            0,
            null,
            string.IsNullOrWhiteSpace(agencyName) ? "the company admin" : agencyName.Trim(),
            string.IsNullOrWhiteSpace(agencyName) ? "" : "the company admin",
            reference);

        return Finish(Clean($"[Low stock] {product} — {details.AvailableQuantity} left at {holder}"), content);
    }

    private static RenderedMessage Finish(string subject, Content content)
    {
        var html = Html(content);
        var text = Text(content);
        return new RenderedMessage(subject, html, text, Sha256(subject, html, text));
    }

    private static string Clean(string subject) => subject.Replace('\r', ' ').Replace('\n', ' ');

    // ── Formatting ──────────────────────────────────────────────────────

    private static string Name(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Replace('\r', ' ').Replace('\n', ' ');

    private static string Money(string currency, decimal amount) =>
        $"{currency} {amount.ToString("N2", Invariant)}";

    private static string Meters(double meters) => $"{meters.ToString("0.#", Invariant)} m";

    /// <summary>"26 Sep 2026, 10:00:00 (UTC 04:30:00Z)": local for people, UTC to compare records exactly.</summary>
    private static string When(DateTimeOffset value)
    {
        var local = value.ToOffset(SriLankaOffset);
        var utc = value.ToUniversalTime();
        return $"{local.ToString("dd MMM yyyy, HH:mm:ss", Invariant)} (UTC {utc.ToString("yyyy-MM-dd HH:mm:ss", Invariant)}Z)";
    }
}
