using TunnelAgent.Services;
using TunnelAgent.ViewModels;
using Xunit;

using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class CliProxyProcessServiceTests
{
    [Fact]
    public void Constructor_InitialState_IsStopped()
    {
        var service = new ProcessService();

        Assert.False(service.IsRunning);
        Assert.Equal(EngineState.Stopped, service.State);
        Assert.Equal(0, service.Port);
        Assert.Null(service.LastError);
    }

    [Fact]
    public async Task StopAsync_SetsStoppedAndRaisesStateChanged()
    {
        var service = new ProcessService();
        var raised = false;
        service.StateChanged += (_, _) => raised = true;

        await service.StopAsync();

        Assert.Equal(EngineState.Stopped, service.State);
        Assert.False(service.IsRunning);
        Assert.True(raised);
    }

    [Fact]
    public async Task StopAsync_MultipleCalls_DoesNotThrow()
    {
        var service = new ProcessService();

        await service.StopAsync();
        await service.StopAsync();
        await service.StopAsync();
    }

    [Fact]
    public async Task StartAsync_NonexistentBinary_ThrowsWin32Exception()
    {
        using var temp = new TestTempDirectory();
        var service = new ProcessService();
        var stateChanges = new List<EngineState>();
        service.StateChanged += (_, _) => stateChanges.Add(service.State);

        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            service.StartAsync(
                temp.File("nonexistent-binary.exe"),
                temp.File("config.yaml"),
                19999));

        // State should be Error after Starting was set
        Assert.Contains(EngineState.Starting, stateChanges);
    }

    [Fact]
    public async Task StartAsync_NonexistentBinary_ExceptionPropagates()
    {
        using var temp = new TestTempDirectory();
        var service = new ProcessService();

        var ex = await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() =>
            service.StartAsync(
                temp.File("nonexistent-binary.exe"),
                temp.File("config.yaml"),
                19998));

        Assert.Contains("cannot find", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(19998, service.Port);
    }

    [Fact]
    public void StateChanged_NotRaised_WhenNoSubscribers()
    {
        var service = new ProcessService();
        // Should not throw
        service.StateChanged += null;
        service.StateChanged -= null;
    }
}
