using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Infrastructure.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>US-E5-1-T3: one Pending request per event ID, against a real database.</summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class NotificationIntakeTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();

    public NotificationIntakeTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private async Task<IntakeResult> AcceptAsync(ConsumedMessage message)
    {
        await using var db = _fixture.CreateContext(null);
        return await new NotificationIntakeService(db, TimeProvider.System, NullLogger<NotificationIntakeService>.Instance)
            .AcceptAsync(message, CancellationToken.None);
    }

    private async Task<int> CountAsync(Guid eventId)
    {
        await using var db = _fixture.CreateContext(null);
        return await db.NotificationRequests.IgnoreQueryFilters().CountAsync(request => request.SourceEventId == eventId);
    }

    [Theory]
    [InlineData("OrderPlaced")]
    [InlineData("OrderConfirmed")]
    [InlineData("PaymentRecorded")]
    [InlineData("OrderCancelled")]
    public async Task Each_event_type_stores_one_pending_request_with_both_recipients_and_the_raw_payload(string eventType)
    {
        var @event = TestEvents.Order(eventType, companyId: _companyId);
        var message = TestEvents.Message(@event, offset: 42);

        var result = await AcceptAsync(message);

        Assert.Equal(IntakeOutcome.Created, result.Outcome);

        await using var db = _fixture.CreateContext(_companyId);
        var stored = await db.NotificationRequests
            .Include(request => request.Recipients)
            .SingleAsync(request => request.NotificationRequestId == result.NotificationRequestId);

        Assert.Equal(NotificationStatus.Pending, stored.Status);
        Assert.Equal(eventType, stored.EventType);
        Assert.Equal(Guid.Parse(TestEvents.EventId(@event)), stored.SourceEventId);
        Assert.Equal(_companyId, stored.CompanyId);
        Assert.Equal("test-correlation", stored.CorrelationId);
        Assert.Equal(("sellora.order.v1", 0, 42L), (stored.SourceTopic, stored.SourcePartition, stored.SourceOffset));
        Assert.Contains("Lakshmi Stores", stored.Payload);
        Assert.Equal(
            new[] { RecipientKind.Shop, RecipientKind.Agency },
            stored.Recipients.OrderBy(recipient => recipient.Kind).Select(recipient => recipient.Kind));
        Assert.Equal(
            "owner@lakshmi-stores.lk",
            stored.Recipients.Single(recipient => recipient.Kind == RecipientKind.Shop).Email);
    }

    // Q2: a redelivery (same event ID) leaves exactly one request.
    [Fact]
    public async Task A_redelivered_event_does_not_create_a_second_request()
    {
        var @event = TestEvents.Order("PaymentRecorded", companyId: _companyId);

        var first = await AcceptAsync(TestEvents.Message(@event, offset: 1));
        var second = await AcceptAsync(TestEvents.Message(@event, offset: 7));

        Assert.Equal(IntakeOutcome.Created, first.Outcome);
        Assert.Equal(IntakeOutcome.Duplicate, second.Outcome);
        Assert.Equal(first.NotificationRequestId, second.NotificationRequestId);
        Assert.Equal(1, await CountAsync(Guid.Parse(TestEvents.EventId(@event))));
    }

    // Two consumers racing on the same event: the unique index decides.
    [Fact]
    public async Task Concurrent_deliveries_of_one_event_still_store_one_request()
    {
        var @event = TestEvents.Order("OrderConfirmed", companyId: _companyId);

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => AcceptAsync(TestEvents.Message(@event))));

        Assert.Single(results, result => result.Outcome == IntakeOutcome.Created);
        Assert.Equal(4, results.Count(result => result.Outcome == IntakeOutcome.Duplicate));
        Assert.Equal(1, await CountAsync(Guid.Parse(TestEvents.EventId(@event))));
    }

    [Fact]
    public async Task Ignored_and_dead_letter_messages_store_nothing()
    {
        var approved = TestEvents.Order("OrderApproved", companyId: _companyId);
        var unknown = TestEvents.Order("OrderPlaced", companyId: _companyId);
        unknown["eventType"] = "Mystery";

        Assert.Equal(IntakeOutcome.Ignored, (await AcceptAsync(TestEvents.Message(approved))).Outcome);
        Assert.Equal(IntakeOutcome.DeadLetter, (await AcceptAsync(TestEvents.Message(unknown))).Outcome);
        Assert.Equal(IntakeOutcome.DeadLetter, (await AcceptAsync(TestEvents.Message("{oops"))).Outcome);

        Assert.Equal(0, await CountAsync(Guid.Parse(TestEvents.EventId(approved))));
        Assert.Equal(0, await CountAsync(Guid.Parse(TestEvents.EventId(unknown))));
    }

    [Fact]
    public async Task Reads_are_limited_to_the_callers_company()
    {
        var @event = TestEvents.Order("OrderPlaced", companyId: _companyId);
        var created = await AcceptAsync(TestEvents.Message(@event));

        await using var mine = _fixture.CreateContext(_companyId);
        await using var theirs = _fixture.CreateContext(Guid.NewGuid());

        Assert.NotNull(await new NotificationRequestReader(mine).GetAsync(created.NotificationRequestId!.Value, CancellationToken.None));
        Assert.Null(await new NotificationRequestReader(theirs).GetAsync(created.NotificationRequestId!.Value, CancellationToken.None));
    }
}
