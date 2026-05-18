using System.Text.Json;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class SettingsServiceEdgeTests
{
    [Fact]
    public async Task SaveImmediateAsync_WithProviders_PersistsAllData()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        var service = new SettingsService(path);
        await service.LoadAsync();

        service.Current.Providers.Add(new ProviderSettings
        {
            Id = "custom-provider",
            Enabled = true,
            BaseUrl = "https://api.example.com",
            DisplayName = "Custom",
            Accounts = [new ProviderAccountSettings { ApiKey = "key-1", Label = "Main" }]
        });
        service.Current.LogLevel = "warn";

        await service.SaveImmediateAsync();

        var reloaded = new SettingsService(path);
        await reloaded.LoadAsync();
        Assert.Equal("warn", reloaded.Current.LogLevel);
        Assert.Single(reloaded.Current.Providers);
        Assert.Equal("custom-provider", reloaded.Current.Providers[0].Id);
        Assert.Equal("key-1", reloaded.Current.Providers[0].Accounts[0].ApiKey);
    }

    [Fact]
    public async Task LoadAsync_FieldMissing_UsesDefault()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        // Only set Port, omit all other fields
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new { Port = 5555 }));

        var service = new SettingsService(path);
        await service.LoadAsync();

        Assert.Equal(5555, service.Current.Port);
        Assert.True(service.Current.LaunchAtLogin); // default
        Assert.Equal("info", service.Current.LogLevel); // default
    }

    [Fact]
    public async Task Save_DoesNotThrow_WhenCalledMultipleTimes()
    {
        using var temp = new TestTempDirectory();
        var service = new SettingsService(temp.File("settings.json"));
        await service.LoadAsync();

        service.Current.Port = 1111;
        service.Save();
        service.Current.Port = 2222;
        service.Save();
        service.Current.Port = 3333;
        service.Save();
    }

    [Fact]
    public void Constructor_DoesNotCreateFileImmediately()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        var service = new SettingsService(path);

        Assert.False(File.Exists(path));
    }
}
