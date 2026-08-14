using TunnelAgent.Services;

namespace TunnelAgent.Tests;

public sealed class CursorStateStoreTests
{
    [Fact]
    public void ResolveStateDbPath_WhenNoneExist_ReturnsNull()
    {
        using var temp = new TestTempDirectory();
        var missing = Path.Combine(temp.Path, "nope", "state.vscdb");

        Assert.Null(CursorStateStore.ResolveStateDbPath(new[] { missing }));
    }

    [Fact]
    public void ResolveStateDbPath_PicksNewestExistingFile()
    {
        using var temp = new TestTempDirectory();
        var stale = temp.File("stale.vscdb");
        var live  = temp.File("live.vscdb");
        File.WriteAllText(stale, "old");
        File.WriteAllText(live, "new");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-60));
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);

        var resolved = CursorStateStore.ResolveStateDbPath(new[] { stale, live });

        Assert.Equal(Path.GetFullPath(live), resolved);
    }

    [Fact]
    public void ResolveStateDbPath_SkipsMissingThenUsesExisting()
    {
        using var temp = new TestTempDirectory();
        var missing = Path.Combine(temp.Path, "missing.vscdb");
        var present = temp.File("present.vscdb");
        File.WriteAllText(present, "ok");

        var resolved = CursorStateStore.ResolveStateDbPath(new[] { missing, present });

        Assert.Equal(Path.GetFullPath(present), resolved);
    }

    [Fact]
    public void ResolveStateDbPath_DeduplicatesSamePath()
    {
        using var temp = new TestTempDirectory();
        var db = temp.File("state.vscdb");
        File.WriteAllText(db, "ok");

        var resolved = CursorStateStore.ResolveStateDbPath(new[] { db, db });

        Assert.Equal(Path.GetFullPath(db), resolved);
    }
}
