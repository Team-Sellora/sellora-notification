using Microsoft.EntityFrameworkCore;
using Sellora.NotificationService.Application.Identity;
using Sellora.NotificationService.Application.Notifications;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Tenancy;
using Sellora.NotificationService.Infrastructure.Persistence;

namespace Sellora.NotificationService.Infrastructure.Notifications;

/// <summary>US-E5-4: the company admin's alert address (tenant-scoped by the query filter and the token).</summary>
public sealed class NotificationSettingsService(
    NotificationDbContext db,
    ITenantContext tenant,
    ICallerIdentity caller,
    TimeProvider clock) : INotificationSettingsService
{
    public async Task<NotificationSettingsResponse> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await db.NotificationSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        return settings is null
            ? new NotificationSettingsResponse(null, null, null)
            : new NotificationSettingsResponse(settings.AlertEmail, settings.UpdatedAt, settings.UpdatedBy);
    }

    public async Task<NotificationSettingsResponse> UpdateAsync(
        UpdateNotificationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var companyId = tenant.CompanyId
            ?? throw new UnauthorizedAccessException("A valid company identifier was not found in the access token.");

        var settings = await db.NotificationSettings.SingleOrDefaultAsync(cancellationToken);

        if (settings is null)
        {
            settings = NotificationSettings.Create(companyId);
            db.NotificationSettings.Add(settings);
        }

        settings.SetAlertEmail(request.AlertEmail, caller.UserId ?? "unknown", clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);

        return new NotificationSettingsResponse(settings.AlertEmail, settings.UpdatedAt, settings.UpdatedBy);
    }
}
