using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Infrastructure.Dispatch;
using Sellora.NotificationService.Infrastructure.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-2 dispatch against a real database, with a recording mail sender.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationDispatcherTests
{
    private const string ShopEmail = "owner@lakshmi-stores.lk";
    private const string AgencyEmail = "orders@colombo-agency.lk";

    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 26, 4, 31, 0, TimeSpan.Zero));
    private readonly RecordingEmailSender _mail = new();

    public NotificationDispatcherTests(PostgreSqlFixture fixture) => _fixture = fixture;

    /// <summary>A PaymentRecorded request, stored the way US-E5-1 stores it.</summary>
    private async Task<Guid> SeedPaymentAsync()
    {
        await using var db = _fixture.CreateContext(null);
        var result = await new NotificationIntakeService(db, _clock, NullLogger<NotificationIntakeService>.Instance)
            .AcceptAsync(TestEvents.Message(TestEvents.Order("PaymentRecorded", companyId: _companyId)), CancellationToken.None);
        return result.NotificationRequestId!.Value;
    }

    private async Task<int> DispatchAsync(IEmailSender? sender = null)
    {
        await using var db = _fixture.CreateContext(null);
        var dispatcher = new NotificationDispatcher(
            db, sender ?? _mail, _clock, Options.Create(new DispatchOptions { BatchSize = 100 }),
            NullLogger<NotificationDispatcher>.Instance);
        return await dispatcher.DispatchDueAsync(CancellationToken.None);
    }

    private async Task<NotificationRequest> ReloadAsync(Guid id)
    {
        await using var db = _fixture.CreateContext(null);
        return await db.NotificationRequests.IgnoreQueryFilters().Include(request => request.Recipients)
            .SingleAsync(request => request.NotificationRequestId == id);
    }

    private IReadOnlyList<OutgoingEmail> SentFor(NotificationRequest request) =>
        _mail.Sent.Where(email => email.Headers[NotificationDispatcher.NotificationIdHeader] == request.NotificationRequestId.ToString()).ToList();

    // Scenario 1 + DoD 1, 3, 5.
    [Fact]
    public async Task Both_parties_get_the_identical_message_concurrently_and_the_gap_is_recorded()
    {
        var id = await SeedPaymentAsync();
        _mail.RequireConcurrent(2); // would time out if the two sends were sequential

        await DispatchAsync();

        var stored = await ReloadAsync(id);
        var sent = SentFor(stored);

        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(new[] { AgencyEmail, ShopEmail }, sent.Select(email => email.ToAddress).OrderBy(address => address));
        Assert.Single(sent.Select(email => (email.Subject, email.Html, email.Text)).Distinct()); // identical apart from To
        Assert.StartsWith("[ORD-260926-K7MQ4R]", sent[0].Subject);

        // DoD 5: the stored body is the body that went out.
        Assert.Equal(stored.RenderedHtml, sent[0].Html);
        Assert.Equal(stored.RenderedText, sent[0].Text);
        Assert.Equal(stored.RenderedSubject, sent[0].Subject);
        Assert.Equal(stored.RenderedBodySha256, sent[0].Headers[NotificationDispatcher.BodyHashHeader]);

        // DoD 3: both timestamps and the gap, within tolerance.
        Assert.All(stored.Recipients, recipient => Assert.Equal(_clock.Now, recipient.SentAt));
        Assert.Equal(0, stored.SendGapMilliseconds);
        Assert.True(stored.SendGapMilliseconds <= new DispatchOptions().SimultaneityToleranceMilliseconds);
    }

    // Scenario 3.
    [Fact]
    public async Task An_unreachable_agency_leaves_PartiallySent_and_the_retry_goes_to_the_agency_only()
    {
        var id = await SeedPaymentAsync();
        _mail.Unreachable.Add(AgencyEmail);

        await DispatchAsync();

        var afterFirst = await ReloadAsync(id);
        Assert.Equal(NotificationStatus.PartiallySent, afterFirst.Status);
        Assert.Equal(RecipientDeliveryStatus.Failed, afterFirst.Recipients.Single(r => r.Kind == RecipientKind.Agency).DeliveryStatus);
        Assert.Contains("Mailbox unavailable", afterFirst.Recipients.Single(r => r.Kind == RecipientKind.Agency).LastError);
        Assert.Equal(new[] { ShopEmail }, SentFor(afterFirst).Select(email => email.ToAddress));

        // Not due yet: nothing happens.
        await DispatchAsync();
        Assert.Single(SentFor(afterFirst));

        // Due, and the agency is back.
        _mail.Unreachable.Clear();
        _clock.Advance(TimeSpan.FromMinutes(2));
        await DispatchAsync();

        var afterRetry = await ReloadAsync(id);
        var sent = SentFor(afterRetry);
        Assert.Equal(NotificationStatus.Sent, afterRetry.Status);
        Assert.Equal(new[] { ShopEmail, AgencyEmail }, sent.Select(email => email.ToAddress)); // shop once, agency once
        Assert.Equal(sent[0].Html, sent[1].Html); // the retry sent the stored body, not a re-render
        Assert.Equal(1, afterRetry.Recipients.Single(r => r.Kind == RecipientKind.Shop).Attempts);
        Assert.Equal(2, afterRetry.Recipients.Single(r => r.Kind == RecipientKind.Agency).Attempts);
        Assert.Equal(120_000, afterRetry.SendGapMilliseconds); // honest: this one was not simultaneous
    }

    [Fact]
    public async Task Two_dispatchers_running_at_once_send_each_request_once()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add(await SeedPaymentAsync());
        }

        await Task.WhenAll(DispatchAsync(), DispatchAsync());

        foreach (var id in ids)
        {
            var stored = await ReloadAsync(id);
            Assert.Equal(NotificationStatus.Sent, stored.Status);
            Assert.Equal(2, SentFor(stored).Count);
        }
    }

    [Fact]
    public async Task A_request_without_an_agency_email_is_sent_to_the_shop_and_not_retried()
    {
        await using (var db = _fixture.CreateContext(null))
        {
            await new NotificationIntakeService(db, _clock, NullLogger<NotificationIntakeService>.Instance).AcceptAsync(
                TestEvents.Message(TestEvents.Order("PaymentRecorded", companyId: _companyId, agencyEmail: null)),
                CancellationToken.None);
        }

        await DispatchAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        await DispatchAsync();

        await using var check = _fixture.CreateContext(null);
        var stored = await check.NotificationRequests.IgnoreQueryFilters().Include(r => r.Recipients)
            .Where(r => r.CompanyId == _companyId)
            .SingleAsync(r => r.Recipients.Any(recipient => recipient.Email == null));

        Assert.Equal(NotificationStatus.PartiallySent, stored.Status);
        Assert.Equal(RecipientDeliveryStatus.Unaddressed, stored.Recipients.Single(r => r.Kind == RecipientKind.Agency).DeliveryStatus);
        Assert.Null(stored.NextAttemptAt);
        Assert.Equal(1, stored.Recipients.Single(r => r.Kind == RecipientKind.Shop).Attempts); // not picked up again
    }

    [Fact]
    public async Task The_rendered_message_can_be_reproduced_through_the_read_api()
    {
        var id = await SeedPaymentAsync();
        await DispatchAsync();

        await using var db = _fixture.CreateContext(_companyId);
        var reader = new NotificationRequestReader(db, Options.Create(new DispatchOptions()));
        var rendered = await reader.GetRenderedAsync(id, CancellationToken.None);
        var summary = await reader.GetAsync(id, CancellationToken.None);

        var sent = SentFor(await ReloadAsync(id));
        Assert.Equal(sent[0].Html, rendered!.Html);
        Assert.Equal(sent[0].Subject, rendered.Subject);
        Assert.Equal("Sent", summary!.Status);
        Assert.True(summary.Dispatch.WithinTolerance);
        Assert.All(summary.Recipients, recipient => Assert.Equal("Sent", recipient.DeliveryStatus));
    }
}
