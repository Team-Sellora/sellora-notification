namespace Sellora.NotificationService.Domain.Notifications;

/// <summary>
/// US-E5-2: the one rendered message for a request. Rendered once, stored,
/// and sent byte-for-byte to every recipient — only the To header differs.
/// </summary>
/// <param name="BodySha256">Hex SHA-256 over subject, HTML and text, for dispute checks.</param>
public sealed record RenderedMessage(string Subject, string Html, string Text, string BodySha256);

/// <summary>The outcome of sending the rendered message to one recipient.</summary>
/// <param name="At">When the provider accepted it (success) or the attempt failed.</param>
public sealed record RecipientSendResult(
    Guid NotificationRecipientId,
    bool Succeeded,
    DateTimeOffset At,
    string? ProviderMessageId,
    string? Error);
