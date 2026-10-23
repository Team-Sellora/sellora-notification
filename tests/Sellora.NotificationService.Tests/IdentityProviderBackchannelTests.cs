using System.Net.Security;
using Sellora.NotificationService.Api.Identity;

namespace Sellora.NotificationService.Tests;

/// <summary>The relaxed backchannel trusts a failing certificate only from the identity provider itself.</summary>
public sealed class IdentityProviderBackchannelTests
{
    private static readonly Uri IdentityProvider =
        new("https://13.61.228.129:9443/oauth2/token/.well-known/openid-configuration");

    [Fact]
    public void A_valid_certificate_is_always_trusted()
    {
        Assert.True(IdentityProviderBackchannel.IsTrusted(
            new Uri("https://example.org/"), SslPolicyErrors.None, identityProvider: null));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    public void A_failing_certificate_is_trusted_from_the_identity_provider(SslPolicyErrors errors)
    {
        Assert.True(IdentityProviderBackchannel.IsTrusted(
            new Uri("https://13.61.228.129:9443/oauth2/jwks"), errors, IdentityProvider));
    }

    [Theory]
    [InlineData("https://example.org/oauth2/jwks")]
    [InlineData("https://13.61.228.129:8443/oauth2/jwks")]
    public void A_failing_certificate_is_refused_from_any_other_host(string requestUri)
    {
        Assert.False(IdentityProviderBackchannel.IsTrusted(
            new Uri(requestUri), SslPolicyErrors.RemoteCertificateChainErrors, IdentityProvider));
    }

    [Fact]
    public void A_failing_certificate_is_refused_when_no_identity_provider_is_configured()
    {
        Assert.False(IdentityProviderBackchannel.IsTrusted(
            new Uri("https://13.61.228.129:9443/oauth2/jwks"), SslPolicyErrors.RemoteCertificateChainErrors, null));
    }

    [Fact]
    public void A_failing_certificate_is_refused_without_a_request_uri()
    {
        Assert.False(IdentityProviderBackchannel.IsTrusted(
            null, SslPolicyErrors.RemoteCertificateChainErrors, IdentityProvider));
    }

    [Theory]
    [InlineData("https://13.61.228.129:9443/oauth2/token/.well-known/openid-configuration", true)]
    [InlineData(null, false)]
    [InlineData("not a url", false)]
    public void The_handler_relaxes_validation_only_for_the_configured_identity_provider(
        string? metadataAddress,
        bool trusted)
    {
        using var handler = IdentityProviderBackchannel.CreateHandler(metadataAddress);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://13.61.228.129:9443/oauth2/jwks");

        var callback = handler.ServerCertificateCustomValidationCallback;

        Assert.NotNull(callback);
        Assert.Equal(trusted, callback(request, null, null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.True(callback(request, null, null, SslPolicyErrors.None));
    }
}
