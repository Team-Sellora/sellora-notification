using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Sellora.NotificationService.Application.Dispatch;

namespace Sellora.NotificationService.Infrastructure.Email;

/// <summary>
/// Sends one email over SMTP with MailKit — the same code for Mailhog
/// locally and Brevo's SMTP relay in staging; only configuration differs.
/// A fresh connection per message: MailKit's client is not thread-safe, and
/// the dispatcher sends to the shop and the agency at the same time.
/// </summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task<string?> SendAsync(OutgoingEmail email, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var message = BuildMessage(email, settings);

        using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };

        var security = !settings.EnableSsl
            ? SecureSocketOptions.None
            : settings.Port == 465
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

        await client.ConnectAsync(settings.Host, settings.Port, security, cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
        }

        // The server's acceptance line, e.g. "250 2.0.0 OK: queued as <id>".
        var response = await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        return string.IsNullOrWhiteSpace(response) ? message.MessageId : response;
    }

    /// <summary>
    /// Same subject and bodies for everyone; only To (and the per-message
    /// Message-Id MimeKit generates) differ between the shop's copy and the
    /// agency's.
    /// </summary>
    public static MimeMessage BuildMessage(OutgoingEmail email, SmtpOptions settings)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(settings.FromName, settings.From));
        message.To.Add(new MailboxAddress(email.ToName ?? string.Empty, email.ToAddress));
        message.Subject = email.Subject;

        foreach (var (name, value) in email.Headers)
        {
            message.Headers.Add(name, value);
        }

        message.Body = new BodyBuilder { HtmlBody = email.Html, TextBody = email.Text }.ToMessageBody();
        return message;
    }
}
