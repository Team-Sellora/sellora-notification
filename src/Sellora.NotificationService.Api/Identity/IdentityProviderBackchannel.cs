using System.Net.Security;

namespace Sellora.NotificationService.Api.Identity;

/// <summary>
/// The JWT backchannel for hosts that do not trust the shared WSO2 CA
/// (local and staging). A certificate that fails validation is accepted
/// only from the identity provider's own host and port, so the relaxation
/// never reaches any other server.
/// </summary>
public static class IdentityProviderBackchannel
{
    public static HttpClientHandler CreateHandler(string? metadataAddress)
    {
        Uri.TryCreate(metadataAddress, UriKind.Absolute, out var identityProvider);

        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, _, _, errors) =>
                IsTrusted(request.RequestUri, errors, identityProvider)
        };
    }

    public static bool IsTrusted(Uri? requestUri, SslPolicyErrors errors, Uri? identityProvider)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return identityProvider is not null
            && requestUri is not null
            && string.Equals(requestUri.Host, identityProvider.Host, StringComparison.OrdinalIgnoreCase)
            && requestUri.Port == identityProvider.Port;
    }
}
