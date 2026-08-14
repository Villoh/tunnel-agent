using TunnelAgent.Services;
using Xunit;

using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine;
namespace TunnelAgent.Tests;

public sealed class EngineRegistryAndPerplexityTests
{
    [Fact]
    public async Task SettingsService_LoadAsync_SeedsKnownEngines()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));

        await settings.LoadAsync();

        var cli = settings.Current.GetOrAddEngine(EngineCatalog.CliProxyApi.Id, 0);
        var perplexity = settings.Current.GetOrAddEngine(EngineCatalog.PerplexityWebUiScraper.Id, 0);
        var nineRouter = settings.Current.GetOrAddEngine(EngineCatalog.NineRouter.Id, 0);
        Assert.Equal(8317, cli.Port);
        Assert.Equal(8327, perplexity.Port);
        Assert.Equal(20128, nineRouter.Port);
    }

    [Fact]
    public async Task EngineRegistryService_ExposesBothManagedEngines()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();

        var registry = new EngineRegistryService(settings);

        Assert.Equal(2, registry.Engines.Count);
        Assert.IsType<TunnelAgent.Infrastructure.Engine.CliProxy.EngineService>(registry.Get("cliproxyapi"));
        Assert.IsType<TunnelAgent.Infrastructure.Engine.Perplexity.EngineService>(registry.Get("perplexity-webui-scraper"));
    }

    [Fact]
    public void AccountService_AddSetDefaultRemove_Works()
    {
        using var temp = new TestTempDirectory();
        var accountsDir = System.IO.Path.Combine(temp.Path, "perplexity-accounts");
        var service = new TunnelAgent.Infrastructure.Engine.Perplexity.AccountService(accountsDir);

        var first = service.Add("Primary", "token-1");
        var second = service.Add("Backup", "token-2");
        var changed = service.SetDefault(second.Id);
        var removed = service.Remove(second.Id);

        Assert.True(changed);
        Assert.True(removed);
        Assert.Equal(first.Id, service.GetDefault()!.Id);
        Assert.Single(service.List());
    }
}
