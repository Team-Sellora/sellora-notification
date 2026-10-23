using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Application.Dispatch;

/// <summary>One email to one recipient — the shared rendered message plus that recipient's address.</summary>
public sealed record OutgoingEmail(
    string ToAddress,
    string? ToName,
    string Subject,
    string Html,
    string Text,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// US-E5-3-T2: the provider did not accept a message, and whether trying
/// again could help. The dispatcher records <see cref="ProviderResponse"/>
/// on the attempt so an admin sees exactly what the provider said.
/// </summary>
public sealed class EmailSendException(SendOutcome outcome, string message, string? providerResponse, Exception? inner = null)
    : Exception(message, inner)
{
    /// <summary>TransientFailure or PermanentFailure.</summary>
    public SendOutcome Outcome { get; } = outcome == SendOutcome.Sent
        ? throw new ArgumentException("A send exception cannot be a success.", nameof(outcome))
        : outcome;

    public string? ProviderResponse { get; } = providerResponse;
}

/// <summary>
/// Sends one email through the configured provider (Brevo SMTP relay in
/// staging, Mailhog/Mailpit locally). Returns the provider's acceptance
/// reply; throws <see cref="EmailSendException"/> (classified transient or
/// permanent) when the provider does not accept the message.
/// </summary>
public interface IEmailSender
{
    Task<string?> SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

/// <summary>Runs dispatch: the periodic pass over due requests, or one request now.</summary>
public interface INotificationDispatcher
{
    /// <returns>How many requests were dispatched in this pass.</returns>
    Task<int> DispatchDueAsync(CancellationToken cancellationToken);

    /// <summary>
    /// US-E5-3 manual resend: claims and dispatches one request immediately.
    /// Returns false when another dispatcher holds it right now.
    /// </summary>
    Task<bool> DispatchNowAsync(
        Guid notificationRequestId,
        AttemptTrigger trigger,
        string? triggeredBy,
        CancellationToken cancellationToken);
}

/// <summary>Bound to the <c>Dispatch</c> section.</summary>
public sealed class DispatchOptions
{
    public const string SectionName = "Dispatch";

    public bool Enabled { get; init; } = true;

    public int PollIntervalSeconds { get; init; } = 5;

    public int BatchSize { get; init; } = 10;

    /// <summary>How long a claimed request is reserved for one dispatcher (above the SMTP timeout).</summary>
    public int LeaseSeconds { get; init; } = 120;

    /// <summary>
    /// The agreed "simultaneous": the largest acceptable gap between the two
    /// recipients' send timestamps. Exceeding it is logged as a warning and
    /// reported by the read API; it does not stop delivery.
    /// </summary>
    public int SimultaneityToleranceMilliseconds { get; init; } = 2_000;

    /// <summary>US-E5-3-T1: first retry after about this long; doubles each time.</summary>
    public int RetryBaseSeconds { get; init; } = 30;

    /// <summary>No single wait is longer than this.</summary>
    public int RetryMaxSeconds { get; init; } = 1_800;

    /// <summary>
    /// Automatic attempts per recipient before it is PermanentlyFailed (the
    /// dead-letter state). Defaults to 5: roughly 30 s, 1, 2 and 4 min apart.
    /// </summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// ± fraction applied to every delay (0.2 = ±20 %), so requests that failed
    /// together during an outage do not all retry in the same second when it ends.
    /// </summary>
    public double JitterRatio { get; init; } = 0.2;

    /// <summary>
    /// Delay before the next try after <paramref name="failures"/> failures:
    /// base × 2^(failures−1), capped, then jittered by ±JitterRatio.
    /// </summary>
    /// <param name="randomUnit">A number in [0, 1); 0.5 means no jitter.</param>
    public TimeSpan RetryDelay(int failures, double randomUnit)
    {
        var baseSeconds = Math.Max(1, RetryBaseSeconds);
        var exponential = baseSeconds * Math.Pow(2, Math.Max(0, failures - 1));
        var capped = Math.Min(exponential, Math.Max(baseSeconds, RetryMaxSeconds));
        var ratio = Math.Clamp(JitterRatio, 0, 0.9);
        var factor = 1 + ratio * (2 * Math.Clamp(randomUnit, 0, 1) - 1);
        return TimeSpan.FromSeconds(capped * factor);
    }

    public RetryPolicy Policy(Func<double> random) =>
        new(Math.Max(1, MaxAttempts), failures => RetryDelay(failures, random()));
}
