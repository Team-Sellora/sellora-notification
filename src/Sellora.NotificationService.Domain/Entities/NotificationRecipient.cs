using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>
/// US-E5-1: one addressee of a notification request — the shop or the
/// agency — with the name and email the event carried. Per-recipient
/// delivery state (so one failed send never resends the other) arrives
/// with US-E5-2.
/// </summary>
public sealed class NotificationRecipient : ITenantScoped
{
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;

    private NotificationRecipient()
    {
    }

    internal NotificationRecipient(Guid notificationRequestId, Guid companyId, NewRecipient recipient)
    {
        NotificationRecipientId = Guid.NewGuid();
        NotificationRequestId = notificationRequestId;
        CompanyId = companyId;
        Kind = recipient.Kind;
        RecipientId = recipient.RecipientId;
        Name = Trim(recipient.Name, MaxNameLength);
        Email = Trim(recipient.Email, MaxEmailLength);
    }

    public Guid NotificationRecipientId { get; private set; }

    public Guid NotificationRequestId { get; private set; }

    public Guid CompanyId { get; private set; }

    public RecipientKind Kind { get; private set; }

    /// <summary>The shop ID or agency ID.</summary>
    public Guid RecipientId { get; private set; }

    public string? Name { get; private set; }

    public string? Email { get; private set; }

    private static string? Trim(string? value, int max)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
