namespace Sellora.NotificationService.Infrastructure.Email;

/// <summary>
/// Bound to the <c>Smtp</c> section (App Service: <c>Smtp__Host</c>, …).
///
/// Local: Mailhog on localhost:1025, no auth, no TLS (docker-compose.yml).
/// Staging (Brevo SMTP relay): Host <c>smtp-relay.brevo.com</c>, Port 587,
/// EnableSsl true (STARTTLS), Username = the Brevo SMTP login, Password =
/// the Brevo SMTP key, From = a sender verified in Brevo.
/// </summary>
public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 1025;

    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>Must be a sender (or domain) verified in the provider, or it will be rejected.</summary>
    public string From { get; init; } = "noreply@sellora.local";

    public string FromName { get; init; } = "Sellora";

    /// <summary>True: TLS (STARTTLS on 587/25, implicit TLS on 465). False: plain (local sinks only).</summary>
    public bool EnableSsl { get; init; }

    public int TimeoutSeconds { get; init; } = 30;
}
