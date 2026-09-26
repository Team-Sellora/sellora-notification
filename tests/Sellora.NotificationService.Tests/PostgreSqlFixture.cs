using Microsoft.EntityFrameworkCore;
using Sellora.NotificationService.Domain.Tenancy;
using Sellora.NotificationService.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace Sellora.NotificationService.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlCollection : ICollectionFixture<PostgreSqlFixture>
{
    public const string Name = "PostgreSQL integration tests";
}

public sealed class PostgreSqlFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("notification_tests")
        .WithUsername("sellora")
        .WithPassword("sellora_test_pw")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // MigrateAsync (not EnsureCreated) so the real migration is exercised.
        await using var db = CreateContext(null);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public NotificationDbContext CreateContext(Guid? companyId) => new(
        new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(ConnectionString)
            .EnableSensitiveDataLogging()
            .Options,
        new TenantStub(companyId));
}

internal sealed class TenantStub(Guid? companyId) : ITenantContext
{
    public Guid? CompanyId { get; } = companyId;
}
