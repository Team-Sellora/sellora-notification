using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>
/// US-E5-3 DoD 5: one send attempt to one recipient — when, how it ended,
/// what the provider said, and what triggered it. Append-only: a resend
/// adds rows, it never overwrites the failures before it.
/// </summary>
public sealed class NotificationAttempt : ITenantScoped
{
    public const int MaxTextLength = 1000;

    private NotificationAttempt()
    {
    }

    internal NotificationAttempt(
        NotificationRecipient recipient,
        int attemptNumber,
        RecipientSendResult result,
        AttemptTrigger trigger,
        string? triggeredBy)
    {
        NotificationAttemptId = Guid.NewGuid();
        NotificationRequestId = recipient.NotificationRequestId;
        NotificationRecipientId = recipient.NotificationRecipientId;
        CompanyId = recipient.CompanyId;
        RecipientKind = recipient.Kind;
        EmailAddress = recipient.Email;
        AttemptNumber = attemptNumber;
        AttemptedAt = result.At;
        Outcome = result.Outcome;
        ProviderResponse = Clip(result.ProviderResponse);
        Error = Clip(result.Error);
        Trigger = trigger;
        TriggeredBy = Clip(triggeredBy);
    }

    public Guid NotificationAttemptId { get; private set; }

    public Guid NotificationRequestId { get; private set; }

    public Guid NotificationRecipientId { get; private set; }

    public Guid CompanyId { get; private set; }

    public RecipientKind RecipientKind { get; private set; }

    /// <summary>The address used for this attempt (it can change on resend).</summary>
    public string? EmailAddress { get; private set; }

    /// <summary>1 for the first attempt to this recipient, 2 for the next, …</summary>
    public int AttemptNumber { get; private set; }

    public DateTimeOffset AttemptedAt { get; private set; }

    public SendOutcome Outcome { get; private set; }

    public string? ProviderResponse { get; private set; }

    public string? Error { get; private set; }

    public AttemptTrigger Trigger { get; private set; }

    /// <summary>The admin's user ID for a manual resend; null for automatic attempts.</summary>
    public string? TriggeredBy { get; private set; }

    private static string? Clip(string? value)
    {
        var trimmed = value?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length <= MaxTextLength ? trimmed : trimmed[..MaxTextLength];
    }
}
