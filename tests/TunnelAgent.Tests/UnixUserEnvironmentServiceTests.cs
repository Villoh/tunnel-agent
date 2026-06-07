using System.IO;
using TunnelAgent.Infrastructure.Services;
using Xunit;

namespace TunnelAgent.Tests;

/// <summary>
/// Tests for UnixUserEnvironmentService.
/// All file I/O uses isolated temp directories — no real ~/.profile or
/// XDG paths are touched. Platform-guarded methods are exercised via
/// their *Core() counterparts which carry no OS attribute.
/// </summary>
public sealed class UnixUserEnvironmentServiceTests : IDisposable
{
    private readonly TestTempDirectory _tmp = new();
    private string EnvFile   => _tmp.File("config/tunnelagent/environment");
    private string ProfileFile => _tmp.File(".profile");

    private UnixUserEnvironmentService Build() =>
        new(appEnvFile: EnvFile, profile: ProfileFile);

    public void Dispose() => _tmp.Dispose();

    // ── App-owned store ───────────────────────────────────────────────────────

    [Fact]
    public void Get_WhenStoreEmpty_ReturnsNull()
    {
        var svc = Build();
        Assert.Null(svc.Get("MISSING_VAR"));
    }

    [Fact]
    public void Set_WritesExportLineToStore()
    {
        var svc = Build();
        svc.Set("MY_KEY", "hello");

        var content = File.ReadAllText(EnvFile);
        Assert.Contains("export MY_KEY=hello", content);
    }

    [Fact]
    public void Set_CreatesDirectoryIfMissing()
    {
        var svc = Build();
        svc.Set("MY_KEY", "v");

        Assert.True(File.Exists(EnvFile));
    }

    [Fact]
    public void Get_AfterSet_ReturnsValue()
    {
        var svc = Build();
        svc.Set("MY_KEY", "world");

        Assert.Equal("world", svc.Get("MY_KEY"));
    }

    [Fact]
    public void Set_MultipleKeys_AllPresent()
    {
        var svc = Build();
        svc.Set("KEY_A", "aaa");
        svc.Set("KEY_B", "bbb");

        Assert.Equal("aaa", svc.Get("KEY_A"));
        Assert.Equal("bbb", svc.Get("KEY_B"));
    }

    [Fact]
    public void Set_OverwritesExistingKey()
    {
        var svc = Build();
        svc.Set("MY_KEY", "first");
        svc.Set("MY_KEY", "second");

        Assert.Equal("second", svc.Get("MY_KEY"));
        var lines = File.ReadAllLines(EnvFile);
        Assert.Single(lines, l => l.StartsWith("export MY_KEY="));
    }

    [Fact]
    public void Remove_DeletesKeyFromStore()
    {
        var svc = Build();
        svc.Set("MY_KEY", "val");
        svc.Remove("MY_KEY");

        Assert.Null(svc.Get("MY_KEY"));
        var content = File.ReadAllText(EnvFile);
        Assert.DoesNotContain("MY_KEY", content);
    }

    [Fact]
    public void Remove_NonExistentKey_DoesNotThrow()
    {
        var svc = Build();
        var ex = Record.Exception(() => svc.Remove("GHOST"));
        Assert.Null(ex);
    }

    [Fact]
    public void SeedProcessEnvironment_SeedsFromStore()
    {
        var svc = Build();
        svc.Set("SEED_TEST_VAR", "seeded");

        // Create a fresh instance that hasn't set anything in-process,
        // then seed and verify the process env was updated.
        var svc2 = new UnixUserEnvironmentService(EnvFile, ProfileFile);
        svc2.SeedProcessEnvironment();

        Assert.Equal("seeded", Environment.GetEnvironmentVariable("SEED_TEST_VAR"));
        Environment.SetEnvironmentVariable("SEED_TEST_VAR", null); // cleanup
    }

    [Fact]
    public void StoreFormat_HasCommentHeader()
    {
        var svc = Build();
        svc.Set("X", "1");

        var first = File.ReadLines(EnvFile).First();
        Assert.StartsWith("#", first);
    }

    // ── ~/.profile hook ───────────────────────────────────────────────────────

