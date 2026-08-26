using TunnelAgent.Services;

using TunnelAgent.Infrastructure.Engine.CliProxy;
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
            Accounts = [new ProviderAccountSettings { ApiKey = "sk-key" }]
        });

        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        // Should have our custom provider
        Assert.Contains(catalog.Providers, p => p.Id == "local-ai");

        Directory.CreateDirectory(authDir);
        var unmanagedFile = Path.Combine(authDir, "unmanaged.json");
        var oauthFile = Path.Combine(authDir, "claude-user@example.com-pro.json");
        var customFile = Path.Combine(authDir, "openai-compat-local-ai-test.json");
        await File.WriteAllTextAsync(unmanagedFile, "{}");
        await File.WriteAllTextAsync(oauthFile, "{\"access_token\":\"token\"}");
        await File.WriteAllTextAsync(customFile, "{\"type\":\"openai-compat\",\"provider\":\"local-ai\",\"api_key\":\"sk-key\"}");

        await catalog.ResetAllCredentialsAsync();

        // After reset, custom provider accounts should be cleared. Managed auth files are backed up and removed,
        // but unrelated JSON in the auth folder must be preserved.
        Assert.Empty(settings.Current.Providers.Where(p => p.Id == "local-ai")
            .SelectMany(p => p.Accounts));
        Assert.True(File.Exists(unmanagedFile));
        Assert.False(File.Exists(oauthFile));
        Assert.False(File.Exists(customFile));
        // Backups must live outside auth-dir so CLIProxyAPI's own credential scan never sees them.
        Assert.False(Directory.Exists(Path.Combine(authDir, ".tunnelagent-backup")));
        Assert.True(Directory.Exists(Path.Combine(IPlatformInfo.Current.LocalDataDirectory, "credential-backups")));
    }

    [Fact]
    public async Task RemoveAccountAsync_LastCustomProviderKey_RemovesEntriesAndKeepsProvider()
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
            Accounts = [new ProviderAccountSettings { ApiKey = "1234" }]
        });

        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        await catalog.RemoveAccountAsync("opencode", "1234");

        var provider = Assert.Single(settings.Current.Providers, p => p.Id == "opencode");
        Assert.Empty(provider.Accounts);

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("  - name: opencode", yaml);
        Assert.Contains("    base-url: \"https://opencode.ai/zen/go/v1\"", yaml);
        Assert.DoesNotContain("api-key-entries:", yaml);
        Assert.DoesNotContain("label:", yaml);
    }

    [Fact]
    public async Task DisconnectOAuth_NonExistentProvider_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

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
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        Directory.CreateDirectory(authDir);
        var claudeFile = Path.Combine(authDir, "claude-user@example.com-pro.json");
        var unrelatedFile = Path.Combine(authDir, "notes.json");
        await File.WriteAllTextAsync(claudeFile, "{\"access_token\":\"token\"}");
        await File.WriteAllTextAsync(unrelatedFile, "{}");

        catalog.DisconnectOAuth("claude");

        Assert.Contains(catalog.Providers, p => p.Id == "claude");
        Assert.False(File.Exists(claudeFile));
        Assert.True(File.Exists(unrelatedFile));
        Assert.False(Directory.Exists(Path.Combine(authDir, ".tunnelagent-backup")));
        Assert.True(Directory.Exists(Path.Combine(IPlatformInfo.Current.LocalDataDirectory, "credential-backups")));
    }

    [Fact]
    public async Task ConnectedProviderCount_ReflectsConnectedState()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);
        using var catalog = new ProviderCatalogService(settings, config, authDir);
        await catalog.InitializeAsync();

        // Without any OAuth tokens, no providers should be connected
        var connected = catalog.Providers.Count(p => p.Connected);
        Assert.Equal(0, connected);
    }
}
