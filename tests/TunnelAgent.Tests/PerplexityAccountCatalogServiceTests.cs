using System.Linq;
using System.Threading.Tasks;
using TunnelAgent.Infrastructure.Engine.Perplexity;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class PerplexityAccountCatalogServiceTests
{
    private static PerplexityAccountCatalogService CreateService(TestTempDirectory temp)
    {
        var dir = System.IO.Path.Combine(temp.Path, "perplexity-accounts");
        return new PerplexityAccountCatalogService(new AccountService(dir));
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
