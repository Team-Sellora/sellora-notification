namespace Sellora.NotificationService.Domain.Notifications;

/// <summary>
/// Where a notification request stands. Stored as text.
///   Pending            — stored (US-E5-1), not attempted yet
///   Sent               — delivered to every recipient (US-E5-2)
///   PartiallySent      — delivered to some; the rest are retried (US-E5-2/3)
///   Failed             — nobody has it yet; a retry is scheduled (US-E5-3)
///   PermanentlyFailed  — someone cannot be reached: a permanent rejection,
///                        no address, or the retry budget ran out. No more
///                        automatic retries; an admin can resend (US-E5-3)
/// </summary>
public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    PartiallySent = 3,
    Failed = 4,
    PermanentlyFailed = 5
}

/// <summary>Who a recipient is to the order. Stored as text.</summary>
public enum RecipientKind
{
    /// <summary>The shop owner of the order's shop.</summary>
    Shop = 1,

    /// <summary>The agency that fulfils the order.</summary>
    Agency = 2,

    /// <summary>
    /// US-E5-4: the company's alert address (set by a company admin in
    /// notification settings) — who hears about low stock.
    /// </summary>
    CompanyAdmin = 3
}

/// <summary>Delivery state of one recipient. Stored as text.</summary>
public enum RecipientDeliveryStatus
{
    /// <summary>Not attempted yet (or reset by a manual resend).</summary>
    Pending = 1,

    /// <summary>Accepted by the mail provider; never sent again.</summary>
    Sent = 2,

    /// <summary>Last attempt failed transiently; retried with back-off.</summary>
    Failed = 3,

    /// <summary>No email address was known; cannot be sent until one is given on resend.</summary>
    Unaddressed = 4,

    /// <summary>Permanently rejected, or out of retries. Only a manual resend tries again.</summary>
    PermanentlyFailed = 5
}

/// <summary>US-E5-3-T2: what one send attempt came to. Stored as text.</summary>
public enum SendOutcome
{
    Sent = 1,

    /// <summary>Timeouts, connection failures, 4xx / throttling: worth retrying.</summary>
    TransientFailure = 2,

    /// <summary>Invalid address, mailbox rejected (5xx): retrying cannot help.</summary>
    PermanentFailure = 3
}

/// <summary>What started an attempt. Stored as text.</summary>
public enum AttemptTrigger
{
    /// <summary>The dispatcher: first send or an automatic retry.</summary>
    Automatic = 1,

    /// <summary>A company admin pressed Resend.</summary>
    ManualResend = 2
}
