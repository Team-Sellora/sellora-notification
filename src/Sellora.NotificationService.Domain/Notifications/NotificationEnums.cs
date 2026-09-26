namespace Sellora.NotificationService.Domain.Notifications;

/// <summary>
/// Where a notification request stands. US-E5-1 only creates Pending
/// requests; sending them (and the Sent / Failed states) is US-E5-2 / US-E5-3.
/// Stored as text.
/// </summary>
public enum NotificationStatus
{
    Pending = 1
}

/// <summary>Who a recipient is to the order. Stored as text.</summary>
public enum RecipientKind
{
    /// <summary>The shop owner of the order's shop.</summary>
    Shop = 1,

    /// <summary>The agency that fulfils the order.</summary>
    Agency = 2
}
