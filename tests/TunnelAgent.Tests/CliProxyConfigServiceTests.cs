using TunnelAgent.Services;

using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class ConfigServiceTests
{
    [Fact]
    public async Task WriteConfigAsync_WritesBaseProxySettings()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.GetOrAddEngine(TunnelAgent.Core.Engine.EngineCatalog.CliProxyApi.Id, TunnelAgent.Core.Engine.EngineCatalog.CliProxyApi.DefaultPort).Port = 9999;
        var authDir = temp.File("auth");
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("host: \"127.0.0.1\"", yaml);
        Assert.Contains("port: 9999", yaml);
        Assert.Contains($"auth-dir: \"{authDir.Replace('\\', '/')}\"", yaml);
        Assert.Contains("\nauth-dir: ", yaml);
        Assert.DoesNotContain("\napi-keys:", yaml);
        Assert.DoesNotContain("\"api-keys:", yaml);
        Assert.Contains("debug: false", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_WritesConfiguredApiKeys()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        await config.WriteConfigAsync();
        await config.WriteApiKeysToConfigAsync(["sk-one", "sk-two", "sk-one"]);

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("api-keys:", yaml);
        Assert.Contains("  - \"sk-one\"", yaml);
        Assert.Contains("  - \"sk-two\"", yaml);
        Assert.Equal(1, CountOccurrences(yaml, "sk-one"));
    }

    [Fact]
    public async Task WriteConfigAsync_DisabledOAuthProvider_AddsExcludedModels()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings { Id = "codex", Enabled = false });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("oauth-excluded-models:", yaml);
        Assert.Contains("  codex:", yaml);
        Assert.Contains("    - \"*\"", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_CustomProvider_CombinesAndDeduplicatesActiveKeys()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var inlineKey = string.Concat("inline", "-key");
        var disabledKey = string.Concat("disabled", "-key");
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "local-ai",
            Enabled = true,
            BaseUrl = "https://local.example/v1",
            DisplayName = "Local AI",
            Accounts =
            [
                new ProviderAccountSettings { ApiKey = inlineKey, Label = "Primary" },
                new ProviderAccountSettings { ApiKey = inlineKey, Label = "Duplicate" },
                new ProviderAccountSettings { ApiKey = disabledKey, Label = "Backup", Disabled = true }
            ]
        });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("openai-compatibility:", yaml);
        Assert.Contains("  - name: local-ai", yaml);
        Assert.DoesNotContain("display-name:", yaml);
        Assert.Contains("    disabled: false", yaml);
        Assert.Contains("    base-url: \"https://local.example/v1\"", yaml);
        Assert.Contains($"      - api-key: \"{inlineKey}\"", yaml);
        Assert.Contains("        label: \"Primary\"", yaml);
        Assert.Contains($"      - api-key: \"{disabledKey}\"", yaml);
        Assert.Contains("        label: \"Backup\"", yaml);
        Assert.Equal(1, CountOccurrences(yaml, inlineKey));
    }

    [Fact]
    public async Task WriteConfigAsync_DisabledCustomProvider_IsKeptInConfig()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "openrouter",
            Enabled = false,
            Kind = ProviderKind.OpenAICompatibility,
            BaseUrl = "https://openrouter.ai/api/v1",
            Accounts = [new ProviderAccountSettings { ApiKey = "sk-or-test", Label = "OpenRouter" }]
        });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("openai-compatibility:", yaml);
        Assert.Contains("  - name: openrouter", yaml);
        Assert.Contains("    disabled: true", yaml);
        Assert.Contains("      - api-key: \"sk-or-test\"", yaml);
        Assert.Contains("        label: \"OpenRouter\"", yaml);
    }

    [Fact]
    public async Task WriteAndRead_CustomProviderModels_RoundTrip()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "openrouter",
            Enabled = true,
            Kind = ProviderKind.OpenAICompatibility,
            BaseUrl = "https://openrouter.ai/api/v1",
            Accounts = [new ProviderAccountSettings { ApiKey = "sk-or-test" }],
            Models = ["openai/gpt-5", "anthropic/claude-opus-4.6"]
        });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("    models:", yaml);
        Assert.Contains("      - name: \"openai/gpt-5\"", yaml);
        Assert.Contains("        alias: \"openai/gpt-5\"", yaml);
        Assert.Contains("      - name: \"anthropic/claude-opus-4.6\"", yaml);

        var parsed = await config.ReadProviderSettingsFromConfigAsync();
        var provider = Assert.Single(parsed, p => p.Id == "openrouter");
        Assert.Equal(new[] { "openai/gpt-5", "anthropic/claude-opus-4.6" }, provider.Models);
        Assert.Single(provider.Accounts, a => a.ApiKey == "sk-or-test");
    }

    [Fact]
    public async Task WriteConfigAsync_CustomProvider_WithNoKeys_KeepsProviderWithoutKeyEntries()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "opencode",
            Enabled = true,
            Kind = ProviderKind.OpenAICompatibility,
            BaseUrl = "https://opencode.ai/zen/go/v1"
        });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("openai-compatibility:", yaml);
        Assert.Contains("  - name: opencode", yaml);
        Assert.Contains("    base-url: \"https://opencode.ai/zen/go/v1\"", yaml);
        Assert.DoesNotContain("api-key-entries:", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_NativeApiKeyProviders_WriteClaudeGeminiAndCodexBlocks()
    {
        using var temp = new TestTempDirectory();
        var authDir = temp.File("auth");
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "claude",
            Enabled = true,
            Kind = ProviderKind.ClaudeApiKey,
            Accounts = [new ProviderAccountSettings { ApiKey = "sk-ant-test" }]
        });
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "gemini-cli",
            Enabled = true,
            Kind = ProviderKind.GeminiApiKey,
            BaseUrl = "https://generativelanguage.googleapis.com",
            Accounts = [new ProviderAccountSettings { ApiKey = "AIza-test" }]
        });
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "codex",
            Enabled = true,
            Kind = ProviderKind.CodexApiKey,
            Accounts = [new ProviderAccountSettings { ApiKey = "sk-codex-test" }]
        });
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), authDir);

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("claude-api-key:", yaml);
        Assert.Contains("  - api-key: \"sk-ant-test\"", yaml);
        Assert.Contains("gemini-api-key:", yaml);
        Assert.Contains("  - api-key: \"AIza-test\"", yaml);
        Assert.Contains("    base-url: \"https://generativelanguage.googleapis.com\"", yaml);
        Assert.Contains("codex-api-key:", yaml);
        Assert.Contains("  - api-key: \"sk-codex-test\"", yaml);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
