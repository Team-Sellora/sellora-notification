using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Infrastructure.Persistence;
using Sellora.NotificationService.Infrastructure.Persistence.Configurations;

namespace Sellora.NotificationService.Infrastructure.Notifications;

/// <summary>
/// US-E5-1-T3: plans the message, then stores at most one Pending request
/// per event ID. The unique index is the guarantee; the lookup before the
/// insert only makes the common redelivery case quiet and cheap, and a race
/// between two consumers still ends with one row (the loser sees 23505).
/// </summary>
public sealed class NotificationIntakeService : INotificationIntake
{
    private readonly NotificationDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger<NotificationIntakeService> _logger;

    public NotificationIntakeService(NotificationDbContext db, TimeProvider clock, ILogger<NotificationIntakeService> logger)
    {
        _db = db;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IntakeResult> AcceptAsync(ConsumedMessage message, CancellationToken cancellationToken)
    {
        var plan = NotificationPlanner.Plan(message);

        if (plan.Kind == PlanKind.Ignore)
        {
            return new IntakeResult(IntakeOutcome.Ignored, plan.Reason);
        }

        if (plan.Kind == PlanKind.DeadLetter)
        {
            return new IntakeResult(IntakeOutcome.DeadLetter, plan.Reason);
        }

        var draft = plan.Draft!;

        var existing = await ExistingRequestIdAsync(draft.EventId, cancellationToken);

        if (existing is not null)
        {
            return Duplicate(draft, existing.Value, message);
        }

        NotificationRequest request;

        try
        {
            request = NotificationRequest.CreatePending(
                draft.CompanyId,
                draft.EventId,
                draft.EventType,
                draft.TemplateKey,
                draft.OrderId,
                draft.OrderReference,
                message.Value!,
                draft.CorrelationId,
                draft.OccurredAt,
                _clock.GetUtcNow(),
                new EventSource(message.Topic, message.Partition, message.Offset),
                draft.Recipients);
        }
        catch (ArgumentException exception)
        {
            // Retrying cannot fix bad data; replaying it forever would stall the partition.
            return new IntakeResult(IntakeOutcome.DeadLetter, exception.Message);
        }

        _db.NotificationRequests.Add(request);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateEvent(exception))
        {
            _db.ChangeTracker.Clear();
            var winner = await ExistingRequestIdAsync(draft.EventId, cancellationToken);
            return Duplicate(draft, winner ?? Guid.Empty, message);
        }

        var unaddressed = request.Recipients.Where(recipient => recipient.Email is null).Select(recipient => recipient.Kind).ToList();

        if (unaddressed.Count > 0)
        {
            // Kept, not dropped: US-E5-3 reports a recipient it could not reach.
            _logger.LogWarning(
                "Notification request {NotificationRequestId} for {EventType} {OrderReference} has no email for: {Recipients}",
                request.NotificationRequestId, draft.EventType, draft.OrderReference, string.Join(", ", unaddressed));
        }

        _logger.LogInformation(
            "NotificationRequestCreated {NotificationRequestId} from {EventType} {EventId} ({OrderReference}) for {RecipientCount} recipients",
            request.NotificationRequestId, draft.EventType, draft.EventId, draft.OrderReference, request.Recipients.Count);

        return new IntakeResult(IntakeOutcome.Created, "created", request.NotificationRequestId);
    }

    private IntakeResult Duplicate(NotificationDraft draft, Guid existingId, ConsumedMessage message)
    {
        // Q2: a redelivery is logged, never silently dropped.
        _logger.LogInformation(
            "NotificationEventDuplicate {EventType} {EventId} ({OrderReference}) redelivered from {Topic}/{Partition}/{Offset}; request {NotificationRequestId} already exists",
            draft.EventType, draft.EventId, draft.OrderReference, message.Topic, message.Partition, message.Offset, existingId);

        return new IntakeResult(IntakeOutcome.Duplicate, "already stored", existingId);
    }

    private Task<Guid?> ExistingRequestIdAsync(Guid eventId, CancellationToken cancellationToken) =>
        _db.NotificationRequests
            .IgnoreQueryFilters()
            .Where(request => request.SourceEventId == eventId)
            .Select(request => (Guid?)request.NotificationRequestId)
            .SingleOrDefaultAsync(cancellationToken);

    private static bool IsDuplicateEvent(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: NotificationRequestConfiguration.SourceEventUniqueIndex
        };
}
