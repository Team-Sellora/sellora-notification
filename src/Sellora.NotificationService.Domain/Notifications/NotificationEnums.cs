namespace Sellora.NotificationService.Domain.Notifications;

/// <summary>
/// Where a notification request stands. Stored as text.
///   Pending        — stored (US-E5-1), not yet delivered to anyone
///   Sent           — delivered to every recipient (US-E5-2)
///   PartiallySent  — delivered to some; only the rest are retried (US-E5-2 / E5-3)
/// A terminal Failed state and the admin failure list are US-E5-3.
/// </summary>
public enum NotificationStatus
{
    Pending = 1,
    Sent = 2,
    PartiallySent = 3
}

/// <summary>Who a recipient is to the order. Stored as text.</summary>
public enum RecipientKind
{
    /// <summary>The shop owner of the order's shop.</summary>
    Shop = 1,

    /// <summary>The agency that fulfils the order.</summary>
    Agency = 2
}

/// <summary>US-E5-2: delivery state of one recipient. Stored as text.</summary>
public enum RecipientDeliveryStatus
{
    /// <summary>Not attempted yet.</summary>
    Pending = 1,

    /// <summary>Accepted by the mail provider; never sent again.</summary>
    Sent = 2,

    /// <summary>Last attempt failed; retried on the next attempt.</summary>
    Failed = 3,

    /// <summary>No email address was known; cannot be sent (reported by US-E5-3).</summary>
    Unaddressed = 4
}
