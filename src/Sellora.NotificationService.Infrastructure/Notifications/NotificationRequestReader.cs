using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Infrastructure.Persistence;

namespace Sellora.NotificationService.Infrastructure.Notifications;

/// <summary>Tenant-filtered reads for the company admin (the query filter does the scoping).</summary>
public sealed class NotificationRequestReader : INotificationRequestReader
{
    private readonly NotificationDbContext _db;
    private readonly int _tolerance;

    public NotificationRequestReader(NotificationDbContext db, IOptions<DispatchOptions> dispatch)
    {
        _db = db;
        _tolerance = dispatch.Value.SimultaneityToleranceMilliseconds;
    }

    public async Task<PagedResponse<NotificationRequestResponse>> ListAsync(
        NotificationRequestQuery query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var requests = _db.NotificationRequests.AsNoTracking();

        if (query.OrderId is { } orderId)
        {
            requests = requests.Where(request => request.OrderId == orderId);
        }

        requests = FilterByStatus(requests, query.Status);

        if (!string.IsNullOrWhiteSpace(query.OrderReference))
        {
            var reference = query.OrderReference.Trim();
            requests = requests.Where(request => request.OrderReference == reference);
        }

        var totalCount = await requests.CountAsync(cancellationToken);

        var items = await requests
            .Include(request => request.Recipients)
            .Include(request => request.AttemptHistory)
            .AsSplitQuery()
            .OrderByDescending(request => request.LastAttemptAt ?? request.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<NotificationRequestResponse>(
            items.Select(request => NotificationRequestResponse.From(request, _tolerance)).ToList(), page, pageSize, totalCount);
    }

    public async Task<NotificationRequestResponse?> GetAsync(Guid notificationRequestId, CancellationToken cancellationToken)
    {
        var request = await _db.NotificationRequests
            .AsNoTracking()
            .Include(candidate => candidate.Recipients)
            .Include(candidate => candidate.AttemptHistory)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.NotificationRequestId == notificationRequestId, cancellationToken);

        return request is null ? null : NotificationRequestResponse.From(request, _tolerance);
    }

    public async Task<RenderedNotificationResponse?> GetRenderedAsync(
        Guid notificationRequestId,
        CancellationToken cancellationToken)
    {
        var request = await _db.NotificationRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.NotificationRequestId == notificationRequestId, cancellationToken);

        return request?.Rendered is { } rendered
            ? new RenderedNotificationResponse(
                request.NotificationRequestId,
                request.OrderReference,
                rendered.Subject,
                rendered.Html,
                rendered.Text,
                rendered.BodySha256,
                request.RenderedAt!.Value)
            : null;
    }

    public async Task<NotificationHealthResponse> GetHealthAsync(CancellationToken cancellationToken)
    {
        // One grouped query: cheap enough for a dashboard to poll.
        var counts = await _db.NotificationRequests
            .AsNoTracking()
            .Where(request => request.Status != NotificationStatus.Sent)
            .GroupBy(request => request.Status)
            .Select(group => new
            {
                Status = group.Key,
                Count = group.Count(),
                Oldest = group.Min(request => (DateTimeOffset?)(request.LastAttemptAt ?? request.ReceivedAt))
            })
            .ToListAsync(cancellationToken);

        int Count(NotificationStatus status) => counts.FirstOrDefault(entry => entry.Status == status)?.Count ?? 0;

        var failed = Count(NotificationStatus.Failed);
        var partial = Count(NotificationStatus.PartiallySent);
        var permanent = Count(NotificationStatus.PermanentlyFailed);

        return new NotificationHealthResponse(
            Count(NotificationStatus.Pending),
            failed,
            partial,
            permanent,
            failed + partial + permanent,
            counts
                .Where(entry => entry.Status is NotificationStatus.Failed or NotificationStatus.PartiallySent or NotificationStatus.PermanentlyFailed)
                .Min(entry => entry.Oldest));
    }

    /// <summary>"failed" = everything not fully delivered after an attempt; otherwise an exact status.</summary>
    internal static IQueryable<Domain.Entities.NotificationRequest> FilterByStatus(
        IQueryable<Domain.Entities.NotificationRequest> requests,
        string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return requests;
        }

        if (string.Equals(status.Trim(), "failed", StringComparison.OrdinalIgnoreCase))
        {
            return requests.Where(request =>
                request.Status == NotificationStatus.Failed ||
                request.Status == NotificationStatus.PartiallySent ||
                request.Status == NotificationStatus.PermanentlyFailed);
        }

        return Enum.TryParse<NotificationStatus>(status.Trim(), ignoreCase: true, out var exact) && Enum.IsDefined(exact)
            ? requests.Where(request => request.Status == exact)
            : requests.Where(_ => false);
    }
}
