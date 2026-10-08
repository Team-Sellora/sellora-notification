using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using Sellora.NotificationService.Application.Dispatch;
using Sellora.NotificationService.Domain.Notifications;

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

        // US-E5-3-D1: a repeatable outage for QA, without stopping containers.
        if (settings.SimulateOutage)
        {
            throw new EmailSendException(
                SendOutcome.TransientFailure,
                "Simulated mail outage (Smtp:SimulateOutage = true).",
                "421 4.3.2 Service not available (simulated)");
        }

        // US-E5-3-T2: a malformed address is rejected every time — never spend retries on it.
        if (!EmailAddressRules.IsWellFormed(email.ToAddress))
        {
            throw new EmailSendException(
                SendOutcome.PermanentFailure,
                $"'{email.ToAddress}' is not a valid email address.",
                null);
        }

        try
        {
            return await SendOverSmtpAsync(email, settings, cancellationToken);
        }
        catch (Exception exception) when (exception is not EmailSendException &&
                                          !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            var (outcome, error, providerResponse) = SmtpFailureClassifier.Classify(exception);
            throw new EmailSendException(outcome, error, providerResponse, exception);
        }
    }

    private static async Task<string?> SendOverSmtpAsync(
        OutgoingEmail email,
        SmtpOptions settings,
        CancellationToken cancellationToken)
    {
        var message = BuildMessage(email, settings);

        using var client = new SmtpClient { Timeout = settings.TimeoutSeconds * 1000 };

        await client.ConnectAsync(settings.Host, settings.Port, SocketSecurity(settings), cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, settings.Password ?? string.Empty, cancellationToken);
        }

        // The server's acceptance line, e.g. "250 2.0.0 OK: queued as <id>".
        var response = await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(quit: true, cancellationToken);

        return string.IsNullOrWhiteSpace(response) ? message.MessageId : response;
    }

    /// <summary>No TLS when SSL is off; implicit TLS on port 465; STARTTLS otherwise.</summary>
    private static SecureSocketOptions SocketSecurity(SmtpOptions settings)
    {
        if (!settings.EnableSsl)
        {
            return SecureSocketOptions.None;
        }

        return settings.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
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
