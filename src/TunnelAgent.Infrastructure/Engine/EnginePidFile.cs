using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace TunnelAgent.Infrastructure.Engine;

/// <summary>
/// Records a managed engine PID so a later Tunnel Agent process can reap a leftover
/// instance after a crash that did not tear the child down.
/// </summary>
internal static class EnginePidFile
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <summary>On-disk record of a process started by Tunnel Agent.</summary>
    internal sealed class Record
    {
        public int Pid { get; set; }
        public DateTime StartTimeUtc { get; set; }
        public string ServerEntryPath { get; set; } = "";
    }

    /// <summary>
    /// PID file stored beside the extracted package: <c>{engineDir}/engine.pid</c>,
    /// derived from <c>{engineDir}/app/custom-server.js</c> (or <c>server.js</c>).
    /// </summary>
    public static string PathForServerEntry(string serverEntryPath)
    {
        var appDir = Path.GetDirectoryName(serverEntryPath);
        if (string.IsNullOrEmpty(appDir))
            throw new ArgumentException("Server entry path must include a directory.", nameof(serverEntryPath));

        var engineDir = Path.GetDirectoryName(appDir);
        if (string.IsNullOrEmpty(engineDir))
            throw new ArgumentException("Server entry path must be inside an engine directory.", nameof(serverEntryPath));

        return Path.Combine(engineDir, "engine.pid");
    }

    /// <summary>Writes <paramref name="process"/> identity so a future start can reap it.</summary>
    public static void Write(string path, Process process, string serverEntryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrWhiteSpace(serverEntryPath);

        var record = new Record
        {
            Pid = process.Id,
            StartTimeUtc = process.StartTime.ToUniversalTime(),
            ServerEntryPath = Path.GetFullPath(serverEntryPath)
        };

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(record, JsonOptions);
        var tmpPath = path + ".tmp";
        File.WriteAllText(tmpPath, json);
        if (File.Exists(path))
            File.Replace(tmpPath, path, null);
        else
            File.Move(tmpPath, path);
    }

    /// <summary>Deletes <paramref name="path"/> if it exists. Ignores I/O errors.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort: a stale file is reaped or ignored on the next start.
        }
    }

    /// <summary>Reads a PID file, or <see langword="null"/> when missing or malformed.</summary>
    public static Record? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var record = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), JsonOptions);
            if (record is null || record.Pid <= 0 || string.IsNullOrWhiteSpace(record.ServerEntryPath))
                return null;

            return record;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Kills the recorded process tree when it is still the same engine instance.
    /// Returns <see langword="true"/> when a live matching process was terminated.
    /// </summary>
    public static bool TryKillRecorded(string path, string serverEntryPath)
    {
        var record = Read(path);
        if (record is null)
        {
            Delete(path);
            return false;
        }

        if (!PathsEqual(record.ServerEntryPath, serverEntryPath))
            return false;

        var killed = false;
        try
        {
            if (IsSameEngineProcess(record, serverEntryPath))
            {
                using var process = Process.GetProcessById(record.Pid);
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                    killed = true;
                }
            }
        }
        catch (ArgumentException)
        {
            // PID is not running.
        }
        catch (InvalidOperationException)
        {
            // Process exited between lookup and kill.
        }
        catch
        {
            // Access denied or the process cannot be signaled — leave the file for a retry.
            return false;
        }

        Delete(path);
        return killed;
    }

    internal static bool IsSameEngineProcess(Record record, string serverEntryPath)
    {
        if (!PathsEqual(record.ServerEntryPath, serverEntryPath))
            return false;

        Process process;
        try
        {
            process = Process.GetProcessById(record.Pid);
        }
        catch (ArgumentException)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
                return false;

            var startUtc = process.StartTime.ToUniversalTime();
            var delta = (startUtc - record.StartTimeUtc).Duration();
            return delta <= StartTimeTolerance;
        }
        catch
        {
            return false;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            left = Path.GetFullPath(left);
            right = Path.GetFullPath(right);
        }
        catch
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }
}
