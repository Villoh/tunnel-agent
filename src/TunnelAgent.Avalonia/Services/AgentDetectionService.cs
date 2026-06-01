using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

public sealed class AgentDetectionService : IAgentDetectionService
{
    private readonly Dictionary<string, AgentDetectionResult> _cache = new();
    private static string[]? _pathDirs;
    private static string[] PathDirs => _pathDirs ??=
        (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':', StringSplitOptions.RemoveEmptyEntries);

    public async Task<IReadOnlyList<AgentDetectionResult>> DetectAllAsync(CancellationToken ct = default)
    {
        var tasks = AgentCatalog.All.Select(async def =>
        {
            var binaryPath = await FindBinaryAsync(def.BinaryNames, ct).ConfigureAwait(false);
            var configured = binaryPath != null && await CheckConfiguredAsync(def.ConfigPaths, ct).ConfigureAwait(false);
            return new AgentDetectionResult(def.Id, binaryPath != null, configured, binaryPath, null);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var r in results)
            _cache[r.AgentId] = r;

        return results;
    }

    public AgentDetectionResult GetCached(string agentId) =>
        _cache.TryGetValue(agentId, out var r) ? r : AgentDetectionResult.NotFound(agentId);

    private static async Task<string?> FindBinaryAsync(string[] names, CancellationToken ct)
    {
        // Run all binary name checks in parallel and return the first hit.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = names.Select(name => FindSingleBinaryAsync(name, cts.Token)).ToList();
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            var result = await done.ConfigureAwait(false);
            if (result != null)
            {
                cts.Cancel();
                return result;
            }
        }
        return null;
    }

    private static Task<string?> FindSingleBinaryAsync(string name, CancellationToken ct) =>
        Task.Run(async () =>
        {
            // Step 1: PATH scan
            var found = ScanPath(name);
            if (found != null) return found;

            // Step 2: Well-known dirs
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                found = ScanWellKnownDirs(name);
                if (found != null) return found;
            }

            // Step 3: where.exe / which as fallback for exotic installs
            return await WhichAsync(name, ct).ConfigureAwait(false);
        }, ct);

    private static async Task<string?> WhichAsync(string name, CancellationToken ct)
    {
        var tool = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "where.exe" : "which";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMilliseconds(1500));

            var psi = new ProcessStartInfo(tool, name)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return null;

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);

            if (proc.ExitCode != 0) return null;

            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Trim())
                             .FirstOrDefault(l => !string.IsNullOrEmpty(l));

            return line != null && File.Exists(line) ? line : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ScanPath(string name)
    {
        foreach (var dir in PathDirs)
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private static string? ScanWellKnownDirs(string name)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var dirs = new[]
        {
            Path.Combine(localApp, "Programs"),
            Path.Combine(appData, "npm"),
            Path.Combine(profile, ".local", "bin"),
            Path.Combine(profile, ".cargo", "bin"),
        };

        foreach (var dir in dirs)
        {
            var full = Path.Combine(dir, name);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    internal static async Task<bool> CheckConfiguredAsync(string[] configPaths, CancellationToken ct = default)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var raw in configPaths)
        {
            var expanded = raw.StartsWith("~/", StringComparison.Ordinal)
                ? Path.Combine(profile, raw[2..].Replace('/', Path.DirectorySeparatorChar))
                : raw;

            if (!File.Exists(expanded)) continue;
            try
            {
                var text = await File.ReadAllTextAsync(expanded, ct).ConfigureAwait(false);
                if (text.Contains("127.0.0.1") || text.Contains("localhost") || text.Contains("cliproxyapi"))
                    return true;
            }
            catch
            {
                // unreadable or malformed — not configured
            }
        }
        return false;
    }
}
