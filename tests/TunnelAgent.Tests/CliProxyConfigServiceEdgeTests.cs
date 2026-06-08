using TunnelAgent.Services;
using Xunit;

using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class ConfigServiceEdgeTests
{
    [Fact]
    public async Task WriteConfigAsync_WithRoutingStrategy_WritesStrategyToYaml()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Port = 9999;
        settings.Current.RoutingStrategy = ViewModels.RoutingStrategy.FillFirst;
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("strategy: \"fill-first\"", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_RoundRobinStrategy_WritesToYaml()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Port = 9999;
        settings.Current.RoutingStrategy = ViewModels.RoutingStrategy.RoundRobin;
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("strategy: \"round-robin\"", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_DisablesDebugLoggingByDefault()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        settings.Current.Port = 9999;
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        await config.WriteConfigAsync();

        var yaml = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("debug: false", yaml);
        Assert.Contains("port: 9999", yaml);
    }

    [Fact]
    public async Task ConfigPath_IsSetByConstructor()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var configPath = temp.File("my-config.yaml");
        var config = new ConfigService(settings, configPath, temp.File("auth"));

        Assert.Equal(configPath, config.ConfigPath);
    }

    [Fact]
    public async Task WriteConfigAsync_MultipleCalls_OverwritesPrevious()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));

        settings.Current.Port = 1111;
        await config.WriteConfigAsync();
        var yaml1 = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("port: 1111", yaml1);

        settings.Current.Port = 2222;
        await config.WriteConfigAsync();
        var yaml2 = await File.ReadAllTextAsync(config.ConfigPath);
        Assert.Contains("port: 2222", yaml2);
        Assert.DoesNotContain("port: 1111", yaml2);
    }

    [Fact]
    public async Task ReadProviderSettingsFromConfig_ReadsDisabledOAuthAndCustomProvider()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        await File.WriteAllTextAsync(config.ConfigPath, """
host: "127.0.0.1"
port: 8317
auth-dir: "auth"
oauth-excluded-models:
  codex:
    - "*"
openai-compatibility:
  - name: local-ai
    display-name: "Local AI"
    base-url: "http://localhost:11434/v1"
    api-key-entries:
      - api-key: "key"
""");

        var providers = await config.ReadProviderSettingsFromConfigAsync();

        var codex = Assert.Single(providers, p => p.Id == "codex");
        Assert.False(codex.Enabled);
        var local = Assert.Single(providers, p => p.Id == "local-ai");
        Assert.True(local.Enabled);
        Assert.Equal("Local AI", local.DisplayName);
        Assert.Equal("http://localhost:11434/v1", local.BaseUrl);
    }

}
