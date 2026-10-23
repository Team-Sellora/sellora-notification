using System.Net.Sockets;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Infrastructure.Email;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-3-T1/T2: back-off, the attempt cap, and transient vs permanent — no database.</summary>
public sealed class RetryAndClassificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 4, 0, 0, TimeSpan.Zero);

    private static NotificationRequest Request(string? agencyEmail = "agency@x.lk") =>
        NotificationRequest.CreatePending(
            Guid.NewGuid(), Guid.NewGuid(), "PaymentRecorded", "payment-recorded.v1", Guid.NewGuid(), "ORD-1", "{}",
            null, Now, Now, new EventSource("t", 0, 0),
            new[]
            {
                new NewRecipient(RecipientKind.Shop, Guid.NewGuid(), "Shop", "shop@x.lk"),
                new NewRecipient(RecipientKind.Agency, Guid.NewGuid(), "Agency", agencyEmail)
            });

    private static NotificationRecipient Of(NotificationRequest request, RecipientKind kind) =>
        request.Recipients.Single(recipient => recipient.Kind == kind);

    private static RecipientSendResult Result(NotificationRecipient recipient, SendOutcome outcome, DateTimeOffset at) =>
        new(recipient.NotificationRecipientId, outcome, at, outcome == SendOutcome.Sent ? "250 OK" : "451 try later",
            outcome == SendOutcome.Sent ? null : "Provider unavailable");

    private static readonly RetryPolicy Policy = new DispatchOptions().Policy(() => 0.5); // no jitter

    // ── T1: exponential back-off with jitter and a cap ───────────────────

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 240)]
    [InlineData(10, 1800)] // capped at RetryMaxSeconds
    public void Delays_double_from_30_seconds_up_to_the_cap(int failures, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), new DispatchOptions().RetryDelay(failures, 0.5));
    }

    [Fact]
    public void Jitter_spreads_retries_within_plus_or_minus_20_percent()
    {
        var options = new DispatchOptions();

        Assert.Equal(TimeSpan.FromSeconds(48), options.RetryDelay(2, 0.0));    // −20 %
        Assert.Equal(TimeSpan.FromSeconds(72), options.RetryDelay(2, 1.0));    // +20 %
        Assert.InRange(options.RetryDelay(2, Random.Shared.NextDouble()).TotalSeconds, 48, 72);
    }

    // Scenario 1: retries with growing intervals, then succeeds, every attempt recorded.
    [Fact]
    public void A_transient_failure_retries_with_growing_intervals_and_then_succeeds()
    {
        var request = Request();
        var shop = Of(request, RecipientKind.Shop);
        var agency = Of(request, RecipientKind.Agency);
        var at = Now;

        request.RecordDispatch(new[] { Result(shop, SendOutcome.TransientFailure, at), Result(agency, SendOutcome.TransientFailure, at) }, at, Policy);
        Assert.Equal(NotificationStatus.Failed, request.Status);
        Assert.Equal(at.AddSeconds(30), request.NextAttemptAt);

        at = request.NextAttemptAt!.Value;
        request.RecordDispatch(new[] { Result(shop, SendOutcome.TransientFailure, at), Result(agency, SendOutcome.TransientFailure, at) }, at, Policy);
        Assert.Equal(at.AddSeconds(60), request.NextAttemptAt); // doubled

        at = request.NextAttemptAt!.Value;
        request.RecordDispatch(new[] { Result(shop, SendOutcome.Sent, at), Result(agency, SendOutcome.Sent, at) }, at, Policy);

        Assert.Equal(NotificationStatus.Sent, request.Status);
        Assert.Equal(6, request.AttemptHistory.Count); // 3 attempts × 2 recipients
        Assert.Equal(
            new[] { SendOutcome.TransientFailure, SendOutcome.TransientFailure, SendOutcome.Sent },
            request.AttemptHistory.Where(a => a.RecipientKind == RecipientKind.Shop).OrderBy(a => a.AttemptNumber).Select(a => a.Outcome));
        Assert.All(request.AttemptHistory, attempt => Assert.NotNull(attempt.ProviderResponse));
    }

    [Fact]
    public void After_the_capped_attempts_a_transient_failure_is_dead_lettered()
    {
        var request = Request();
        var shop = Of(request, RecipientKind.Shop);
        var agency = Of(request, RecipientKind.Agency);

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var at = Now.AddHours(attempt);
            request.RecordDispatch(new[] { Result(shop, SendOutcome.Sent, at), Result(agency, SendOutcome.TransientFailure, at) }
                .Where(result => request.RecipientsToSend().Any(r => r.NotificationRecipientId == result.NotificationRecipientId))
                .ToList(), at, Policy);
        }

        Assert.Equal(NotificationStatus.PermanentlyFailed, request.Status);
        Assert.Equal(RecipientDeliveryStatus.PermanentlyFailed, agency.DeliveryStatus);
        Assert.Equal(5, agency.Attempts);
        Assert.StartsWith("Gave up after 5 attempts", agency.LastError);
        Assert.Null(request.NextAttemptAt);
        Assert.Equal(1, shop.Attempts); // never re-sent
    }

    // Scenario 3 / Q2: a permanent failure skips the budget.
    [Fact]
    public void A_permanent_failure_stops_after_one_attempt()
    {
        var request = Request();
        var shop = Of(request, RecipientKind.Shop);
        var agency = Of(request, RecipientKind.Agency);

        request.RecordDispatch(new[] { Result(shop, SendOutcome.Sent, Now), Result(agency, SendOutcome.PermanentFailure, Now) }, Now, Policy);

        Assert.Equal(NotificationStatus.PermanentlyFailed, request.Status);
        Assert.Equal(1, agency.Attempts);
        Assert.Equal(0, agency.FailuresSinceReset);
        Assert.Null(request.NextAttemptAt);
        Assert.Empty(request.RecipientsToSend());
    }

    // ── T4: resend ───────────────────────────────────────────────────────

    [Fact]
    public void Resend_requeues_only_the_unsent_recipient_at_the_corrected_address_with_a_fresh_budget()
    {
        var request = Request(agencyEmail: "agency@@broken");
        var shop = Of(request, RecipientKind.Shop);
        var agency = Of(request, RecipientKind.Agency);
        request.RecordDispatch(new[] { Result(shop, SendOutcome.Sent, Now), Result(agency, SendOutcome.PermanentFailure, Now) }, Now, Policy);

        request.PrepareResend(new Dictionary<RecipientKind, string> { [RecipientKind.Agency] = "orders@agency.lk" }, "admin-sub", Now.AddHours(1));

        Assert.Equal(NotificationStatus.PartiallySent, request.Status);
        Assert.Equal("orders@agency.lk", agency.Email);
        Assert.Equal(RecipientDeliveryStatus.Pending, agency.DeliveryStatus);
        Assert.Equal(RecipientDeliveryStatus.Sent, shop.DeliveryStatus);
        Assert.Equal(agency.NotificationRecipientId, Assert.Single(request.RecipientsToSend()).NotificationRecipientId);
        Assert.Equal("admin-sub", request.LastResendBy);
        Assert.Single(request.AttemptHistory, a => a.RecipientKind == RecipientKind.Agency); // history kept

        request.RecordDispatch(new[] { Result(agency, SendOutcome.Sent, Now.AddHours(1)) }, Now.AddHours(1), Policy,
            AttemptTrigger.ManualResend, "admin-sub");

        Assert.Equal(NotificationStatus.Sent, request.Status);
        var resent = request.AttemptHistory.Where(a => a.RecipientKind == RecipientKind.Agency).OrderBy(a => a.AttemptNumber).Last();
        Assert.Equal(2, resent.AttemptNumber);
        Assert.Equal(AttemptTrigger.ManualResend, resent.Trigger);
        Assert.Equal("orders@agency.lk", resent.EmailAddress);
    }

    [Fact]
    public void A_fully_sent_notification_cannot_be_resent()
    {
        var request = Request();
        request.RecordDispatch(request.Recipients.Select(r => Result(r, SendOutcome.Sent, Now)).ToList(), Now, Policy);

        Assert.Throws<InvalidOperationException>(() =>
            request.PrepareResend(new Dictionary<RecipientKind, string>(), "admin", Now));
    }

    [Fact]
    public void A_missing_address_needs_one_on_resend()
    {
        var request = Request(agencyEmail: null);
        request.MarkUnaddressedRecipients();
        request.RecordDispatch(new[] { Result(Of(request, RecipientKind.Shop), SendOutcome.Sent, Now) }, Now, Policy);

        Assert.Throws<InvalidOperationException>(() => request.PrepareResend(new Dictionary<RecipientKind, string>(), "admin", Now));

        request.PrepareResend(new Dictionary<RecipientKind, string> { [RecipientKind.Agency] = "a@agency.lk" }, "admin", Now);
        Assert.Equal("a@agency.lk", Assert.Single(request.RecipientsToSend()).Email);
    }

    // ── T2: classification ──────────────────────────────────────────────

    [Theory]
    [InlineData("owner@shop.lk", true)]
    [InlineData("first.last+tag@mail.example.co", true)]
    [InlineData("not-an-email", false)]
    [InlineData("a@b", false)]
    [InlineData("two@@signs.lk", false)]
    [InlineData("space in@shop.lk", false)]
    [InlineData("dot@.shop.lk", false)]
    [InlineData("", false)]
    public void Addresses_are_checked_for_shape(string address, bool valid)
    {
        Assert.Equal(valid, EmailAddressRules.IsWellFormed(address));
    }

    public static TheoryData<Exception, SendOutcome> Failures => new()
    {
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 no such user"), SendOutcome.PermanentFailure },
        { new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, SmtpStatusCode.TransactionFailed, "554 rejected"), SendOutcome.PermanentFailure },
        { new SmtpCommandException(SmtpErrorCode.RecipientNotAccepted, SmtpStatusCode.MailboxBusy, "450 greylisted"), SendOutcome.TransientFailure },
        { new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, SmtpStatusCode.ServiceNotAvailable, "421 too many connections"), SendOutcome.TransientFailure },
        { new SmtpCommandException(SmtpErrorCode.SenderNotAccepted, SmtpStatusCode.MailboxUnavailable, "550 sender not verified"), SendOutcome.TransientFailure },
        { new AuthenticationException("535 bad credentials"), SendOutcome.TransientFailure },
        { new SocketException((int)SocketError.ConnectionRefused), SendOutcome.TransientFailure },
        { new TimeoutException(), SendOutcome.TransientFailure },
        { new IOException("reset"), SendOutcome.TransientFailure },
        { new ParseException("bad address", 0, 0), SendOutcome.PermanentFailure }
    };

    [Theory]
    [MemberData(nameof(Failures), DisableDiscoveryEnumeration = true)]
    public void Smtp_failures_are_classified(Exception exception, SendOutcome expected)
    {
        Assert.Equal(expected, SmtpFailureClassifier.Classify(exception).Outcome);
    }

    [Fact]
    public async Task A_malformed_address_is_refused_before_any_connection()
    {
        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions { Host = "unreachable.invalid", Port = 1 }));

        var error = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("not-an-email"), CancellationToken.None));

        Assert.Equal(SendOutcome.PermanentFailure, error.Outcome);
    }

    [Fact]
    public async Task A_simulated_outage_is_a_transient_failure()
    {
        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions { SimulateOutage = true }));

        var error = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("owner@shop.lk"), CancellationToken.None));

        Assert.Equal(SendOutcome.TransientFailure, error.Outcome);
    }

    [Fact]
    public async Task An_unreachable_provider_is_a_transient_failure()
    {
        var sender = new SmtpEmailSender(Options.Create(new SmtpOptions { Host = "127.0.0.1", Port = 1, TimeoutSeconds = 5 }));

        var error = await Assert.ThrowsAsync<EmailSendException>(() => sender.SendAsync(Email("owner@shop.lk"), CancellationToken.None));

        Assert.Equal(SendOutcome.TransientFailure, error.Outcome);
    }

    private static OutgoingEmail Email(string to) =>
        new(to, "Owner", "[ORD-1] Payment recorded", "<p>x</p>", "x", new Dictionary<string, string>());
}
