using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>
/// One addressee of a notification request — the shop or the agency — with
/// the name and email the event carried (US-E5-1) and its own delivery state
/// (US-E5-2), so a recipient that already received the message is never
/// sent it again when the other one is retried.
/// </summary>
public sealed class NotificationRecipient : ITenantScoped
{
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxErrorLength = 1000;
    public const int MaxProviderMessageIdLength = 300;

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
        DeliveryStatus = RecipientDeliveryStatus.Pending;
    }

    public Guid NotificationRecipientId { get; private set; }

    public Guid NotificationRequestId { get; private set; }

    public Guid CompanyId { get; private set; }

    public RecipientKind Kind { get; private set; }

    /// <summary>The shop ID or agency ID.</summary>
    public Guid RecipientId { get; private set; }

    public string? Name { get; private set; }

    public string? Email { get; private set; }

    public RecipientDeliveryStatus DeliveryStatus { get; private set; }

    /// <summary>When the provider accepted the message for this recipient.</summary>
    public DateTimeOffset? SentAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The provider's reply on acceptance (e.g. the SMTP queue ID), for tracing with Brevo.</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>Still owed the message and has an address to send it to.</summary>
    public bool NeedsSending =>
        Email is not null &&
        DeliveryStatus is RecipientDeliveryStatus.Pending or RecipientDeliveryStatus.Failed;

    internal void MarkUnaddressed()
    {
        if (Email is null && DeliveryStatus == RecipientDeliveryStatus.Pending)
        {
            DeliveryStatus = RecipientDeliveryStatus.Unaddressed;
        }
    }

    internal void Apply(RecipientSendResult result)
    {
        if (DeliveryStatus == RecipientDeliveryStatus.Sent)
        {
            // Never overwrite a delivery that already happened.
            return;
        }

        Attempts += 1;
        LastAttemptAt = result.At;

        if (result.Succeeded)
        {
            DeliveryStatus = RecipientDeliveryStatus.Sent;
            SentAt = result.At;
            ProviderMessageId = Trim(result.ProviderMessageId, MaxProviderMessageIdLength);
            LastError = null;
        }
        else
        {
            DeliveryStatus = RecipientDeliveryStatus.Failed;
            LastError = Trim(result.Error ?? "Unknown send failure.", MaxErrorLength);
        }
    }

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
