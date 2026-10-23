using Microsoft.EntityFrameworkCore;
using Sellora.NotificationService.Domain.Entities;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Infrastructure.Persistence;

public sealed class NotificationDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public NotificationDbContext(DbContextOptions<NotificationDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<NotificationRequest> NotificationRequests => Set<NotificationRequest>();

    public DbSet<NotificationRecipient> NotificationRecipients => Set<NotificationRecipient>();

    /// <summary>US-E5-3: every send attempt.</summary>
    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();

    /// <summary>US-E5-4: names and addresses learned from order events.</summary>
    public DbSet<DirectoryEntry> DirectoryEntries => Set<DirectoryEntry>();

    /// <summary>US-E5-4: per-company settings (the alert address).</summary>
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationDbContext).Assembly);

        // Same tenancy rule as every Sellora service: reads only ever see the
        // caller's company. The consumer writes with the event's companyId and
        // checks idempotency with IgnoreQueryFilters (event IDs are global).
        modelBuilder.Entity<NotificationRequest>()
            .HasQueryFilter(request =>
                _tenantContext.CompanyId != null &&
                request.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<NotificationRecipient>()
            .HasQueryFilter(recipient =>
                _tenantContext.CompanyId != null &&
                recipient.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<NotificationAttempt>()
            .HasQueryFilter(attempt =>
                _tenantContext.CompanyId != null &&
                attempt.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<DirectoryEntry>()
            .HasQueryFilter(entry =>
                _tenantContext.CompanyId != null &&
                entry.CompanyId == _tenantContext.CompanyId);

        modelBuilder.Entity<NotificationSettings>()
            .HasQueryFilter(settings =>
                _tenantContext.CompanyId != null &&
                settings.CompanyId == _tenantContext.CompanyId);
    }
}
