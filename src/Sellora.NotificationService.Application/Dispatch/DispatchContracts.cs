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
/// Sends one email through the configured provider (Brevo SMTP relay in
/// staging, Mailhog/Mailpit locally). Returns the provider's acceptance
/// reference; throws when the provider does not accept the message.
/// </summary>
public interface IEmailSender
{
    Task<string?> SendAsync(OutgoingEmail email, CancellationToken cancellationToken);
}

/// <summary>Runs one dispatch pass: claims due requests and sends them.</summary>
public interface INotificationDispatcher
{
    /// <returns>How many requests were dispatched in this pass.</returns>
    Task<int> DispatchDueAsync(CancellationToken cancellationToken);
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

    public int RetryBaseSeconds { get; init; } = 60;

    public int RetryMaxSeconds { get; init; } = 1_800;

    /// <summary>1 min, 2 min, 4 min … capped at <see cref="RetryMaxSeconds"/>.</summary>
    public TimeSpan RetryDelay(int attempt)
    {
        var seconds = RetryBaseSeconds * Math.Pow(2, Math.Max(0, attempt - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, RetryMaxSeconds));
    }
}
