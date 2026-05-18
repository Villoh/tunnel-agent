using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class AuthFileWatcherEdgeTests
{
    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var watcher = new AuthFileWatcher(temp.Path);

        watcher.Dispose();
        watcher.Dispose(); // second dispose should not throw
    }

    [Fact]
    public void NotifyNow_AfterDispose_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var watcher = new AuthFileWatcher(temp.Path);
        watcher.Dispose();

        watcher.NotifyNow(); // should not throw after dispose
    }

    [Fact]
    public void NotifyNow_BeforeAnySubscribers_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        using var watcher = new AuthFileWatcher(temp.Path);

        watcher.NotifyNow(); // should not throw with no subscribers
    }
}