    [Fact]
    public void EnsureProfileHook_WhenProfileMissing_CreatesFileWithBlock()
    {
        var svc = Build();
        svc.EnsureProfileHookCore();

        Assert.True(File.Exists(ProfileFile));
        var content = File.ReadAllText(ProfileFile);
        Assert.Contains("# BEGIN TunnelAgent", content);
        Assert.Contains("# END TunnelAgent", content);
        Assert.Contains(EnvFile, content);
    }

    [Fact]
    public void EnsureProfileHook_WhenProfileExists_AppendsBlock()
    {
        File.WriteAllText(ProfileFile, "# existing content\n");
        var svc = Build();
        svc.EnsureProfileHookCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.StartsWith("# existing content", content);
        Assert.Contains("# BEGIN TunnelAgent", content);
    }

    [Fact]
    public void EnsureProfileHook_IsIdempotent()
    {
        var svc = Build();
        svc.EnsureProfileHookCore();
        svc.EnsureProfileHookCore();
        svc.EnsureProfileHookCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.Equal(1, CountOccurrences(content, "# BEGIN TunnelAgent"));
    }

    [Fact]
    public void EnsureProfileHook_WhenProfileHasNoTrailingNewline_InsertsNewlineFirst()
    {
        File.WriteAllText(ProfileFile, "# no newline at end");
        var svc = Build();
        svc.EnsureProfileHookCore();

        var content = File.ReadAllText(ProfileFile);
        // The block must not be glued onto the last line
        Assert.DoesNotContain("end# BEGIN", content);
        Assert.Contains("\n# BEGIN TunnelAgent", content);
    }

    [Fact]
    public void EnsureProfileHook_SourceLineReferencesAppEnvFile()
    {
        var svc = Build();
        svc.EnsureProfileHookCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.Contains($"[ -f \"{EnvFile}\" ] && . \"{EnvFile}\"", content);
    }

    [Fact]
    public void CleanProfileHook_WhenStoreEmpty_RemovesBlock()
    {
        var svc = Build();
        svc.EnsureProfileHookCore();  // add hook
        // store is empty — hook should be removed
        svc.CleanProfileHookIfEmptyCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.DoesNotContain("# BEGIN TunnelAgent", content);
        Assert.DoesNotContain("# END TunnelAgent", content);
    }

    [Fact]
    public void CleanProfileHook_WhenStoreHasVars_LeavesBlockIntact()
    {
        var svc = Build();
        svc.Set("K", "v");
        svc.EnsureProfileHookCore();
        svc.CleanProfileHookIfEmptyCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.Contains("# BEGIN TunnelAgent", content);
    }

    [Fact]
    public void CleanProfileHook_PreservesContentOutsideBlock()
    {
        File.WriteAllText(ProfileFile, "# user content\nexport PATH=$PATH:/usr/local/bin\n");
        var svc = Build();
        svc.EnsureProfileHookCore();
        svc.CleanProfileHookIfEmptyCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.Contains("# user content", content);
        Assert.Contains("export PATH=$PATH:/usr/local/bin", content);
    }

    [Fact]
    public void CleanProfileHook_WhenProfileMissing_DoesNotThrow()
    {
        var svc = Build();
        var ex = Record.Exception(() => svc.CleanProfileHookIfEmptyCore());
        Assert.Null(ex);
    }

    [Fact]
    public void CleanProfileHook_TrimsTrailingBlankLinesLeftByBlock()
    {
        File.WriteAllText(ProfileFile, "# user content\n");
        var svc = Build();
        svc.EnsureProfileHookCore();
        svc.CleanProfileHookIfEmptyCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.DoesNotContain("\n\n\n", content);
        Assert.EndsWith("# user content", content.TrimEnd('\n', '\r'));
    }

    [Fact]
    public void FullFlow_SetThenRemoveAll_CleansProfileAndStore()
    {
        var svc = Build();

        svc.Set("KEY_A", "1");
        svc.EnsureProfileHookCore();
        svc.Set("KEY_B", "2");

        svc.Remove("KEY_A");
        svc.Remove("KEY_B");
        svc.CleanProfileHookIfEmptyCore();

        // Store should be empty (only header comment)
        var storeLines = File.ReadAllLines(EnvFile)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToList();
        Assert.Empty(storeLines);

        // Profile hook should be gone
        if (File.Exists(ProfileFile))
            Assert.DoesNotContain("# BEGIN TunnelAgent", File.ReadAllText(ProfileFile));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(pattern, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }
}
