using System.Diagnostics;
using TunnelAgent.Infrastructure.Engine;

namespace TunnelAgent.Tests;

public sealed class EnginePidFileTests
{
    [Fact]
    public void PathForServerEntry_UsesEngineDirectory()
    {
        using var temp = new TestTempDirectory();
        var serverPath = Path.Combine(temp.Path, "app", "custom-server.js");

        var pidPath = EnginePidFile.PathForServerEntry(serverPath);

        Assert.Equal(Path.Combine(temp.Path, "engine.pid"), pidPath);
    }

    [Fact]
    public void PathForServerEntry_MissingDirectory_Throws()
    {
        Assert.Throws<ArgumentException>(() => EnginePidFile.PathForServerEntry("custom-server.js"));
    }

    [Fact]
    public void Read_MissingOrMalformed_ReturnsNull()
    {
        using var temp = new TestTempDirectory();
        var path = temp.File("engine.pid");

        Assert.Null(EnginePidFile.Read(path));

        File.WriteAllText(path, "{not-json");
        Assert.Null(EnginePidFile.Read(path));

        File.WriteAllText(path, """{"pid":0,"startTimeUtc":"2026-01-01T00:00:00Z","serverEntryPath":"x"}""");
        Assert.Null(EnginePidFile.Read(path));
    }

    [Fact]
    public void TryKillRecorded_MatchingProcess_KillsAndDeletesFile()
    {
        using var temp = new TestTempDirectory();
        var serverPath = Path.Combine(temp.Path, "app", "custom-server.js");
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        File.WriteAllText(serverPath, "// stub");
        var pidPath = EnginePidFile.PathForServerEntry(serverPath);

        using var leftover = StartHangProcess();
        try
        {
            EnginePidFile.Write(pidPath, leftover, serverPath);
            Assert.True(File.Exists(pidPath));

            Assert.True(EnginePidFile.TryKillRecorded(pidPath, serverPath));

            leftover.WaitForExit(2000);
            leftover.Refresh();
            Assert.True(leftover.HasExited);
            Assert.False(File.Exists(pidPath));
        }
        finally
        {
            TryKill(leftover);
        }
    }

    [Fact]
    public void TryKillRecorded_DifferentServerEntry_LeavesProcessAndFile()
    {
        using var temp = new TestTempDirectory();
        var serverPath = Path.Combine(temp.Path, "app", "custom-server.js");
        var otherPath = Path.Combine(temp.Path, "app", "server.js");
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        File.WriteAllText(serverPath, "// stub");
        var pidPath = EnginePidFile.PathForServerEntry(serverPath);

        using var leftover = StartHangProcess();
        try
        {
            EnginePidFile.Write(pidPath, leftover, serverPath);

            Assert.False(EnginePidFile.TryKillRecorded(pidPath, otherPath));
            leftover.Refresh();
            Assert.False(leftover.HasExited);
            Assert.True(File.Exists(pidPath));
        }
        finally
        {
            TryKill(leftover);
        }
    }

    [Fact]
    public void TryKillRecorded_StalePid_DeletesFileWithoutThrowing()
    {
        using var temp = new TestTempDirectory();
        var serverPath = Path.Combine(temp.Path, "app", "custom-server.js");
        Directory.CreateDirectory(Path.GetDirectoryName(serverPath)!);
        var pidPath = EnginePidFile.PathForServerEntry(serverPath);
        var record = new EnginePidFile.Record
        {
            Pid = 2147483647,
            StartTimeUtc = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ServerEntryPath = Path.GetFullPath(serverPath)
        };
        File.WriteAllText(pidPath, System.Text.Json.JsonSerializer.Serialize(record));

        Assert.False(EnginePidFile.TryKillRecorded(pidPath, serverPath));
        Assert.False(File.Exists(pidPath));
    }

    internal static Process StartHangProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping")
            {
                Arguments = "-n 60 127.0.0.1",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
            : new ProcessStartInfo("sleep")
            {
                ArgumentList = { "60" },
                UseShellExecute = false,
                CreateNoWindow = true
            };

        var process = Process.Start(startInfo);
        Assert.NotNull(process);
        return process!;
    }

    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Test cleanup.
        }
    }
}
