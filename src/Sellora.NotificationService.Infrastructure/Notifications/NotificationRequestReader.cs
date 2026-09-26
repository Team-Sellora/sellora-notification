using Microsoft.EntityFrameworkCore;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Infrastructure.Persistence;

namespace Sellora.NotificationService.Infrastructure.Notifications;

/// <summary>Tenant-filtered reads for the company admin (the query filter does the scoping).</summary>
public sealed class NotificationRequestReader : INotificationRequestReader
{
    private readonly NotificationDbContext _db;

    public NotificationRequestReader(NotificationDbContext db)
    {
        _db = db;
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

        if (!string.IsNullOrWhiteSpace(query.OrderReference))
        {
            var reference = query.OrderReference.Trim();
            requests = requests.Where(request => request.OrderReference == reference);
        }

        var totalCount = await requests.CountAsync(cancellationToken);

        var items = await requests
            .Include(request => request.Recipients)
            .OrderByDescending(request => request.ReceivedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResponse<NotificationRequestResponse>(
            items.Select(NotificationRequestResponse.From).ToList(), page, pageSize, totalCount);
    }

    public async Task<NotificationRequestResponse?> GetAsync(Guid notificationRequestId, CancellationToken cancellationToken)
    {
        var request = await _db.NotificationRequests
            .AsNoTracking()
            .Include(candidate => candidate.Recipients)
            .SingleOrDefaultAsync(candidate => candidate.NotificationRequestId == notificationRequestId, cancellationToken);

        return request is null ? null : NotificationRequestResponse.From(request);
    }
}
