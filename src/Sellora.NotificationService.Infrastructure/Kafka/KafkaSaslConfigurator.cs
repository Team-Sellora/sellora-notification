using Confluent.Kafka;

namespace Sellora.NotificationService.Infrastructure.Kafka;

/// <summary>
/// Copied from sellora-order / sellora-inventory. Confluent Cloud needs
/// SASL_SSL with PLAIN; a local broker needs nothing, so SASL is only
/// applied when a username is configured.
/// </summary>
public static class KafkaSaslConfigurator
{
    public static void Apply(ClientConfig config, string? username, string? password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return;
        }

        config.SecurityProtocol = SecurityProtocol.SaslSsl;
        config.SaslMechanism = SaslMechanism.Plain;
        config.SaslUsername = username;
        config.SaslPassword = password;
    }
}
