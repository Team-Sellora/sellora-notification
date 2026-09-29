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

/// <summary>
/// US-E5-4 against a real database: addresses resolved from the directory
/// and settings, and delivery / low-stock going through the SAME dispatcher
/// (render once, concurrent send, retry) as payment notifications.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class DeliveryAndLowStockIntakeTests
{
    private readonly PostgreSqlFixture _fixture;
    private readonly Guid _companyId = Guid.NewGuid();
    private readonly RecordingEmailSender _mail = new();

    public DeliveryAndLowStockIntakeTests(PostgreSqlFixture fixture) => _fixture = fixture;

    private async Task<IntakeResult> AcceptAsync(ConsumedMessage message)
    {
        await using var db = _fixture.CreateContext(null);
        return await new NotificationIntakeService(db, TimeProvider.System, NullLogger<NotificationIntakeService>.Instance)
            .AcceptAsync(message, CancellationToken.None);
    }

    private async Task SetAlertEmailAsync(string? email)
    {
        await using var db = _fixture.CreateContext(_companyId);
        await new NotificationSettingsService(db, new TenantStub(_companyId), new Caller("admin-sub"), TimeProvider.System)
            .UpdateAsync(new UpdateNotificationSettingsRequest(email), CancellationToken.None);
    }

    private async Task DispatchAsync()
    {
        await using var db = _fixture.CreateContext(null);
        await new NotificationDispatcher(
                db, _mail, TimeProvider.System, Options.Create(new DispatchOptions { BatchSize = 100 }),
                NullLogger<NotificationDispatcher>.Instance)
            .DispatchDueAsync(CancellationToken.None);
    }

    private async Task<NotificationRequest> ReloadAsync(Guid id)
    {
        await using var db = _fixture.CreateContext(null);
        return await db.NotificationRequests.IgnoreQueryFilters().Include(r => r.Recipients)
            .SingleAsync(r => r.NotificationRequestId == id);
    }

    private IReadOnlyList<OutgoingEmail> SentFor(Guid id) =>
        _mail.Sent.Where(email => email.Headers[NotificationDispatcher.NotificationIdHeader] == id.ToString()).ToList();

    // Q2 end to end: agency + company admin get the same message, no shop.
    [Fact]
    public async Task Low_stock_reaches_the_agency_and_the_company_admin_with_the_product_name()
    {
        // An earlier order taught the directory the agency's address and the product's name.
        await AcceptAsync(TestEvents.Message(TestEvents.Order("OrderPlaced", companyId: _companyId)
            .Also(e => e["lines"]![0]!["productId"] = DeliveryAndStockTestEvents.ProductId.ToString())));
        await SetAlertEmailAsync("ops@acme.lk");

        var result = await AcceptAsync(DeliveryAndStockTestEvents.OnSharedTopic(
            DeliveryAndStockTestEvents.LowStock(companyId: _companyId), "sellora.inventory.v1"));
        await DispatchAsync();

        var stored = await ReloadAsync(result.NotificationRequestId!.Value);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(
            new[] { "ops@acme.lk", "orders@colombo-agency.lk" },
            stored.Recipients.Select(r => r.Email).OrderBy(e => e));
        Assert.DoesNotContain(stored.Recipients, r => r.Kind == RecipientKind.Shop);

        var sent = SentFor(stored.NotificationRequestId);
        Assert.Equal(2, sent.Count);
        Assert.Single(sent.Select(e => e.Html).Distinct()); // render once, identical to both
        Assert.Contains("Sunlight Soap 100g", sent[0].Subject);
    }

    [Fact]
    public async Task Without_an_alert_address_the_company_admin_is_flagged_not_silently_skipped()
    {
        await SetAlertEmailAsync(null);

        var result = await AcceptAsync(DeliveryAndStockTestEvents.OnSharedTopic(
            DeliveryAndStockTestEvents.LowStock(companyId: _companyId, ownerType: "SalesRep"), "sellora.inventory.v1"));
        await DispatchAsync();

        var stored = await ReloadAsync(result.NotificationRequestId!.Value);
        Assert.Equal(NotificationStatus.PermanentlyFailed, stored.Status); // on the admin list
        Assert.Equal(RecipientDeliveryStatus.Unaddressed, Assert.Single(stored.Recipients).DeliveryStatus);
    }

    // Q1 end to end.
    [Fact]
    public async Task A_delivery_status_the_shop_can_act_on_is_sent_and_an_internal_one_is_not()
    {
        var delivered = await AcceptAsync(DeliveryAndStockTestEvents.OnSharedTopic(
            DeliveryAndStockTestEvents.Delivery(status: "Delivered", companyId: _companyId), "sellora.delivery.v1"));
        var assigned = await AcceptAsync(DeliveryAndStockTestEvents.OnSharedTopic(
            DeliveryAndStockTestEvents.Delivery(status: "Assigned", companyId: _companyId), "sellora.delivery.v1"));
        await DispatchAsync();

        Assert.Equal(IntakeOutcome.Ignored, assigned.Outcome);
        var sent = SentFor(delivered.NotificationRequestId!.Value);
        Assert.Equal(new[] { "orders@colombo-agency.lk", "owner@lakshmi-stores.lk" }, sent.Select(e => e.ToAddress).OrderBy(a => a));
        Assert.StartsWith("[ORD-260929-DLV001] Delivery completed", sent[0].Subject);
    }

    [Fact]
    public async Task A_delivery_event_without_emails_uses_the_addresses_learned_from_orders()
    {
        await AcceptAsync(TestEvents.Message(TestEvents.Order("OrderConfirmed", companyId: _companyId)));

        var result = await AcceptAsync(DeliveryAndStockTestEvents.OnSharedTopic(
            DeliveryAndStockTestEvents.Delivery(status: "InTransit", companyId: _companyId, shopEmail: null, agencyEmail: null),
            "sellora.delivery.v1"));

        var stored = await ReloadAsync(result.NotificationRequestId!.Value);
        Assert.Equal("owner@lakshmi-stores.lk", stored.Recipients.Single(r => r.Kind == RecipientKind.Shop).Email);
        Assert.Equal("orders@colombo-agency.lk", stored.Recipients.Single(r => r.Kind == RecipientKind.Agency).Email);
    }

    [Fact]
    public async Task Settings_reject_a_malformed_address_and_are_per_company()
    {
        await SetAlertEmailAsync("ops@acme.lk");
        await Assert.ThrowsAsync<ArgumentException>(() => SetAlertEmailAsync("not-an-email"));

        await using var mine = _fixture.CreateContext(_companyId);
        await using var theirs = _fixture.CreateContext(Guid.NewGuid());
        var caller = new Caller("x");

        Assert.Equal("ops@acme.lk", (await new NotificationSettingsService(mine, new TenantStub(_companyId), caller, TimeProvider.System)
            .GetAsync(CancellationToken.None)).AlertEmail);
        Assert.Null((await new NotificationSettingsService(theirs, new TenantStub(Guid.NewGuid()), caller, TimeProvider.System)
            .GetAsync(CancellationToken.None)).AlertEmail);
    }

    private sealed class Caller(string userId) : ICallerIdentity
    {
        public string? UserId { get; } = userId;
    }
}

internal static class JsonNodeExtensions
{
    public static System.Text.Json.Nodes.JsonObject Also(this System.Text.Json.Nodes.JsonObject node, Action<System.Text.Json.Nodes.JsonObject> change)
    {
        change(node);
        return node;
    }
}
