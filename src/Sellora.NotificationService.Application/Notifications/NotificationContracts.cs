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
    string? LastError);

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
    Guid OrderId,
    string OrderReference,
    string Status,
    DateTimeOffset OccurredAt,
    DateTimeOffset ReceivedAt,
    string? CorrelationId,
    IReadOnlyList<NotificationRecipientResponse> Recipients,
    NotificationDispatchResponse Dispatch)
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
                recipient.LastError))
            .ToList(),
        new NotificationDispatchResponse(
            request.AttemptCount,
            request.LastAttemptAt,
            request.NextAttemptAt,
            request.CompletedAt,
            request.SendGapMilliseconds,
            toleranceMilliseconds,
            request.SendGapMilliseconds is { } gap ? gap <= toleranceMilliseconds : null,
            request.RenderedBodySha256));
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record NotificationRequestQuery(Guid? OrderId, string? OrderReference, int Page, int PageSize);

/// <summary>Company admin view of what was queued — lets QA check US-E5-1 without database access.</summary>
public interface INotificationRequestReader
{
    Task<PagedResponse<NotificationRequestResponse>> ListAsync(NotificationRequestQuery query, CancellationToken cancellationToken);

    Task<NotificationRequestResponse?> GetAsync(Guid notificationRequestId, CancellationToken cancellationToken);

    /// <summary>The stored rendered message; null when not found or not rendered yet.</summary>
    Task<RenderedNotificationResponse?> GetRenderedAsync(Guid notificationRequestId, CancellationToken cancellationToken);
}
