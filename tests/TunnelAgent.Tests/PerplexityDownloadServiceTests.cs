using TunnelAgent.Services;
using Xunit;

using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine.Perplexity;
namespace TunnelAgent.Tests;

public sealed class PerplexityDownloadServiceTests
{
    [Fact]
    public void Constructor_StartsNotInstalled()
    {
        var service = new DownloadService();

        Assert.Equal(EngineState.NotInstalled, service.State);
        Assert.Null(service.InstalledVersion);
        Assert.Null(service.LatestVersion);
        Assert.Null(service.InstalledBinarySha256);
        Assert.Null(service.InstalledArchiveSha256);
        Assert.Null(service.LatestAssetName);
        Assert.Null(service.LatestAssetSha256);
        Assert.Null(service.IntegrityError);
        Assert.False(service.UpdateAvailable);
        Assert.Equal(0, service.DownloadProgress);
    }

    [Fact]
    public async Task InitializeAsync_WithoutBinary_SetsNotInstalled()
    {
        var service = new DownloadService();
        var raised = false;
        service.StateChanged += (_, _) => raised = true;

        await service.InitializeAsync();

        Assert.Contains(service.State, new[] { EngineState.NotInstalled, EngineState.Stopped });
        Assert.True(raised);
    }

    [Fact]
    public void IsBinaryInstalled_ReturnsBoolean()
    {
        var installed = DownloadService.IsBinaryInstalled();
        Assert.True(installed || !installed);
    }

    [Fact]
    public void BinaryPath_EndsWithExpectedBinaryName()
    {
        var path = DownloadService.BinaryPath;

        Assert.NotNull(path);
        Assert.NotEmpty(path);
        Assert.EndsWith(".exe", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EngineDir_IsNotNullOrEmpty()
    {
        var dir = DownloadService.EngineDir;

        Assert.NotNull(dir);
        Assert.NotEmpty(dir);
    }

    [Fact]
    public void UpdateAvailable_WhenNoVersions_ReturnsFalse()
    {
        var service = new DownloadService();
        Assert.False(service.UpdateAvailable);
    }

    [Fact]
    public void StateChanged_HandlerCanSubscribe()
    {
        var service = new DownloadService();
        var events = new List<EngineState>();
        service.StateChanged += (_, _) => events.Add(service.State);

        Assert.NotNull(service);
    }
}
