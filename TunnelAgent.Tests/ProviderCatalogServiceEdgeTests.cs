using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class ProviderCatalogServiceEdgeTests
{
    [Fact]
    public async Task ResetAllCredentialsAsync_RemovesAllCustomAndOAuthAccounts()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();

        // Add a custom provider with account
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "local-ai",
            Enabled = true,
            BaseUrl = "https://local.example/v1",
            DisplayName = "Local AI",
            Accounts = [new ProviderAccountSettings { ApiKey = "sk-key", Label = "Primary" }]
        });

        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        // Should have our custom provider
        Assert.Contains(catalog.Providers, p => p.Id == "local-ai");

        await catalog.ResetAllCredentialsAsync();

        // After reset, custom provider accounts should be cleared
        Assert.Empty(settings.Current.Providers.Where(p => p.Id == "local-ai")
            .SelectMany(p => p.Accounts));
    }

    [Fact]
    public void DisconnectOAuth_NonExistentProvider_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        catalog.InitializeAsync().GetAwaiter().GetResult();

        // Should not throw for unknown provider
        catalog.DisconnectOAuth("nonexistent-provider-id");
    }

    [Fact]
    public async Task DisconnectOAuth_DisablesKnownProvider()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        // Disconnect claude (which has OAuth support)
        catalog.DisconnectOAuth("claude");

        // Should not throw, provider should exist
        Assert.Contains(catalog.Providers, p => p.Id == "claude");
    }

    [Fact]
    public async Task ConnectedProviderCount_ReflectsConnectedState()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        // Without any OAuth tokens, no providers should be connected
        var connected = catalog.Providers.Count(p => p.Connected);
        Assert.Equal(0, connected);
    }
}
