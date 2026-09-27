using Sellora.NotificationService.Domain.Notifications;
using Sellora.NotificationService.Domain.Tenancy;

namespace Sellora.NotificationService.Domain.Entities;

/// <summary>
/// One addressee of a notification request — the shop or the agency — with
/// the name and email the event carried (US-E5-1), its own delivery state
/// (US-E5-2) and its own retry budget (US-E5-3), so a recipient that already
/// received the message is never sent it again when the other is retried.
/// </summary>
public sealed class NotificationRecipient : ITenantScoped
{
    public const int MaxNameLength = 200;
    public const int MaxEmailLength = 320;
    public const int MaxErrorLength = 1000;
    public const int MaxProviderMessageIdLength = 300;

    public const string NoAddressReason = "No email address is on record for this recipient.";

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

    /// <summary>Every attempt ever made to this recipient, automatic or manual.</summary>
    public int Attempts { get; private set; }

    /// <summary>
    /// US-E5-3: attempts counted against the retry budget. Only transient
    /// failures use it; a manual resend starts a fresh budget.
    /// </summary>
    public int FailuresSinceReset { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>The provider's last reply for this recipient (acceptance or rejection).</summary>
    public string? ProviderMessageId { get; private set; }

    /// <summary>Still owed the message, has an address, and is not given up on.</summary>
    public bool NeedsSending =>
        Email is not null &&
        DeliveryStatus is RecipientDeliveryStatus.Pending or RecipientDeliveryStatus.Failed;

    /// <summary>Cannot be reached without someone acting (no address, rejected, out of retries).</summary>
    public bool IsStuck =>
        DeliveryStatus is RecipientDeliveryStatus.Unaddressed or RecipientDeliveryStatus.PermanentlyFailed;

    internal void MarkUnaddressed()
    {
        if (Email is null && DeliveryStatus == RecipientDeliveryStatus.Pending)
        {
            DeliveryStatus = RecipientDeliveryStatus.Unaddressed;
            LastError = NoAddressReason;
        }
    }

    /// <summary>
    /// Applies one attempt's result. A transient failure keeps the recipient
    /// in the retry path until the budget runs out; a permanent failure
    /// skips the budget and stops at once.
    /// </summary>
    /// <returns>The attempt number just made, or null if nothing changed (already sent).</returns>
    internal int? Apply(RecipientSendResult result, int maxAttempts)
    {
        if (DeliveryStatus == RecipientDeliveryStatus.Sent)
        {
            // Never overwrite a delivery that already happened.
            return null;
        }

        Attempts += 1;
        LastAttemptAt = result.At;
        ProviderMessageId = Trim(result.ProviderResponse, MaxProviderMessageIdLength) ?? ProviderMessageId;

        switch (result.Outcome)
        {
            case SendOutcome.Sent:
                DeliveryStatus = RecipientDeliveryStatus.Sent;
                SentAt = result.At;
                LastError = null;
                FailuresSinceReset = 0;
                break;

            case SendOutcome.PermanentFailure:
                // Retrying a rejected address wastes the budget and delays others.
                DeliveryStatus = RecipientDeliveryStatus.PermanentlyFailed;
                LastError = Trim(result.Error ?? "Permanently rejected by the mail provider.", MaxErrorLength);
                break;

            default:
                FailuresSinceReset += 1;
                var error = result.Error ?? "Temporary send failure.";

                if (FailuresSinceReset >= maxAttempts)
                {
                    DeliveryStatus = RecipientDeliveryStatus.PermanentlyFailed;
                    LastError = Trim($"Gave up after {FailuresSinceReset} attempts. Last error: {error}", MaxErrorLength);
                }
                else
                {
                    DeliveryStatus = RecipientDeliveryStatus.Failed;
                    LastError = Trim(error, MaxErrorLength);
                }

                break;
        }

        return Attempts;
    }

    /// <summary>
    /// US-E5-3 manual resend: back in the queue with a fresh retry budget,
    /// optionally at a corrected address. History is kept, not reset.
    /// </summary>
    internal void ResetForResend(string? correctedEmail)
    {
        if (DeliveryStatus == RecipientDeliveryStatus.Sent)
        {
            return;
        }

        if (correctedEmail is not null)
        {
            Email = Trim(correctedEmail, MaxEmailLength);
        }

        DeliveryStatus = RecipientDeliveryStatus.Pending;
        FailuresSinceReset = 0;
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
