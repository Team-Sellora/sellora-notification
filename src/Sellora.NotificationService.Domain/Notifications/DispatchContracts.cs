namespace Sellora.NotificationService.Domain.Notifications;

/// <summary>
/// US-E5-2: the one rendered message for a request. Rendered once, stored,
/// and sent byte-for-byte to every recipient — only the To header differs.
/// </summary>
/// <param name="BodySha256">Hex SHA-256 over subject, HTML and text, for dispute checks.</param>
public sealed record RenderedMessage(string Subject, string Html, string Text, string BodySha256);

/// <summary>The outcome of sending the rendered message to one recipient.</summary>
/// <param name="At">When the provider accepted it (sent) or the attempt failed.</param>
/// <param name="ProviderResponse">The provider's reply, e.g. "250 2.0.0 OK queued" or "550 5.1.1 user unknown".</param>
public sealed record RecipientSendResult(
    Guid NotificationRecipientId,
    SendOutcome Outcome,
    DateTimeOffset At,
    string? ProviderResponse,
    string? Error)
{
    public bool Succeeded => Outcome == SendOutcome.Sent;
}

/// <summary>US-E5-3-T1: how retries are spaced and when they stop.</summary>
/// <param name="MaxAttempts">Automatic attempts per recipient before it is PermanentlyFailed.</param>
/// <param name="Delay">Delay before the next attempt, given how many have failed so far.</param>
public sealed record RetryPolicy(int MaxAttempts, Func<int, TimeSpan> Delay);
