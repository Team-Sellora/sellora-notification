using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-2: the request's dispatch state rules, no database.</summary>
public sealed class NotificationDispatchStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 26, 4, 30, 0, TimeSpan.Zero);
    private static readonly RenderedMessage Message = new("[ORD-1] Payment recorded", "<p>x</p>", "x", "abc");

    private static NotificationRequest Request(string? shopEmail = "shop@x.lk", string? agencyEmail = "agency@x.lk") =>
        NotificationRequest.CreatePending(
            Guid.NewGuid(), Guid.NewGuid(), "PaymentRecorded", "payment-recorded.v1", Guid.NewGuid(), "ORD-1", "{}",
            null, Now, Now, new EventSource("t", 0, 0),
            new[]
            {
                new NewRecipient(RecipientKind.Shop, Guid.NewGuid(), "Shop", shopEmail),
                new NewRecipient(RecipientKind.Agency, Guid.NewGuid(), "Agency", agencyEmail)
            });

    private static NotificationRecipient Of(NotificationRequest request, RecipientKind kind) =>
        request.Recipients.Single(recipient => recipient.Kind == kind);

    private static RecipientSendResult Ok(NotificationRecipient recipient, DateTimeOffset at) =>
        new(recipient.NotificationRecipientId, true, at, "queued", null);

    private static RecipientSendResult Fail(NotificationRecipient recipient, DateTimeOffset at) =>
        new(recipient.NotificationRecipientId, false, at, null, "Mailbox unavailable");

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMinutes(attempt);

    [Fact]
    public void Both_delivered_is_Sent_with_the_measured_gap()
    {
        var request = Request();
        var shop = Of(request, RecipientKind.Shop);
        var agency = Of(request, RecipientKind.Agency);

        request.RecordDispatch(new[] { Ok(shop, Now), Ok(agency, Now.AddMilliseconds(37)) }, Now.AddMilliseconds(40), Backoff);

        Assert.Equal(NotificationStatus.Sent, request.Status);
        Assert.Equal(37, request.SendGapMilliseconds);
        Assert.Null(request.NextAttemptAt);
        Assert.Equal(Now.AddMilliseconds(40), request.CompletedAt);
        Assert.Empty(request.RecipientsToSend());
    }

    // Scenario 3 on the aggregate.
    [Fact]
    public void One_failure_is_PartiallySent_and_only_the_failed_recipient_is_owed_a_retry()
    {
        var request = Request();
        var shop = Of(request, RecipientKind.Shop);
        var agency = Of(request, RecipientKind.Agency);

        request.RecordDispatch(new[] { Ok(shop, Now), Fail(agency, Now) }, Now, Backoff);

        Assert.Equal(NotificationStatus.PartiallySent, request.Status);
        Assert.Equal(agency.NotificationRecipientId, Assert.Single(request.RecipientsToSend()).NotificationRecipientId);
        Assert.Equal(Now.AddMinutes(1), request.NextAttemptAt);
        Assert.Null(request.SendGapMilliseconds);

        // The retry succeeds: Sent, and the gap now honestly shows the delay.
        request.RecordDispatch(new[] { Ok(agency, Now.AddMinutes(1)) }, Now.AddMinutes(1), Backoff);

        Assert.Equal(NotificationStatus.Sent, request.Status);
        Assert.Equal(60_000, request.SendGapMilliseconds);
        Assert.Equal(Now, shop.SentAt);
        Assert.Equal(1, shop.Attempts);
        Assert.Equal(2, agency.Attempts);
    }

    [Fact]
    public void A_late_result_never_overwrites_a_delivery()
    {
        var request = Request();
        var shop = Of(request, RecipientKind.Shop);
        request.RecordDispatch(new[] { Ok(shop, Now) }, Now, Backoff);

        request.RecordDispatch(new[] { Fail(shop, Now.AddMinutes(1)) }, Now.AddMinutes(1), Backoff);

        Assert.Equal(RecipientDeliveryStatus.Sent, shop.DeliveryStatus);
        Assert.Equal(Now, shop.SentAt);
    }

    [Fact]
    public void Nobody_delivered_stays_Pending_with_a_backed_off_retry()
    {
        var request = Request();

        request.RecordDispatch(
            new[] { Fail(Of(request, RecipientKind.Shop), Now), Fail(Of(request, RecipientKind.Agency), Now) }, Now, Backoff);
        request.RecordDispatch(
            new[] { Fail(Of(request, RecipientKind.Shop), Now), Fail(Of(request, RecipientKind.Agency), Now) }, Now, Backoff);

        Assert.Equal(NotificationStatus.Pending, request.Status);
        Assert.Equal(2, request.AttemptCount);
        Assert.Equal(Now.AddMinutes(2), request.NextAttemptAt);
    }

    [Fact]
    public void A_recipient_without_an_address_is_not_retried_forever()
    {
        var request = Request(agencyEmail: null);
        request.MarkUnaddressedRecipients();

        Assert.Equal(RecipientKind.Shop, Assert.Single(request.RecipientsToSend()).Kind);

        request.RecordDispatch(new[] { Ok(Of(request, RecipientKind.Shop), Now) }, Now, Backoff);

        Assert.Equal(NotificationStatus.PartiallySent, request.Status);
        Assert.Equal(RecipientDeliveryStatus.Unaddressed, Of(request, RecipientKind.Agency).DeliveryStatus);
        Assert.Null(request.NextAttemptAt); // nothing left that can be sent
    }

    [Fact]
    public void A_request_is_rendered_only_once()
    {
        var request = Request();
        request.AttachRendering(Message, Now);

        Assert.Throws<InvalidOperationException>(() => request.AttachRendering(Message with { Html = "<p>other</p>" }, Now));
        Assert.Equal("<p>x</p>", request.RenderedHtml);
    }
}
