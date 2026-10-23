using Sellora.NotificationService.Domain.Entities;

namespace Sellora.NotificationService.Application.Notifications;

public enum IntakeOutcome
{
    /// <summary>A new Pending request was stored.</summary>
    Created,

    /// <summary>This event was already stored (a redelivery); nothing new.</summary>
    Duplicate,

    /// <summary>A known event that notifies nobody.</summary>
    Ignored,

    /// <summary>The message must go to the dead-letter topic.</summary>
    DeadLetter
}

public sealed record IntakeResult(IntakeOutcome Outcome, string Reason, Guid? NotificationRequestId = null);

/// <summary>
/// US-E5-1: turns one consumed message into at most one stored request.
/// Throws only on infrastructure failure (e.g. the database is down), in
/// which case the caller must not commit the offset so the record replays.
/// </summary>
public interface INotificationIntake
{
    Task<IntakeResult> AcceptAsync(ConsumedMessage message, CancellationToken cancellationToken);
}

public sealed record NotificationRecipientResponse(
    string Kind,
    Guid RecipientId,
    string? Name,
    string? Email,
    string DeliveryStatus,
    DateTimeOffset? SentAt,
    int Attempts,
    string? LastError,
    DateTimeOffset? LastAttemptAt = null);

/// <summary>US-E5-3: one recorded attempt — the history an admin diagnoses from.</summary>
public sealed record NotificationAttemptResponse(
    string RecipientKind,
    string? EmailAddress,
    int AttemptNumber,
    DateTimeOffset AttemptedAt,
    string Outcome,
    string? ProviderResponse,
    string? Error,
    string Trigger,
    string? TriggeredBy);

/// <summary>US-E5-2: how the dispatch went, measured rather than asserted.</summary>
public sealed record NotificationDispatchResponse(
    int AttemptCount,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? CompletedAt,
    int? SendGapMilliseconds,
    int ToleranceMilliseconds,
    bool? WithinTolerance,
    string? RenderedBodySha256);

/// <summary>US-E5-2-T4: the exact message sent, for a dispute.</summary>
public sealed record RenderedNotificationResponse(
    Guid NotificationRequestId,
    string OrderReference,
    string Subject,
    string Html,
    string Text,
    string BodySha256,
    DateTimeOffset RenderedAt);

public sealed record NotificationRequestResponse(
    Guid NotificationRequestId,
    Guid SourceEventId,
    string EventType,
    string TemplateKey,
    Guid? OrderId,
    string OrderReference,
    string Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string? CorrelationId,
    IReadOnlyList<NotificationRecipientResponse> Recipients,
    NotificationDispatchResponse Dispatch,
    string? FailureReason = null,
    DateTimeOffset? LastResendAt = null,
    string? LastResendBy = null,
    IReadOnlyList<NotificationAttemptResponse>? Attempts = null)
{
    public static NotificationRequestResponse From(NotificationRequest request, int toleranceMilliseconds) => new(
        request.NotificationRequestId,
        request.SourceEventId,
        request.EventType,
        request.TemplateKey,
        request.OrderId,
        request.OrderReference,
        request.Status.ToString(),
        request.OccurredAt,
        request.ReceivedAt,
        request.CorrelationId,
        request.Recipients
            .OrderBy(recipient => recipient.Kind)
            .Select(recipient => new NotificationRecipientResponse(
                recipient.Kind.ToString(),
                recipient.RecipientId,
                recipient.Name,
                recipient.Email,
                recipient.DeliveryStatus.ToString(),
                recipient.SentAt,
                recipient.Attempts,
                recipient.LastError,
                recipient.LastAttemptAt))
            .ToList(),
        new NotificationDispatchResponse(
            request.AttemptCount,
            request.LastAttemptAt,
            request.NextAttemptAt,
            request.CompletedAt,
            request.SendGapMilliseconds,
            toleranceMilliseconds,
            request.SendGapMilliseconds is { } gap ? gap <= toleranceMilliseconds : null,
            request.RenderedBodySha256),
        request.FailureReason,
        request.LastResendAt,
        request.LastResendBy,
        request.AttemptHistory
            .OrderBy(attempt => attempt.AttemptedAt)
            .ThenBy(attempt => attempt.RecipientKind)
            .Select(attempt => new NotificationAttemptResponse(
                attempt.RecipientKind.ToString(),
                attempt.EmailAddress,
                attempt.AttemptNumber,
                attempt.AttemptedAt,
                attempt.Outcome.ToString(),
                attempt.ProviderResponse,
                attempt.Error,
                attempt.Trigger.ToString(),
                attempt.TriggeredBy))
            .ToList());
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <param name="Status">
/// A status name (Pending, Sent, PartiallySent, Failed, PermanentlyFailed),
/// or <c>failed</c> for everything not fully delivered after an attempt
/// (Failed, PartiallySent and PermanentlyFailed) — the admin's list.
/// </param>
public sealed record NotificationRequestQuery(
    Guid? OrderId,
    string? OrderReference,
    int Page,
    int PageSize,
    string? Status = null);

/// <summary>US-E5-3-T5: counts a dashboard can poll; a rising NeedsAttention is a silent outage made visible.</summary>
public sealed record NotificationHealthResponse(
    int Pending,
    int Failed,
    int PartiallySent,
    int PermanentlyFailed,
    int NeedsAttention,
    DateTimeOffset? OldestFailureAt);

/// <summary>Company admin view of what was queued — lets QA check US-E5-1 without database access.</summary>
public interface INotificationRequestReader
{
    Task<PagedResponse<NotificationRequestResponse>> ListAsync(NotificationRequestQuery query, CancellationToken cancellationToken);

    Task<NotificationRequestResponse?> GetAsync(Guid notificationRequestId, CancellationToken cancellationToken);

    /// <summary>The stored rendered message; null when not found or not rendered yet.</summary>
    Task<RenderedNotificationResponse?> GetRenderedAsync(Guid notificationRequestId, CancellationToken cancellationToken);

    Task<NotificationHealthResponse> GetHealthAsync(CancellationToken cancellationToken);
}

/// <summary>A corrected address for one recipient, given on resend.</summary>
public sealed record ResendRecipientAddress(string Kind, string Email);

public sealed record ResendNotificationRequest(IReadOnlyCollection<ResendRecipientAddress>? Recipients);

public enum ResendOutcome
{
    Succeeded,
    NotFound,
    InvalidRequest,
    NothingToResend,
    Busy,
    CallerNotPermitted
}

/// <summary>The request after the resend attempt (which may itself have failed — see its status).</summary>
public sealed record ResendResult(ResendOutcome Outcome, NotificationRequestResponse? Request = null, string? Message = null);

/// <summary>US-E5-3-T4: a company admin's manual resend.</summary>
public interface INotificationResendService
{
    Task<ResendResult> ResendAsync(
        Guid notificationRequestId,
        ResendNotificationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>US-E5-4: the company's notification settings.</summary>
public sealed record NotificationSettingsResponse(string? AlertEmail, DateTimeOffset? UpdatedAt, string? UpdatedBy);

public sealed record UpdateNotificationSettingsRequest(string? AlertEmail);

public interface INotificationSettingsService
{
    Task<NotificationSettingsResponse> GetAsync(CancellationToken cancellationToken);

    /// <summary>Null/blank clears the alert address. Throws ArgumentException for a malformed one.</summary>
    Task<NotificationSettingsResponse> UpdateAsync(UpdateNotificationSettingsRequest request, CancellationToken cancellationToken);
}
