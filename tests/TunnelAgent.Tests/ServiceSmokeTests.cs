using System.Collections.ObjectModel;
using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class ServiceSmokeTests
{
    [Fact]
    public void ProviderIconRegistry_KnownAndUnknownProviders_ReturnExpectedMetadata()
    {
        var claude = ProviderIconRegistry.Get("claude");
        var unknown = ProviderIconRegistry.Get("unknown-provider");
        var kimi = ProviderIconRegistry.Get("moonshot");

        Assert.Equal(PackIconSimpleIconsKind.Claude, claude.IconKind);
        Assert.Equal("#D97757", claude.LogoColor);
        Assert.Equal(PackIconSimpleIconsKind.OpenAi, unknown.IconKind);
        Assert.Equal("#555555", unknown.LogoColor);
        Assert.NotNull(kimi.CustomIconData);
    }

    [Fact]
    public void OAuthService_IsOAuthProvider_RecognizesSupportedProviders()
    {
        Assert.True(OAuthService.IsOAuthProvider("claude"));
        Assert.True(OAuthService.IsOAuthProvider("codex"));
        Assert.False(OAuthService.IsOAuthProvider("local-ai"));
    }

    [Fact]
    public async Task OAuthService_ConnectAsync_UnsupportedProvider_ReturnsFailureWithoutStartingProcess()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        using var service = new OAuthService(config);

        var result = await service.ConnectAsync("local-ai");

        Assert.False(result.Success);
        Assert.Equal(OAuthConnectStatus.NotSupported, result.Status);
    }

    [Fact]
    public async Task ProcessService_StopAsync_SetsStoppedAndRaisesStateChanged()
    {
        var service = new ProcessService();
        var raised = false;
        service.StateChanged += (_, _) => raised = true;

        await service.StopAsync();

        Assert.Equal(EngineState.Stopped, service.State);
        Assert.True(raised);
    }

    [Fact]
    public async Task ModelFetchService_UnreachableProxy_CompletesWithoutMutatingGroups()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Port = 1;
        var service = new ModelFetchService(settings);
        var groups = new ObservableCollection<AvailableModelGroupViewModel>
        {
            new("Existing", "existing", PackIconSimpleIconsKind.OpenAi, "#000000")
        };

        await service.FetchAndApplyAsync(groups, 1, null);

        var group = Assert.Single(groups);
        Assert.Equal("Existing", group.ProviderName);
    }

    [Fact]
    public async Task QuotaFetchService_UnsupportedProvider_CompletesWithoutQuotaChanges()
    {
        using var temp = new TestTempDirectory();
        var service = new QuotaFetchService(temp.Path);
        var provider = new ProviderViewModel("local-ai", "Local AI", PackIconSimpleIconsKind.OpenAi, "#000000", "Custom");
        var account = new ProviderAccountViewModel("local-ai", "test-key", "Primary", isDisabled: false);
        provider.Accounts.Add(account);

        await service.FetchAndApplyAsync(provider);

        Assert.Empty(account.QuotaBars);
    }

    [Fact]
    public void UserEnvironmentService_Remove_ClearsStaleProcessFallback()
    {
        var name = $"TUNNEL_AGENT_TEST_{Guid.NewGuid():N}";
        try
        {
            Environment.SetEnvironmentVariable(name, "stale", EnvironmentVariableTarget.Process);
            TunnelAgent.Infrastructure.Services.UserEnvironmentService.Set(name, "value");
            Assert.Equal("value", TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get(name));

            TunnelAgent.Infrastructure.Services.UserEnvironmentService.Remove(name);

            Assert.Null(TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get(name));
        }
        finally
        {
            TunnelAgent.Infrastructure.Services.UserEnvironmentService.Remove(name);
        }
    }

    [Fact]
    public void DownloadService_NewInstance_StartsNotInstalledOrStopped()
    {
        var service = new DownloadService();

        Assert.Contains(service.State, new[] { EngineState.NotInstalled, EngineState.Stopped });
    }
}
