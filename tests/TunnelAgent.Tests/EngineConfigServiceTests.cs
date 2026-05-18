using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class EngineConfigServiceTests
{
    [Fact]
    public async Task WriteConfigAsync_WritesBaseProxySettings()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Port = 9999;
        var authDir = temp.File("auth");
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir);

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("host: \"127.0.0.1\"", yaml);
        Assert.Contains("port: 9999", yaml);
        Assert.Contains($"auth-dir: \"{authDir.Replace('\\', '/')}\"", yaml);
        Assert.Contains("debug: false", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_DisabledOAuthProvider_AddsExcludedModels()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Providers.Add(new ProviderSettings { Id = "codex", Enabled = false });
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

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
        var fileKey = string.Concat("file", "-key");
        settings.Current.Providers.Add(new ProviderSettings
        {
            Id = "local-ai",
            Enabled = true,
            BaseUrl = "https://local.example/v1",
            DisplayName = "Local AI",
            Accounts =
            [
                new ProviderAccountSettings { ApiKey = inlineKey },
                new ProviderAccountSettings { ApiKey = disabledKey, Disabled = true }
            ]
        });
        var store = new CustomProviderCredentialStore(authDir);
        store.Save("local-ai", fileKey, "File");
        store.Save("local-ai", inlineKey, "Duplicate");
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), authDir, store);

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("openai-compatibility:", yaml);
        Assert.Contains("  - name: local-ai", yaml);
        Assert.Contains("    display-name: \"Local AI\"", yaml);
        Assert.Contains("    base-url: \"https://local.example/v1\"", yaml);
        Assert.Contains($"      - api-key: \"{inlineKey}\"", yaml);
        Assert.Contains($"      - api-key: \"{fileKey}\"", yaml);
        Assert.DoesNotContain(disabledKey, yaml);
        Assert.Equal(1, CountOccurrences(yaml, inlineKey));
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
