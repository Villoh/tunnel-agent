using System.Text.Json;
using TunnelAgent.Core.Engine;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_WhenFileMissing_CreatesDefaultsFile()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        var service = new SettingsService(path);

        await service.LoadAsync();

        Assert.Equal(8317, CliProxyPort(service.Current));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task LoadAsync_WhenFileEmpty_WritesDefaultsFile()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        await File.WriteAllTextAsync(path, "");
        var service = new SettingsService(path);

        await service.LoadAsync();

        Assert.Equal(8317, CliProxyPort(service.Current));
        var json = await File.ReadAllTextAsync(path);
        Assert.Contains("\"Port\": 8317", json);
        Assert.Contains("\"ThemeMode\": \"system\"", json);
    }

    [Fact]
    public async Task LoadAsync_WhenFileExists_LoadsPersistedSettings()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new AppSettings
        {
            Port = 9001,
            LaunchAtLogin = false
        }));
        var service = new SettingsService(path);

        await service.LoadAsync();

        Assert.Equal(9001, CliProxyPort(service.Current));
        Assert.False(service.Current.LaunchAtLogin);
    }

    [Fact]
    public async Task LoadAsync_WhenJsonInvalid_FallsBackToDefaults()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        await File.WriteAllTextAsync(path, "not json");
        var service = new SettingsService(path);

        await service.LoadAsync();

        Assert.Equal(8317, CliProxyPort(service.Current));
    }

    [Fact]
    public async Task SaveImmediateAsync_WritesCurrentSettings()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        var service = new SettingsService(path);
        await service.LoadAsync();
        service.Current.GetOrAddEngine(EngineCatalog.CliProxyApi.Id, EngineCatalog.CliProxyApi.DefaultPort).Port = 7777;

        await service.SaveImmediateAsync();

        var reloaded = new SettingsService(path);
        await reloaded.LoadAsync();
        Assert.Equal(7777, CliProxyPort(reloaded.Current));
    }

    private static int CliProxyPort(AppSettings settings) =>
        settings.GetOrAddEngine(EngineCatalog.CliProxyApi.Id, EngineCatalog.CliProxyApi.DefaultPort).Port;
}
