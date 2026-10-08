using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Infrastructure.Dispatch;
using Sellora.NotificationService.Infrastructure.Email;
using Sellora.NotificationService.Infrastructure.Notifications;

namespace Sellora.NotificationService.Tests;

/// <summary>
/// US-E5-2-Q1/Q2 through real SMTP into a captured test mailbox (Mailpit in
/// Testcontainers — the same MailKit code that talks to Brevo in staging):
/// both mailboxes receive bodies identical apart from the address header,
/// with the coordinates and map link of the payment's check-in.
/// </summary>
[Collection(PostgreSqlCollection.Name)]
public sealed class MailpitDispatchTests : IAsyncLifetime
{
    private readonly PostgreSqlFixture _fixture;
    private readonly IContainer _mailpit = new ContainerBuilder("axllent/mailpit:v1.21")
        .WithPortBinding(1025, true)
        .WithPortBinding(8025, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8025).ForPath("/livez")))
        .Build();

    private readonly Guid _companyId = Guid.NewGuid();
    private readonly string _reference = $"ORD-260926-{Random.Shared.Next(100000, 999999)}";
    private HttpClient _api = null!;

    public MailpitDispatchTests(PostgreSqlFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _mailpit.StartAsync();
        _api = new HttpClient { BaseAddress = new Uri($"http://{_mailpit.Hostname}:{_mailpit.GetMappedPublicPort(8025)}") };
    }

    public async Task DisposeAsync()
    {
        _api.Dispose();
        await _mailpit.DisposeAsync();
    }

    [Fact]
    public async Task Shop_and_agency_receive_identical_bodies_with_the_check_in_location()
    {
        var @event = TestEvents.Order("PaymentRecorded", companyId: _companyId, orderReference: _reference);
        var location = @event["checkInLocation"]!.AsObject();
        location["latitude"] = 6.927123;
        location["longitude"] = 79.861235;

        Guid requestId;
        await using (var db = _fixture.CreateContext(null))
        {
            requestId = (await new NotificationIntakeService(db, TimeProvider.System, NullLogger<NotificationIntakeService>.Instance)
                .AcceptAsync(TestEvents.Message(@event), CancellationToken.None)).NotificationRequestId!.Value;
        }

        var smtp = Options.Create(new SmtpOptions
        {
            Host = _mailpit.Hostname,
            Port = _mailpit.GetMappedPublicPort(1025),
            From = "noreply@sellora.test",
            EnableSsl = false
        });

        await using (var db = _fixture.CreateContext(null))
        {
            await new NotificationDispatcher(
                    db, new SmtpEmailSender(smtp), TimeProvider.System,
                    Options.Create(new DispatchOptions { BatchSize = 100 }), NullLogger<NotificationDispatcher>.Instance)
                .DispatchDueAsync(CancellationToken.None);
        }

        var messages = await MessagesForAsync(_reference);
        Assert.Equal(2, messages.Count);

        var shop = messages.Single(message => message.To == "owner@lakshmi-stores.lk");
        var agency = messages.Single(message => message.To == "orders@colombo-agency.lk");

        // Identical apart from the address header.
        Assert.Equal(shop.Subject, agency.Subject);
        Assert.Equal(shop.Html, agency.Html);
        Assert.Equal(shop.Text, agency.Text);
        Assert.StartsWith($"[{_reference}]", shop.Subject);

        // Scenario 2: coordinates and map link as recorded on the payment.
        Assert.Contains("6.927123, 79.861235", shop.Text);
        Assert.Contains("https://www.google.com/maps/search/?api=1&query=6.927123,79.861235", shop.Text);
        Assert.Contains("https://www.google.com/maps/search/?api=1&amp;query=6.927123,79.861235", shop.Html);

        // DoD 5: what the mailbox received is what is stored.
        await using var check = _fixture.CreateContext(null);
        var stored = await check.NotificationRequests.IgnoreQueryFilters().Include(request => request.Recipients)
            .SingleAsync(request => request.NotificationRequestId == requestId);
        Assert.Equal(NotificationStatus.Sent, stored.Status);
        Assert.Equal(stored.RenderedSubject, shop.Subject);
        Assert.Equal(Normalise(stored.RenderedText!), Normalise(shop.Text));
        Assert.Equal(stored.RenderedBodySha256, shop.BodyHash);
        Assert.NotNull(stored.SendGapMilliseconds);
        Assert.True(stored.SendGapMilliseconds <= new DispatchOptions().SimultaneityToleranceMilliseconds);
    }

    private sealed record Captured(string To, string Subject, string Html, string Text, string? BodyHash);

    private async Task<List<Captured>> MessagesForAsync(string reference)
    {
        using var list = JsonDocument.Parse(await _api.GetStringAsync("/api/v1/messages"));
        var captured = new List<Captured>();

        foreach (var summary in list.RootElement.GetProperty("messages").EnumerateArray())
        {
            if (!summary.GetProperty("Subject").GetString()!.Contains(reference))
            {
                continue;
            }

            var id = summary.GetProperty("ID").GetString();
            using var message = JsonDocument.Parse(await _api.GetStringAsync($"/api/v1/message/{id}"));
            var headers = await _api.GetFromJsonAsync<Dictionary<string, string[]>>($"/api/v1/message/{id}/headers");
            var root = message.RootElement;

            captured.Add(new Captured(
                root.GetProperty("To")[0].GetProperty("Address").GetString()!,
                root.GetProperty("Subject").GetString()!,
                root.GetProperty("HTML").GetString()!,
                root.GetProperty("Text").GetString()!,
                headers?.FirstOrDefault(header =>
                    string.Equals(header.Key, NotificationDispatcher.BodyHashHeader, StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault()));
        }

        return captured;
    }

    // SMTP transports text with CRLF line endings; compare content, not line-ending style.
    private static string Normalise(string text) => text.Replace("\r\n", "\n").TrimEnd();
}
