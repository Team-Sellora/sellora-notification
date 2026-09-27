using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Application.Identity;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Infrastructure.Dispatch;
using Sellora.NotificationService.Infrastructure.Notifications;
using Sellora.NotificationService.Infrastructure.Persistence;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-3 against a real database: retries, dead-lettering, attempt history, resend, health counts.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationFailureHandlingTests
{
    private const string ShopEmail = "owner@lakshmi-stores.lk";
    private const string AgencyEmail = "orders@colombo-agency.lk";

    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 28, 4, 0, 0, TimeSpan.Zero));
    private readonly ScriptedEmailSender _mail = new();

    public NotificationFailureHandlingTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private async Task<Guid> SeedAsync(string? agencyEmail = AgencyEmail)
    {
        await using var db = _fixture.CreateContext(null);
        var result = await new NotificationIntakeService(db, _clock, NullLogger<NotificationIntakeService>.Instance).AcceptAsync(
            TestEvents.Message(TestEvents.Order("PaymentRecorded", companyId: _companyId, agencyEmail: agencyEmail)),
            CancellationToken.None);
        return result.NotificationRequestId!.Value;
    }

    private NotificationDispatcher Dispatcher(NotificationDbContext db) => new(
        db, _mail, _clock, Options.Create(new DispatchOptions { BatchSize = 100 }),
        NullLogger<NotificationDispatcher>.Instance, () => 0.5); // no jitter: exact delays

    private async Task DispatchDueAsync()
    {
        await using var db = _fixture.CreateContext(null);
        await Dispatcher(db).DispatchDueAsync(CancellationToken.None);
    }

    private async Task<NotificationRequest> ReloadAsync(Guid id)
    {
        await using var db = _fixture.CreateContext(null);
        return await db.NotificationRequests.IgnoreQueryFilters()
            .Include(request => request.Recipients)
            .Include(request => request.AttemptHistory)
            .SingleAsync(request => request.NotificationRequestId == id);
    }

    private async Task<ResendResult> ResendAsync(Guid id, params ResendRecipientAddress[] addresses)
    {
        await using var db = _fixture.CreateContext(_companyId);
        var reader = new NotificationRequestReader(db, Options.Create(new DispatchOptions()));
        var service = new NotificationResendService(
            db, new TenantStub(_companyId), new Caller("admin-sub"), Dispatcher(db), reader, _clock,
            NullLogger<NotificationResendService>.Instance);
        return await service.ResendAsync(id, new ResendNotificationRequest(addresses), CancellationToken.None);
    }

    // Scenario 1 / Q1: transient outage → growing intervals → recovers, every attempt recorded.
    [Fact]
    public async Task A_provider_outage_is_retried_with_growing_intervals_and_recovers()
    {
        var id = await SeedAsync();
        _mail.Down = true;

        await DispatchDueAsync();
        var first = await ReloadAsync(id);
        Assert.Equal(NotificationStatus.Failed, first.Status);
        Assert.Equal(_clock.Now.AddSeconds(30), first.NextAttemptAt);

        _clock.Now = first.NextAttemptAt!.Value;
        await DispatchDueAsync();
        var second = await ReloadAsync(id);
        Assert.Equal(_clock.Now.AddSeconds(60), second.NextAttemptAt); // doubled

        _mail.Down = false;
        _clock.Now = second.NextAttemptAt!.Value;
        await DispatchDueAsync();

        var recovered = await ReloadAsync(id);
        Assert.Equal(NotificationStatus.Sent, recovered.Status);
        Assert.Equal(6, recovered.AttemptHistory.Count);
        Assert.All(recovered.AttemptHistory, attempt => Assert.NotNull(attempt.ProviderResponse));
        Assert.Equal(
            new[] { SendOutcome.TransientFailure, SendOutcome.TransientFailure, SendOutcome.Sent },
            recovered.AttemptHistory.Where(a => a.RecipientKind == RecipientKind.Agency).OrderBy(a => a.AttemptNumber).Select(a => a.Outcome));
    }

    [Fact]
    public async Task A_long_outage_dead_letters_after_five_attempts()
    {
        var id = await SeedAsync();
        _mail.Down = true;

        for (var attempt = 0; attempt < 6; attempt++)
        {
            await DispatchDueAsync();
            _clock.Advance(TimeSpan.FromHours(1));
        }

        var stored = await ReloadAsync(id);
        Assert.Equal(NotificationStatus.PermanentlyFailed, stored.Status);
        Assert.All(stored.Recipients, recipient => Assert.Equal(5, recipient.Attempts)); // capped, not 6
        Assert.StartsWith("Gave up after 5 attempts", stored.FailureReason);
        Assert.Null(stored.NextAttemptAt);
    }

    // Q2: a permanent failure skips retries.
    [Fact]
    public async Task An_invalid_address_goes_straight_to_PermanentlyFailed()
    {
        var id = await SeedAsync(agencyEmail: "not-an-email");

        await DispatchDueAsync();
        _clock.Advance(TimeSpan.FromHours(1));
        await DispatchDueAsync();

        var stored = await ReloadAsync(id);
        var agency = stored.Recipients.Single(r => r.Kind == RecipientKind.Agency);
        Assert.Equal(NotificationStatus.PermanentlyFailed, stored.Status);
        Assert.Equal(RecipientDeliveryStatus.PermanentlyFailed, agency.DeliveryStatus);
        Assert.Equal(1, agency.Attempts); // not retried
        Assert.Equal(SendOutcome.PermanentFailure, stored.AttemptHistory.Single(a => a.RecipientKind == RecipientKind.Agency).Outcome);
        Assert.Equal(new[] { ShopEmail }, _mail.Delivered.Select(email => email.ToAddress)); // the shop still got it
    }

    // Scenario 3 / Q4: resend at the corrected address, recorded as a new attempt.
    [Fact]
    public async Task Resend_at_a_corrected_address_succeeds_and_is_recorded_as_a_new_attempt()
    {
        var id = await SeedAsync(agencyEmail: "not-an-email");
        await DispatchDueAsync();

        var result = await ResendAsync(id, new ResendRecipientAddress("Agency", AgencyEmail));

        Assert.Equal(ResendOutcome.Succeeded, result.Outcome);
        Assert.Equal("Sent", result.Request!.Status);

        var stored = await ReloadAsync(id);
        var agencyAttempts = stored.AttemptHistory.Where(a => a.RecipientKind == RecipientKind.Agency).OrderBy(a => a.AttemptNumber).ToList();
        Assert.Equal(2, agencyAttempts.Count); // the failure is kept, not overwritten
        Assert.Equal(AttemptTrigger.ManualResend, agencyAttempts[1].Trigger);
        Assert.Equal("admin-sub", agencyAttempts[1].TriggeredBy);
        Assert.Equal(AgencyEmail, agencyAttempts[1].EmailAddress);
        Assert.Equal(1, _mail.Delivered.Count(email => email.ToAddress == ShopEmail)); // shop not re-sent
        Assert.Equal("admin-sub", stored.LastResendBy);
    }

    [Fact]
    public async Task Resend_is_refused_for_bad_input_other_companies_and_delivered_notifications()
    {
        var failed = await SeedAsync(agencyEmail: "not-an-email");
        var delivered = await SeedAsync();
        await DispatchDueAsync();

        Assert.Equal(ResendOutcome.InvalidRequest, (await ResendAsync(failed, new ResendRecipientAddress("Agency", "still bad"))).Outcome);
        Assert.Equal(ResendOutcome.InvalidRequest, (await ResendAsync(failed, new ResendRecipientAddress("Rep", AgencyEmail))).Outcome);
        Assert.Equal(ResendOutcome.NothingToResend, (await ResendAsync(delivered)).Outcome);

        await using var db = _fixture.CreateContext(Guid.NewGuid()); // another company
        var reader = new NotificationRequestReader(db, Options.Create(new DispatchOptions()));
        var other = await new NotificationResendService(
            db, new TenantStub(Guid.NewGuid()), new Caller("intruder"), Dispatcher(db), reader, _clock,
            NullLogger<NotificationResendService>.Instance).ResendAsync(failed, new ResendNotificationRequest(null), CancellationToken.None);
        Assert.Equal(ResendOutcome.NotFound, other.Outcome);
    }

    // T4 list + T5 count.
    [Fact]
    public async Task The_failed_list_and_health_count_show_only_this_companys_undelivered_notifications()
    {
        var failed = await SeedAsync(agencyEmail: "not-an-email");
        var delivered = await SeedAsync();
        await DispatchDueAsync();

        await using var db = _fixture.CreateContext(_companyId);
        var reader = new NotificationRequestReader(db, Options.Create(new DispatchOptions()));

        var list = await reader.ListAsync(new NotificationRequestQuery(null, null, 1, 50, "failed"), CancellationToken.None);
        var health = await reader.GetHealthAsync(CancellationToken.None);

        var row = Assert.Single(list.Items);
        Assert.Equal(failed, row.NotificationRequestId);
        Assert.Equal("PermanentlyFailed", row.Status);
        Assert.Contains("not a valid email address", row.FailureReason);
        Assert.NotEmpty(row.Attempts!);
        Assert.DoesNotContain(list.Items, item => item.NotificationRequestId == delivered);
        Assert.Equal(1, health.PermanentlyFailed);
        Assert.Equal(1, health.NeedsAttention);

        // After a successful resend the count drops.
        await ResendAsync(failed, new ResendRecipientAddress("Agency", AgencyEmail));
        await using var after = _fixture.CreateContext(_companyId);
        Assert.Equal(0, (await new NotificationRequestReader(after, Options.Create(new DispatchOptions())).GetHealthAsync(CancellationToken.None)).NeedsAttention);
    }

    private sealed class Caller(string userId) : ICallerIdentity
    {
        public string? UserId { get; } = userId;
    }

    /// <summary>Delivers, or fails transiently while Down; malformed addresses fail permanently like the real sender.</summary>
    private sealed class ScriptedEmailSender : IEmailSender
    {
        private readonly List<OutgoingEmail> _delivered = new();

        public bool Down { get; set; }

        public IReadOnlyList<OutgoingEmail> Delivered
        {
            get
            {
                lock (_delivered)
                {
                    return _delivered.ToList();
                }
            }
        }

        public Task<string?> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
        {
            if (!EmailAddressRules.IsWellFormed(email.ToAddress))
            {
                throw new EmailSendException(SendOutcome.PermanentFailure, $"'{email.ToAddress}' is not a valid email address.", null);
            }

            if (Down)
            {
                throw new EmailSendException(SendOutcome.TransientFailure, "Connection refused.", "421 4.3.2 Service not available");
            }

            lock (_delivered)
            {
                _delivered.Add(email);
            }

            return Task.FromResult<string?>("250 2.0.0 OK queued");
        }
    }
}
