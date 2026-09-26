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

public sealed record NotificationRecipientResponse(string Kind, Guid RecipientId, string? Name, string? Email);

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
    IReadOnlyList<NotificationRecipientResponse> Recipients)
{
    public static NotificationRequestResponse From(NotificationRequest request) => new(
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
                recipient.Kind.ToString(), recipient.RecipientId, recipient.Name, recipient.Email))
            .ToList());
}

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record NotificationRequestQuery(Guid? OrderId, string? OrderReference, int Page, int PageSize);

/// <summary>Company admin view of what was queued — lets QA check US-E5-1 without database access.</summary>
public interface INotificationRequestReader
{
    Task<PagedResponse<NotificationRequestResponse>> ListAsync(NotificationRequestQuery query, CancellationToken cancellationToken);

    Task<NotificationRequestResponse?> GetAsync(Guid notificationRequestId, CancellationToken cancellationToken);
}
