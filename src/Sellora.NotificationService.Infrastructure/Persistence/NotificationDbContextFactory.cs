using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Infrastructure.Persistence;

/// <summary>Lets <c>dotnet ef</c> build the context without starting the host (no Kafka, no JWT).</summary>
public sealed class NotificationDbContextFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5437;Database=notification_db;Username=sellora;Password=sellora_dev_pass";

        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new NotificationDbContext(options, new NoTenant());
    }

    private sealed class NoTenant : ITenantContext
    {
        public Guid? CompanyId => null;
    }
}
