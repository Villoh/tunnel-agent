using TunnelAgent.Infrastructure.Engine.Perplexity;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

[Collection("UserEnvironment")]
public sealed class PerplexityAccountCatalogServiceTests : IDisposable
{
    private readonly InMemoryUserEnvironmentService _env = new();
    private readonly TunnelAgent.Services.IUserEnvironmentService _previousEnv;

    public PerplexityAccountCatalogServiceTests()
    {
        _previousEnv = TunnelAgent.Infrastructure.Services.UserEnvironmentService.SetImplementation(_env);
    }

    public void Dispose()
    {
        TunnelAgent.Infrastructure.Services.UserEnvironmentService.SetImplementation(_previousEnv);
    }

    private static PerplexityAccountCatalogService CreateService(TestTempDirectory temp)
    {
        var dir = System.IO.Path.Combine(temp.Path, "perplexity-accounts");
        return new PerplexityAccountCatalogService(new AccountService(dir));
    }

    private sealed class InMemoryUserEnvironmentService : TunnelAgent.Services.IUserEnvironmentService
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values = new();
        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => _values[name] = value;
        public void Remove(string name) => _values.Remove(name);
    }

    [Fact]
    public async Task AddAsync_FirstAccount_BecomesDefault()
    {
        using var temp = new TestTempDirectory();
        var service = CreateService(temp);

        await service.AddAsync("Primary", "token-1");

        var account = Assert.Single(service.List());
        Assert.True(account.IsDefault);
        Assert.Equal("Primary", account.Label);
    }

    [Fact]
    public async Task SetDefaultAsync_UpdatesDefaultSelection()
    {
        using var temp = new TestTempDirectory();
        var service = CreateService(temp);

        await service.AddAsync("One", "token-1");
        await service.AddAsync("Two", "token-2");
        var second = service.List().Single(a => a.Label == "Two");

        await service.SetDefaultAsync(second.Id);

        var accounts = service.List();
        Assert.Equal("Two", Assert.Single(accounts, a => a.IsDefault).Label);
    }

    [Fact]
    public async Task UpdateLabelAsync_ChangesPersistedLabel()
    {
        using var temp = new TestTempDirectory();
        var service = CreateService(temp);
        await service.AddAsync("Old", "token-1");
        var account = Assert.Single(service.List());

        await service.UpdateLabelAsync(account.Id, "New");

        Assert.Equal("New", Assert.Single(service.List()).Label);
    }

    [Fact]
    public async Task RemoveAsync_DefaultAccount_PromotesNext()
    {
        using var temp = new TestTempDirectory();
        var service = CreateService(temp);

        await service.AddAsync("One", "token-1");
        await service.AddAsync("Two", "token-2");
        var first = service.List().First(a => a.IsDefault);

        await service.RemoveAsync(first.Id);

        var remaining = service.List();
        var single = Assert.Single(remaining);
        Assert.True(single.IsDefault);
    }

    [Fact]
    public async Task RemoveAllAsync_ClearsStaleProcessEnvironmentToken()
    {
        using var temp = new TestTempDirectory();
        var service = CreateService(temp);
        var envName = PerplexityAccountCatalogService.EnvVarName;
        _env.Set(envName, "stale-token");
        await service.AddAsync("Primary", "token-1");
        Assert.Equal("token-1", _env.Get(envName));

        await service.RemoveAllAsync();

        Assert.Null(_env.Get(envName));
        Assert.Empty(service.List());
    }

    [Fact]
    public async Task MigrateFromSettings_MovesLegacyAccounts()
    {
        using var temp = new TestTempDirectory();
        var service = CreateService(temp);

        var legacy = new System.Collections.Generic.List<PerplexityAccountSettings>
        {
            new() { Id = "abc123", Label = "Legacy", SessionToken = "tok", IsDefault = true }
        };

        await service.InitializeAsync(legacy);

        var account = Assert.Single(service.List());
        Assert.Equal("Legacy", account.Label);
        Assert.Equal("abc123", account.Id);
    }
}
