using TunnelAgent.Services;

using TunnelAgent.Infrastructure.Engine.CliProxy;
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
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.InitializeAsync();

        Assert.Contains(catalog.Providers, p => p.Id == "claude" && p.IsOAuth);
        Assert.Contains(catalog.Providers, p => p.Id == "codex" && p.IsOAuth);
        Assert.DoesNotContain(catalog.Providers, p => p.Id == "qwen");
        Assert.All(catalog.Providers.Where(p => p.IsOAuth), p => Assert.False(p.Connected));
    }

    [Fact]
    public async Task OnConfigChanged_GeminiApiKeyProvider_RefreshesAccounts()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        Directory.CreateDirectory(authDir);
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        await catalog.AddAccountAsync("gemini-cli", "", "AIza-test", "Gemini", null, ProviderKind.GeminiApiKey);
        await Task.Delay(300);

        var gemini = Assert.Single(catalog.Providers, p => p.Id == "gemini-cli");
        var account = Assert.Single(gemini.Accounts);
        Assert.Equal("Gemini", account.Label);
        Assert.Equal("AIza-test", account.ApiKey);
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
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.InitializeAsync();

        var provider = Assert.Single(catalog.Providers, p => p.Id == "local-ai");
        Assert.False(provider.IsOAuth);
        Assert.Equal("Local AI", provider.Name);
        var account = Assert.Single(provider.Accounts);
        Assert.Equal("Primary", account.Label);
    }

    [Fact]
    public async Task AddAccountAsync_ExistingKey_WithBlankLabel_ClearsLabelInConfig()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "opencode",
            Enabled = true,
            Kind = ProviderKind.OpenAICompatibility,
            BaseUrl = "https://opencode.ai/zen/go/v1",
            Accounts = [new ProviderAccountSettings { ApiKey = "1234", Label = "Test" }]
        });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        var created = await catalog.AddAccountAsync("opencode", "https://opencode.ai/zen/go/v1", "1234", "", null, ProviderKind.OpenAICompatibility);

        Assert.False(created);
        Assert.Equal("", Assert.Single(settings.Current.Providers.Single(p => p.Id == "opencode").Accounts).Label);
        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("      - api-key: \"1234\"", yaml);
        Assert.DoesNotContain("label:", yaml);
    }

    [Fact]
    public async Task SetProviderEnabledAsync_NewProvider_PersistsSettingAndWritesConfig()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.SetProviderEnabledAsync("codex", false);

        var provider = Assert.Single(settings.Current.Providers, p => p.Id == "codex");
        Assert.False(provider.Enabled);
        Assert.True(File.Exists(config.ConfigPath));
    }
}
