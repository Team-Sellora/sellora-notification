namespace Sellora.NotificationService.Domain.Tenancy;

/// <summary>The company the current request or background work acts for.</summary>
public interface ITenantContext
{
    Guid? CompanyId { get; }
}

/// <summary>An explicit tenant scope for trusted background processing (the consumer).</summary>
public interface ISystemTenantContext
{
    IDisposable BeginSystemTenantScope(Guid companyId);
}

public interface ITenantScoped
{
    Guid CompanyId { get; }
}
