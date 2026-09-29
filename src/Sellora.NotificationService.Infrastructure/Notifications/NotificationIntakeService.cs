using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Sellora.NotificationService.Application.Events;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Notifications;
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

        // US-E5-4: remember names and addresses every order event carries.
        await ApplyDirectoryUpdatesAsync(plan.DirectoryUpdates, cancellationToken);

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

        var recipients = await ResolveRecipientsAsync(draft, cancellationToken);
        var context = draft.LowStock is null ? null : await LowStockContextAsync(draft.LowStock, cancellationToken);

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
                recipients,
                context);
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

    /// <summary>
    /// US-E5-4: fills in addresses the event did not carry — an agency or
    /// shop from the directory learned from order events, the company admin
    /// from notification settings. Still missing = stored without an address,
    /// so the dispatcher marks it Unaddressed and the admin list shows it.
    /// </summary>
    private async Task<IReadOnlyList<NewRecipient>> ResolveRecipientsAsync(
        NotificationDraft draft,
        CancellationToken cancellationToken)
    {
        var resolved = new List<NewRecipient>();

        foreach (var recipient in draft.Recipients)
        {
            if (recipient.Email is not null)
            {
                resolved.Add(recipient);
                continue;
            }

            if (recipient.Kind == RecipientKind.CompanyAdmin)
            {
                var alertEmail = await _db.NotificationSettings.IgnoreQueryFilters()
                    .Where(settings => settings.CompanyId == draft.CompanyId)
                    .Select(settings => settings.AlertEmail)
                    .SingleOrDefaultAsync(cancellationToken);
                resolved.Add(recipient with { Email = alertEmail });
                continue;
            }

            var kind = recipient.Kind == RecipientKind.Shop ? DirectoryEntryKind.Shop : DirectoryEntryKind.Agency;
            var entry = await DirectoryAsync(draft.CompanyId, kind, recipient.RecipientId, cancellationToken);
            resolved.Add(recipient with { Email = entry?.Email, Name = recipient.Name ?? entry?.Name });
        }

        return resolved;
    }

    private async Task<string> LowStockContextAsync(LowStockEventMessage lowStock, CancellationToken cancellationToken)
    {
        var product = await DirectoryAsync(lowStock.CompanyId, DirectoryEntryKind.Product, lowStock.ProductId, cancellationToken);
        var isAgencyStock = string.Equals(lowStock.OwnerType, "Agency", StringComparison.OrdinalIgnoreCase) &&
                            lowStock.ExternalOwnerId is not null;
        var agency = isAgencyStock
            ? await DirectoryAsync(lowStock.CompanyId, DirectoryEntryKind.Agency, lowStock.ExternalOwnerId!.Value, cancellationToken)
            : null;
        var agencyName = isAgencyStock ? agency?.Name ?? lowStock.OwnerDisplayName ?? "the agency" : null;

        return System.Text.Json.JsonSerializer.Serialize(
            new Application.Rendering.NotificationRenderer.LowStockContext(
                product?.Name,
                agencyName,
                lowStock.OwnerDisplayName ?? agency?.Name),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
    }

    private Task<DirectoryEntry?> DirectoryAsync(Guid companyId, DirectoryEntryKind kind, Guid entryId, CancellationToken cancellationToken) =>
        _db.DirectoryEntries.IgnoreQueryFilters()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                entry => entry.CompanyId == companyId && entry.Kind == kind && entry.EntryId == entryId,
                cancellationToken);

    /// <summary>Latest non-empty value wins; concurrent consumers are safe (ON CONFLICT).</summary>
    private async Task ApplyDirectoryUpdatesAsync(IReadOnlyList<DirectoryUpdate>? updates, CancellationToken cancellationToken)
    {
        if (updates is null || updates.Count == 0)
        {
            return;
        }

        var now = _clock.GetUtcNow();

        foreach (var update in updates)
        {
            var name = Trim(update.Name, NotificationRecipient.MaxNameLength);
            var email = Trim(update.Email, NotificationRecipient.MaxEmailLength);

            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO notification_directory (company_id, kind, entry_id, name, email, updated_at)
                VALUES ({update.CompanyId}, {update.Kind.ToString()}, {update.EntryId}, {name}, {email}, {now})
                ON CONFLICT (company_id, kind, entry_id) DO UPDATE
                SET name = COALESCE(EXCLUDED.name, notification_directory.name),
                    email = COALESCE(EXCLUDED.email, notification_directory.email),
                    updated_at = EXCLUDED.updated_at
                """,
                cancellationToken);
        }
    }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length <= max ? trimmed : trimmed[..max];
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
