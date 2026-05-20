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
    public void ConfigPath_IsSetByConstructor()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
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
}
