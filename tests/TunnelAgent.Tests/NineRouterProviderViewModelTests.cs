using TunnelAgent.Services;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Tests;

public sealed class NineRouterProviderViewModelTests
{
    [Fact]
    public void MatchesAuthFilter_OAuth_ReturnsOnlyOAuthProviders()
    {
        var oauth = Provider(NineRouterAuthModes.OAuth);
        var apiKey = Provider(NineRouterAuthModes.ApiKey);

        Assert.True(oauth.MatchesAuthFilter("OAuth"));
        Assert.False(apiKey.MatchesAuthFilter("OAuth"));
    }

    [Fact]
    public void MatchesAuthFilter_ApiKey_IncludesCookieProviders()
    {
        var apiKey = Provider(NineRouterAuthModes.ApiKey);
        var cookie = Provider(NineRouterAuthModes.Cookie);
        var oauth = Provider(NineRouterAuthModes.OAuth);

        Assert.True(apiKey.MatchesAuthFilter("API Key"));
        Assert.True(cookie.MatchesAuthFilter("API Key"));
        Assert.False(oauth.MatchesAuthFilter("API Key"));
    }

    [Fact]
    public void MatchesAuthFilter_Both_IncludesOAuthAndStoredCredentials()
    {
        var oauth = Provider(NineRouterAuthModes.OAuth);
        var apiKey = Provider(NineRouterAuthModes.ApiKey);
        var noAuth = Provider(NineRouterAuthModes.NoAuth);

        Assert.True(oauth.MatchesAuthFilter("Both"));
        Assert.True(apiKey.MatchesAuthFilter("Both"));
        Assert.False(noAuth.MatchesAuthFilter("Both"));
    }

    private static NineRouterProviderViewModel Provider(NineRouterAuthModes authModes) =>
        new(new NineRouterProviderOption("test", "Test", authModes, NineRouterOAuthFlow.None));
}
