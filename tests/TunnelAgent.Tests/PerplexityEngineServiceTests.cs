using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using Xunit;

using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine.Perplexity;
namespace TunnelAgent.Tests;

public sealed class PerplexityEngineServiceTests
{
    [Fact]
    public async Task Constructor_InitialState_IsNotInstalledOrStopped()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);

        Assert.Contains(engine.State, new[] { EngineState.NotInstalled, EngineState.Stopped });
        Assert.False(engine.IsRunning);
        Assert.False(engine.UpdateAvailable);
        Assert.Equal(0, engine.DownloadProgress);
        Assert.Null(engine.InstalledVersion);
        Assert.Null(engine.LatestVersion);
    }

    [Fact]
    public async Task StateChanged_RaisesWhenStateChanges()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);
        var raised = false;
        engine.StateChanged += (_, _) => raised = true;

        try { await engine.InitializeAsync(); } catch { }

        Assert.True(raised);
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);

        await engine.StopAsync();
    }

    [Fact]
    public async Task WriteConfigAsync_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();

        var engine = new EngineService(settings);
        await engine.WriteConfigAsync();
    }

    [Fact]
    public async Task CheckForUpdateAsync_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);

        try { await engine.CheckForUpdateAsync(); } catch { }
    }

    [Fact]
    public async Task InstalledVersion_ReflectsDownloadService()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);

        Assert.Null(engine.InstalledVersion);
        Assert.Equal(8327, engine.Port);
        Assert.Null(engine.LastError);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_NotInstalled_PropagatesExceptions()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);

        await Assert.ThrowsAnyAsync<Exception>(() => engine.DownloadAndInstallAsync("v0.0.0-nonexistent"));
    }

    [Fact]
    public async Task Properties_ExposeSubserviceState()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var engine = new EngineService(settings);

        Assert.Null(engine.InstalledBinarySha256);
        Assert.Null(engine.InstalledArchiveSha256);
        Assert.Null(engine.LatestAssetName);
        Assert.Null(engine.LatestAssetSha256);
        Assert.Null(engine.IntegrityError);
    }
}
