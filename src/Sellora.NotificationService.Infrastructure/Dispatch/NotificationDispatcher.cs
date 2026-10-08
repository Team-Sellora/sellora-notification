using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Application.Rendering;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Infrastructure.Persistence;

namespace Sellora.NotificationService.Infrastructure.Dispatch;

/// <summary>
/// US-E5-2: renders each due request once, stores the rendered message, and
/// sends that same message to the shop and the agency concurrently.
///
///   claim (FOR UPDATE SKIP LOCKED, lease) → render once + persist
///     → Task.WhenAll(send to every recipient still owed it)
///     → record per-recipient timestamps, the gap, and Sent / PartiallySent
///
/// A retry sends the stored message to the recipients still owed it and
/// never to one already sent to. Rendering is persisted before sending, so
/// what is stored is exactly what went out.
/// </summary>
public sealed class NotificationDispatcher(
    NotificationDbContext db,
    IEmailSender sender,
    TimeProvider clock,
    IOptions<DispatchOptions> options,
    ILogger<NotificationDispatcher> logger,
    Func<double>? random = null) : INotificationDispatcher
{
    /// <summary>Jitter source; tests pass a fixed value to make delays exact.</summary>
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    public const string NotificationIdHeader = "X-Sellora-Notification-Id";
    public const string OrderReferenceHeader = "X-Sellora-Order-Reference";
    public const string BodyHashHeader = "X-Sellora-Body-SHA256";

    public async Task<int> DispatchDueAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var claimed = await ClaimDueAsync(settings, cancellationToken);

        foreach (var notificationRequestId in claimed)
        {
            await DispatchOneAsync(notificationRequestId, settings, AttemptTrigger.Automatic, null, cancellationToken);
        }

        return claimed.Count;
    }

    public async Task<bool> DispatchNowAsync(
        Guid notificationRequestId,
        AttemptTrigger trigger,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();

        // Claim this one row, unless another dispatcher is sending it right now.
        var claimed = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            UPDATE notification_request
            SET claimed_until = {now.AddSeconds(settings.LeaseSeconds)}
            WHERE notification_request_id = {notificationRequestId}
              AND (claimed_until IS NULL OR claimed_until < {now})
            """,
            cancellationToken);

        if (claimed != 1)
        {
            return false;
        }

        await DispatchOneAsync(notificationRequestId, settings, trigger, triggeredBy, cancellationToken);
        return true;
    }

    /// <summary>
    /// Atomically reserves up to BatchSize due requests. SKIP LOCKED plus the
    /// lease means two instances (e.g. both deployment slots) never take the
    /// same request, and a crashed dispatcher's claim expires by itself.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ClaimDueAsync(DispatchOptions settings, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var connection = db.Database.GetDbConnection();
        var opened = connection.State != ConnectionState.Open;

        if (opened)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE notification_request
                SET claimed_until = @lease_until
                WHERE notification_request_id IN (
                    SELECT notification_request_id
                    FROM notification_request
                    WHERE status IN ('Pending', 'PartiallySent', 'Failed')
                      AND (attempt_count = 0 OR next_attempt_at <= @now)
                      AND (claimed_until IS NULL OR claimed_until < @now)
                    ORDER BY received_at
                    LIMIT @batch
                    FOR UPDATE SKIP LOCKED)
                RETURNING notification_request_id;
                """;
            command.Parameters.Add(new NpgsqlParameter("lease_until", now.AddSeconds(settings.LeaseSeconds)));
            command.Parameters.Add(new NpgsqlParameter("now", now));
            command.Parameters.Add(new NpgsqlParameter("batch", Math.Clamp(settings.BatchSize, 1, 100)));

            var ids = new List<Guid>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                ids.Add(reader.GetGuid(0));
            }

            return ids;
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task DispatchOneAsync(
        Guid notificationRequestId,
        DispatchOptions settings,
        AttemptTrigger trigger,
        string? triggeredBy,
        CancellationToken cancellationToken)
    {
        var policy = settings.Policy(_random);

        db.ChangeTracker.Clear();

        var request = await db.NotificationRequests
            .IgnoreQueryFilters() // background work across tenants; each row carries its own companyId
            .Include(candidate => candidate.Recipients)
            .Include(candidate => candidate.AttemptHistory)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.NotificationRequestId == notificationRequestId, cancellationToken);

        if (request is null || request.Status == NotificationStatus.Sent)
        {
            return;
        }

        // DoD 1: render once. Stored before anything is sent, so the stored
        // body is always the body that went out.
        if (!request.IsRendered)
        {
            RenderedMessage rendered;

            try
            {
                rendered = NotificationRenderer.Render(request.TemplateKey, request.Payload, request.Context);
            }
            catch (NotificationRenderException exception)
            {
                logger.LogError(
                    exception,
                    "NotificationRenderFailed {NotificationRequestId} ({OrderReference}): {Reason}",
                    request.NotificationRequestId, request.OrderReference, exception.Message);

                // Retrying cannot fix the stored event: permanent, visible to the admin.
                request.RecordDispatch(
                    request.RecipientsToSend()
                        .Select(recipient => new RecipientSendResult(
                            recipient.NotificationRecipientId, SendOutcome.PermanentFailure, clock.GetUtcNow(), null,
                            $"Could not render the message: {exception.Message}"))
                        .ToList(),
                    clock.GetUtcNow(),
                    policy,
                    trigger,
                    triggeredBy);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            request.AttachRendering(rendered, clock.GetUtcNow());
        }

        request.MarkUnaddressedRecipients();
        await db.SaveChangesAsync(cancellationToken);

        var message = request.Rendered!;
        var targets = request.RecipientsToSend();

        // DoD 1: one operation, concurrent sends — the same body to each.
        var results = await Task.WhenAll(targets.Select(recipient => SendAsync(request, recipient, message, cancellationToken)));

        request.RecordDispatch(results, clock.GetUtcNow(), policy, trigger, triggeredBy);
        await db.SaveChangesAsync(cancellationToken);

        Log(request, results, settings);
    }

    private async Task<RecipientSendResult> SendAsync(
        NotificationRequest request,
        NotificationRecipient recipient,
        RenderedMessage message,
        CancellationToken cancellationToken)
    {
        var email = new OutgoingEmail(
            recipient.Email!,
            recipient.Name,
            message.Subject,
            message.Html,
            message.Text,
            new Dictionary<string, string>
            {
                [NotificationIdHeader] = request.NotificationRequestId.ToString(),
                [OrderReferenceHeader] = request.OrderReference,
                [BodyHashHeader] = message.BodySha256
            });

        try
        {
            var providerResponse = await sender.SendAsync(email, cancellationToken);

            // Taken when the provider accepted it — the timestamp the gap is measured on.
            return new RecipientSendResult(
                recipient.NotificationRecipientId, SendOutcome.Sent, clock.GetUtcNow(), providerResponse, null);
        }
        catch (EmailSendException exception)
        {
            return new RecipientSendResult(
                recipient.NotificationRecipientId, exception.Outcome, clock.GetUtcNow(), exception.ProviderResponse, exception.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A sender that did not classify its failure: retry within the budget.
            return new RecipientSendResult(
                recipient.NotificationRecipientId, SendOutcome.TransientFailure, clock.GetUtcNow(), null,
                $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private void Log(NotificationRequest request, IReadOnlyCollection<RecipientSendResult> results, DispatchOptions settings)
    {
        foreach (var failure in results.Where(result => !result.Succeeded))
        {
            var recipient = request.Recipients.Single(candidate => candidate.NotificationRecipientId == failure.NotificationRecipientId);
            logger.LogWarning(
                "NotificationSendFailed {NotificationRequestId} ({OrderReference}) to {RecipientKind}: {Outcome} {Error} [{ProviderResponse}]. Next attempt {NextAttemptAt}",
                request.NotificationRequestId, request.OrderReference, recipient.Kind, failure.Outcome, failure.Error,
                failure.ProviderResponse, request.NextAttemptAt);
        }

        if (request.Status == NotificationStatus.PermanentlyFailed)
        {
            // The dead-letter point: no more automatic retries; it is on the admin list.
            logger.LogError(
                "NotificationPermanentlyFailed {NotificationRequestId} ({OrderReference}): {FailureReason}",
                request.NotificationRequestId, request.OrderReference, request.FailureReason);
        }

        logger.LogInformation(
            "NotificationDispatched {NotificationRequestId} ({OrderReference}) {Status}: sent {SentCount}/{RecipientCount}, gap {SendGapMs} ms, attempt {Attempt}",
            request.NotificationRequestId, request.OrderReference, request.Status,
            request.Recipients.Count(recipient => recipient.DeliveryStatus == RecipientDeliveryStatus.Sent),
            request.Recipients.Count, request.SendGapMilliseconds, request.AttemptCount);

        if (request.SendGapMilliseconds > settings.SimultaneityToleranceMilliseconds)
        {
            logger.LogWarning(
                "NotificationGapOutsideTolerance {NotificationRequestId} ({OrderReference}): {SendGapMs} ms > {ToleranceMs} ms",
                request.NotificationRequestId, request.OrderReference, request.SendGapMilliseconds,
                settings.SimultaneityToleranceMilliseconds);
        }
    }
}
