using System.Text.Json;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class SettingsServiceEdgeTests
{
    [Fact]
    public async Task SaveImmediateAsync_DoesNotPersistRuntimeProviderState()
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
            Accounts = [new ProviderAccountSettings { ApiKey = "key-1" }]
        });
        await service.SaveImmediateAsync();

        var json = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("Providers", json);

        var reloaded = new SettingsService(path);
        await reloaded.LoadAsync();
        Assert.Empty(reloaded.Current.Providers);
    }

    [Fact]
    public async Task LoadAsync_WithLegacyRuntimeState_DoesNotStripBeforeMigration()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("settings.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Port = 5555,
            Providers = new[] { new { Id = "custom-provider", Enabled = true, BaseUrl = "https://api.example.com" } },
            PerplexityAccounts = new[] { new { Id = "acct", Label = "Primary", SessionToken = "token" } }
        }));

        var service = new SettingsService(path);
        await service.LoadAsync();

        Assert.Single(service.Current.Providers);
        Assert.Single(service.Current.PerplexityAccounts);
        var persisted = await File.ReadAllTextAsync(path);
        Assert.Contains("Providers", persisted);
        Assert.Contains("PerplexityAccounts", persisted);
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

        Assert.Equal(5555, service.Current.GetOrAddEngine(Core.Engine.EngineCatalog.CliProxyApi.Id, Core.Engine.EngineCatalog.CliProxyApi.DefaultPort).Port);
        Assert.True(service.Current.LaunchAtLogin); // default
        Assert.Equal("system", service.Current.ThemeMode); // default
        var persisted = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(persisted);
        Assert.False(doc.RootElement.TryGetProperty("Port", out _));
        Assert.Contains("\"LaunchAtLogin\": true", persisted);
        Assert.Contains("\"ThemeMode\": \"system\"", persisted);
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
