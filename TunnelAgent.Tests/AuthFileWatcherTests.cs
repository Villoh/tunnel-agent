using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class AuthFileWatcherTests
{
    [Fact]
    public void NotifyNow_RaisesChangedImmediately()
    {
        using var temp = new TestTempDirectory();
        using var watcher = new AuthFileWatcher(temp.Path);
        var raised = false;
        watcher.Changed += (_, _) => raised = true;

        watcher.NotifyNow();

        Assert.True(raised);
    }

    [Fact]
    public async Task FileChange_RaisesChangedAfterDebounce()
    {
        using var temp = new TestTempDirectory();
        using var watcher = new AuthFileWatcher(temp.Path);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, _) => changed.TrySetResult();

        await File.WriteAllTextAsync(temp.File("codex-user.json"), "{}");

        var completed = await Task.WhenAny(changed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.Same(changed.Task, completed);
    }
}
