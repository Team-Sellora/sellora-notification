using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>
/// US-E5-4: per-company notification settings a company admin controls.
/// For now one thing: the address that receives company-level alerts
/// (low stock). Kept here, not in Organization, because only this service
/// sends to it.
/// </summary>
public sealed class NotificationSettings : ITenantScoped
{
    private NotificationSettings()
    {
    }

    public Guid CompanyId { get; private set; }

    public string? AlertEmail { get; private set; }

    public string UpdatedBy { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationSettings Create(Guid companyId) => new() { CompanyId = companyId };

    /// <summary>Sets or clears (null/blank) the alert address.</summary>
    public void SetAlertEmail(string? email, string updatedBy, DateTimeOffset now)
    {
        var trimmed = string.IsNullOrWhiteSpace(email) ? null : email.Trim();

        if (trimmed is not null && !EmailAddressRules.IsWellFormed(trimmed))
        {
            throw new ArgumentException($"'{trimmed}' is not a valid email address.", nameof(email));
        }

        AlertEmail = trimmed;
        UpdatedBy = updatedBy.Trim();
        UpdatedAt = now;
    }
}
