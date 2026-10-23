using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Sellora.NotificationService.Domain.Notifications;

namespace Sellora.NotificationService.Infrastructure.Email;

/// <summary>
/// US-E5-3-T2: decides whether an SMTP failure is worth retrying.
///
/// SMTP (RFC 5321) already encodes this: a 4xx reply is a temporary refusal
/// ("try later" — greylisting, throttling, mailbox busy) and a 5xx reply is
/// a permanent one ("user unknown", "invalid address"). The backlog's
/// "4xx throttling / 5xx provider error → retry" wording comes from HTTP
/// APIs, where 5xx means the provider is broken; over SMTP a 5xx at RCPT is
/// the synchronous hard bounce and must not use up the retry budget.
///
/// Two deliberate exceptions stay transient even on 5xx, because they are
/// our configuration rather than the recipient: the sender being refused
/// (Brevo sender not verified) and authentication failing (wrong SMTP key).
/// Fixing the settings lets the retries recover on their own.
/// </summary>
public static class SmtpFailureClassifier
{
    public static (SendOutcome Outcome, string Error, string? ProviderResponse) Classify(Exception exception)
    {
        switch (exception)
        {
            case SmtpCommandException command:
                var code = (int)command.StatusCode;
                var response = $"{code} {command.Message}".Trim();

                if (command.ErrorCode == SmtpErrorCode.SenderNotAccepted)
                {
                    return (SendOutcome.TransientFailure,
                        "The provider refused the sender address — check that Smtp:From is a verified sender.", response);
                }

                if (code >= 500)
                {
                    var reason = command.ErrorCode == SmtpErrorCode.RecipientNotAccepted
                        ? "The recipient's mail server rejected the address (hard bounce)."
                        : "The provider permanently rejected the message.";
                    return (SendOutcome.PermanentFailure, reason, response);
                }

                return (SendOutcome.TransientFailure, "The provider asked to try again later.", response);

            case AuthenticationException:
                return (SendOutcome.TransientFailure,
                    "SMTP authentication failed — check Smtp:Username and Smtp:Password.", exception.Message);

            case ParseException:
                return (SendOutcome.PermanentFailure, "The email address is not valid.", exception.Message);

            case SmtpProtocolException:
            case ServiceNotConnectedException:
            case ServiceNotAuthenticatedException:
            case SocketException:
            case IOException:
            case TimeoutException:
            case OperationCanceledException:
                return (SendOutcome.TransientFailure,
                    $"The mail provider could not be reached ({exception.GetType().Name}).", exception.Message);

            default:
                // Unknown: retry within the budget rather than give up on a guess.
                return (SendOutcome.TransientFailure, $"Unexpected send failure ({exception.GetType().Name}).", exception.Message);
        }
    }
}
