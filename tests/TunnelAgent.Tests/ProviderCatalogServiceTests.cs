using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class ProviderCatalogServiceTests
{
    [Fact]
    public async Task InitializeAsync_AddsBuiltInOAuthProviders()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.InitializeAsync();

        Assert.Contains(catalog.Providers, p => p.Id == "claude" && p.IsOAuth);
        Assert.Contains(catalog.Providers, p => p.Id == "codex" && p.IsOAuth);
        Assert.All(catalog.Providers.Where(p => p.IsOAuth), p => Assert.False(p.Connected));
    }

    [Fact]
    public async Task InitializeAsync_CustomProviderInSettings_AddsAccountsFromCredentialStore()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "local-ai",
            Enabled = true,
            BaseUrl = "https://local.example/v1",
            DisplayName = "Local AI"
        });
        new CustomProviderCredentialStore(authDir).Save("local-ai", "test-key", "Primary");
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.InitializeAsync();

        var provider = Assert.Single(catalog.Providers, p => p.Id == "local-ai");
        Assert.False(provider.IsOAuth);
        Assert.Equal("Local AI", provider.Name);
        var account = Assert.Single(provider.Accounts);
        Assert.Equal("Primary", account.Label);
    }

    [Fact]
    public async Task SetProviderEnabledAsync_NewProvider_PersistsSettingAndWritesConfig()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.SetProviderEnabledAsync("codex", false);

        var provider = Assert.Single(settings.Current.Providers, p => p.Id == "codex");
        Assert.False(provider.Enabled);
        Assert.True(File.Exists(config.ConfigPath));
    }
}
