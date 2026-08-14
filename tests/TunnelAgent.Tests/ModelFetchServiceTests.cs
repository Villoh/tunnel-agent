using TunnelAgent.Core.Engine;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

[Collection("UserEnvironment")]
public sealed class ModelFetchServiceTests : IDisposable
{
    private readonly InMemoryUserEnvironmentService _env = new();
    private readonly IUserEnvironmentService _previousEnv;

    public ModelFetchServiceTests()
    {
        _previousEnv = TunnelAgent.Infrastructure.Services.UserEnvironmentService.SetImplementation(_env);
    }

    public void Dispose()
    {
        TunnelAgent.Infrastructure.Services.UserEnvironmentService.SetImplementation(_previousEnv);
    }

    [Fact]
    public void ApiKeyForEngine_CliProxyApi_UsesCliproxyEnvVar()
    {
        _env.Set("TUNNEL_AGENT_CLIPROXY_API_KEY", "clip-key");
        _env.Set(NineRouterClientKeyService.EnvVarName, "router-key");
        _env.Set("TUNNEL_AGENT_PERPLEXITY_TOKEN", "pplx-token");

        Assert.Equal("clip-key", ModelFetchService.ApiKeyForEngine(EngineCatalog.CliProxyApi.Id));
        Assert.Equal("clip-key", ModelFetchService.ApiKeyForEngine(null));
    }

    [Fact]
    public void ApiKeyForEngine_Perplexity_ReturnsEmpty()
    {
        _env.Set("TUNNEL_AGENT_CLIPROXY_API_KEY", "clip-key");
        _env.Set("TUNNEL_AGENT_PERPLEXITY_TOKEN", "pplx-token");

        Assert.Equal("", ModelFetchService.ApiKeyForEngine(EngineCatalog.PerplexityWebUiScraper.Id));
    }

    [Fact]
    public void ApiKeyForEngine_NineRouter_UsesNineRouterEnvVar()
    {
        _env.Set("TUNNEL_AGENT_CLIPROXY_API_KEY", "clip-key");
        _env.Set(NineRouterClientKeyService.EnvVarName, "router-key");

        Assert.Equal("router-key", ModelFetchService.ApiKeyForEngine(EngineCatalog.NineRouter.Id));
    }

    private sealed class InMemoryUserEnvironmentService : IUserEnvironmentService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => _values[name] = value;
        public void Remove(string name) => _values.Remove(name);
    }
}
