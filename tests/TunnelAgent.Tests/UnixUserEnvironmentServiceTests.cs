using System.IO;
using TunnelAgent.Infrastructure.Services;
using Xunit;

namespace TunnelAgent.Tests;

/// <summary>
/// Tests for UnixUserEnvironmentService.
/// All file I/O uses isolated temp directories - no real ~/.profile,
/// XDG paths, or LaunchAgent directories are touched.
/// Platform-guarded methods are exercised via their *Core() counterparts.
/// </summary>
public sealed class UnixUserEnvironmentServiceTests : IDisposable
{
    private readonly TestTempDirectory _tmp = new();

    private string EnvFile      => _tmp.File("config/tunnelagent/environment");
    private string ProfileFile  => _tmp.File(".profile");
    private string PlistFile    => _tmp.File("LaunchAgents/com.tunnelagent.environment.plist");

    private UnixUserEnvironmentService Build() =>
        new(appEnvFile: EnvFile, profile: ProfileFile, launchAgentPlist: PlistFile);

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

        Assert.Contains("export MY_KEY=hello", File.ReadAllText(EnvFile));
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
        Assert.Single(File.ReadAllLines(EnvFile), l => l.StartsWith("export MY_KEY="));
    }

    [Fact]
    public void Remove_DeletesKeyFromStore()
    {
        var svc = Build();
        svc.Set("MY_KEY", "val");
        svc.Remove("MY_KEY");

        Assert.Null(svc.Get("MY_KEY"));
        Assert.DoesNotContain("MY_KEY", File.ReadAllText(EnvFile));
    }

    [Fact]
    public void Remove_NonExistentKey_DoesNotThrow()
    {
        var ex = Record.Exception(() => Build().Remove("GHOST"));
        Assert.Null(ex);
    }

    [Fact]
    public void SeedProcessEnvironment_SeedsFromStore()
    {
        Build().Set("SEED_TEST_VAR", "seeded");

        new UnixUserEnvironmentService(EnvFile, ProfileFile, PlistFile)
            .SeedProcessEnvironment();

        Assert.Equal("seeded", Environment.GetEnvironmentVariable("SEED_TEST_VAR"));
        Environment.SetEnvironmentVariable("SEED_TEST_VAR", null);
    }

    [Fact]
    public void StoreFormat_HasCommentHeader()
    {
        Build().Set("X", "1");
        Assert.StartsWith("#", File.ReadLines(EnvFile).First());
    }

    // ── Linux: ~/.profile hook ────────────────────────────────────────────────

    [Fact]
    public void EnsureProfileHook_WhenProfileMissing_CreatesFileWithBlock()
    {
        Build().EnsureProfileHookCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.Contains("# BEGIN TunnelAgent", content);
        Assert.Contains("# END TunnelAgent", content);
        Assert.Contains(EnvFile, content);
    }

    [Fact]
    public void EnsureProfileHook_WhenProfileExists_AppendsBlock()
    {
        File.WriteAllText(ProfileFile, "# existing content\n");
        Build().EnsureProfileHookCore();

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

        Assert.Equal(1, CountOccurrences(File.ReadAllText(ProfileFile), "# BEGIN TunnelAgent"));
    }

    [Fact]
    public void EnsureProfileHook_WhenProfileHasNoTrailingNewline_InsertsNewlineFirst()
    {
        File.WriteAllText(ProfileFile, "# no newline at end");
        Build().EnsureProfileHookCore();

        var content = File.ReadAllText(ProfileFile);
        Assert.DoesNotContain("end# BEGIN", content);
        Assert.Contains("\n# BEGIN TunnelAgent", content);
    }

    [Fact]
    public void EnsureProfileHook_SourceLineReferencesAppEnvFile()
    {
        Build().EnsureProfileHookCore();
        Assert.Contains($"[ -f \"{EnvFile}\" ] && . \"{EnvFile}\"", File.ReadAllText(ProfileFile));
    }

    [Fact]
    public void CleanProfileHook_WhenStoreEmpty_RemovesBlock()
    {
        var svc = Build();
        svc.EnsureProfileHookCore();
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

        Assert.Contains("# BEGIN TunnelAgent", File.ReadAllText(ProfileFile));
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
        Assert.Null(Record.Exception(() => Build().CleanProfileHookIfEmptyCore()));
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
    public void FullFlow_Linux_SetThenRemoveAll_CleansProfileAndStore()
    {
        var svc = Build();
        svc.Set("KEY_A", "1");
        svc.EnsureProfileHookCore();
        svc.Set("KEY_B", "2");
        svc.Remove("KEY_A");
        svc.Remove("KEY_B");
        svc.CleanProfileHookIfEmptyCore();

        var storeVarLines = File.ReadAllLines(EnvFile)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToList();
        Assert.Empty(storeVarLines);

        if (File.Exists(ProfileFile))
            Assert.DoesNotContain("# BEGIN TunnelAgent", File.ReadAllText(ProfileFile));
    }

    // ── macOS: LaunchAgent plist ──────────────────────────────────────────────

    [Fact]
    public void WriteLaunchAgent_CreatesPlistWithAllVars()
    {
        var svc = Build();
        svc.Set("KEY_A", "val_a");
        svc.Set("KEY_B", "val_b");
        svc.WriteLaunchAgentCore();

        Assert.True(File.Exists(PlistFile));
        var content = File.ReadAllText(PlistFile);
        Assert.Contains("launchctl setenv", content);
        Assert.Contains("KEY_A", content);
        Assert.Contains("val_a", content);
        Assert.Contains("KEY_B", content);
        Assert.Contains("val_b", content);
    }

    [Fact]
    public void WriteLaunchAgent_WhenStoreEmpty_DeletesPlist()
    {
        var svc = Build();
        svc.Set("K", "v");
        svc.WriteLaunchAgentCore(); // creates it
        svc.Remove("K");
        svc.WriteLaunchAgentCore(); // should delete it

        Assert.False(File.Exists(PlistFile));
    }

    [Fact]
    public void WriteLaunchAgent_WhenPlistMissing_DoesNotThrow()
    {
        Assert.Null(Record.Exception(() => Build().WriteLaunchAgentCore()));
    }

    [Fact]
    public void WriteLaunchAgent_PlistIsValidXml()
    {
        var svc = Build();
        svc.Set("MY_KEY", "my_value");
        svc.WriteLaunchAgentCore();

        var content = File.ReadAllText(PlistFile);
        Assert.StartsWith("<?xml", content);
        Assert.Contains("<plist", content);
        Assert.Contains("</plist>", content);
        Assert.Contains("<key>RunAtLoad</key>", content);
        Assert.Contains("<true/>", content);
    }

    [Fact]
    public void WriteLaunchAgent_PlistContainsCorrectLabel()
    {
        var svc = Build();
        svc.Set("K", "v");
        svc.WriteLaunchAgentCore();

        Assert.Contains("com.tunnelagent.environment", File.ReadAllText(PlistFile));
    }

    [Fact]
    public void WriteLaunchAgent_UpdatesOnSubsequentSet()
    {
        var svc = Build();
        svc.Set("KEY_A", "v1");
        svc.WriteLaunchAgentCore();
        svc.Set("KEY_B", "v2");
        svc.WriteLaunchAgentCore();

        var content = File.ReadAllText(PlistFile);
        Assert.Contains("KEY_A", content);
        Assert.Contains("KEY_B", content);
    }

    [Fact]
    public void CleanLaunchAgent_WhenStoreEmpty_DeletesPlist()
    {
        var svc = Build();
        svc.Set("K", "v");
        svc.WriteLaunchAgentCore();
        svc.Remove("K");
        svc.CleanLaunchAgentIfEmptyCore();

        Assert.False(File.Exists(PlistFile));
    }

    [Fact]
    public void CleanLaunchAgent_WhenStoreHasVars_LeavesFileIntact()
    {
        var svc = Build();
        svc.Set("K", "v");
        svc.WriteLaunchAgentCore();
        svc.CleanLaunchAgentIfEmptyCore();

        Assert.True(File.Exists(PlistFile));
    }

    [Fact]
    public void CleanLaunchAgent_WhenPlistMissing_DoesNotThrow()
    {
        Assert.Null(Record.Exception(() => Build().CleanLaunchAgentIfEmptyCore()));
    }

    [Fact]
    public void BuildLaunchAgentPlist_EscapesXmlSpecialChars()
    {
        var vars = new Dictionary<string, string> { ["KEY"] = "val<>&\"" };
        var plist = UnixUserEnvironmentService.BuildLaunchAgentPlist(vars);

        Assert.DoesNotContain("val<>", plist);
        Assert.Contains("val&lt;&gt;&amp;", plist);
    }

    [Fact]
    public void BuildLaunchAgentPlist_EscapesSingleQuotesInShell()
    {
        var vars = new Dictionary<string, string> { ["KEY"] = "it's a value" };
        var plist = UnixUserEnvironmentService.BuildLaunchAgentPlist(vars);

        // Shell escaping: ' → '\'' which after XML escaping (' → &apos;) becomes &apos;\&apos;&apos;
        Assert.Contains("it&apos;\\&apos;&apos;s a value", plist);
    }

    [Fact]
    public void FullFlow_macOS_SetThenRemoveAll_CleansPlistAndStore()
    {
        var svc = Build();
        svc.Set("KEY_A", "1");
        svc.WriteLaunchAgentCore();
        svc.Set("KEY_B", "2");
        svc.WriteLaunchAgentCore();

        svc.Remove("KEY_A");
        svc.Remove("KEY_B");
        svc.CleanLaunchAgentIfEmptyCore();

        var storeVarLines = File.ReadAllLines(EnvFile)
            .Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith('#'))
            .ToList();
        Assert.Empty(storeVarLines);
        Assert.False(File.Exists(PlistFile));
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
