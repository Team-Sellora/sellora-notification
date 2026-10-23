using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Application.Identity;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;
using Sellora.NotificationService.Infrastructure.Persistence;

namespace Sellora.NotificationService.Infrastructure.Notifications;

/// <summary>
/// US-E5-3-T4: a company admin's manual resend. Recipients not yet sent to
/// go back in the queue with a fresh retry budget — at a corrected address
/// if the admin gives one (the fix for an invalid or missing address) — and
/// the request is dispatched straight away, so the admin sees the result.
/// A recipient who already has the message is never sent it again, and
/// the new attempt is added to the history rather than replacing it.
/// </summary>
public sealed class NotificationResendService(
    NotificationDbContext db,
    ITenantContext tenant,
    ICallerIdentity caller,
    INotificationDispatcher dispatcher,
    INotificationRequestReader reader,
    TimeProvider clock,
    ILogger<NotificationResendService> logger) : INotificationResendService
{
    public async Task<ResendResult> ResendAsync(
        Guid notificationRequestId,
        ResendNotificationRequest request,
        CancellationToken cancellationToken)
    {
        if (tenant.CompanyId is null || string.IsNullOrWhiteSpace(caller.UserId))
        {
            return new ResendResult(ResendOutcome.CallerNotPermitted, Message: "The access token does not identify a company admin.");
        }

        var corrected = new Dictionary<RecipientKind, string>();

        foreach (var address in request.Recipients ?? Array.Empty<ResendRecipientAddress>())
        {
            if (!Enum.TryParse<RecipientKind>(address.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            {
                return new ResendResult(ResendOutcome.InvalidRequest, Message: $"Unknown recipient kind '{address.Kind}'. Use Shop or Agency.");
            }

            if (!EmailAddressRules.IsWellFormed(address.Email))
            {
                return new ResendResult(ResendOutcome.InvalidRequest, Message: $"'{address.Email}' is not a valid email address.");
            }

            corrected[kind] = address.Email.Trim();
        }

        // Tenant filter applies: another company's request looks missing.
        var stored = await db.NotificationRequests
            .Include(candidate => candidate.Recipients)
            .SingleOrDefaultAsync(candidate => candidate.NotificationRequestId == notificationRequestId, cancellationToken);

        if (stored is null)
        {
            return new ResendResult(ResendOutcome.NotFound, Message: $"No notification {notificationRequestId} is visible to you.");
        }

        try
        {
            stored.PrepareResend(corrected, caller.UserId, clock.GetUtcNow());
        }
        catch (InvalidOperationException exception)
        {
            return new ResendResult(ResendOutcome.NothingToResend, Message: exception.Message);
        }

        await db.SaveChangesAsync(cancellationToken);

        var dispatched = await dispatcher.DispatchNowAsync(
            notificationRequestId, AttemptTrigger.ManualResend, caller.UserId, cancellationToken);

        if (!dispatched)
        {
            // Queued already (due now); the dispatcher holding it will send it within seconds.
            return new ResendResult(
                ResendOutcome.Busy,
                await reader.GetAsync(notificationRequestId, cancellationToken),
                "This notification is being sent right now; it has been queued and will go out within seconds.");
        }

        var after = await reader.GetAsync(notificationRequestId, cancellationToken);

        logger.LogInformation(
            "NotificationResent {NotificationRequestId} by {UserId}: now {Status}",
            notificationRequestId, caller.UserId, after?.Status);

        return new ResendResult(ResendOutcome.Succeeded, after);
    }
}
