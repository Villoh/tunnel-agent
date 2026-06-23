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

        await catalog.AddAccountAsync("gemini-cli", "", "AIza-test", null, ProviderKind.GeminiApiKey);
        await Task.Delay(300);

        var gemini = Assert.Single(catalog.Providers, p => p.Id == "gemini-cli");
        var account = Assert.Single(gemini.Accounts);
        Assert.Equal("AIza-test", account.ApiKey);
    }

    [Fact]
    public async Task InitializeAsync_CustomProviderInYaml_LoadsProvider()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        await File.WriteAllTextAsync(config.ConfigPath, """
host: "127.0.0.1"
port: 8317
auth-dir: "auth"
openai-compatibility:
  - name: local-ai
    display-name: "Local AI"
    base-url: "https://local.example/v1"
    api-key-entries:
      - api-key: "test-key"
""");
        using var catalog = new ProviderCatalogService(settings, config, authDir);

        await catalog.InitializeAsync();

        var provider = Assert.Single(catalog.Providers, p => p.Id == "local-ai");
        Assert.False(provider.IsOAuth);
        Assert.Equal("Local AI", provider.Name);
        var account = Assert.Single(provider.Accounts);
        Assert.Equal("test-key", account.ApiKey);
    }

    [Fact]
    public async Task UpdateCustomProviderModelsAsync_PersistsModelsAndRefreshesProvider()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        await catalog.AddCustomProviderAsync("OpenRouter", "https://openrouter.ai/api/v1", "sk-or", ["a", "b"]);
        var providerId = Assert.Single(catalog.Providers, p => p.Id == "openrouter").Id;

        await catalog.UpdateCustomProviderModelsAsync(providerId, ["b", "c", "d"]);

        var provider = Assert.Single(catalog.Providers, p => p.Id == providerId);
        Assert.Equal(new[] { "b", "c", "d" }, provider.Models);

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("      - name: \"c\"", yaml);
        Assert.DoesNotContain("      - name: \"a\"", yaml);
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
