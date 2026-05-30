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

    public async Task<IReadOnlyList<AgentDetectionResult>> DetectAllAsync(CancellationToken ct = default)
    {
        var tasks = AgentCatalog.All.Select(async def =>
        {
            var binaryPath = await FindBinaryAsync(def.BinaryNames, ct).ConfigureAwait(false);
            var configured = binaryPath != null && CheckConfigured(def.ConfigPaths);
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
        foreach (var name in names)
        {
            // Step 1: where.exe / which
            var found = await WhichAsync(name, ct).ConfigureAwait(false);
            if (found != null) return found;

            // Step 2: PATH scan
            found = ScanPath(name);
            if (found != null) return found;

            // Step 3: Well-known dirs (Windows)
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                found = ScanWellKnownDirs(name);
                if (found != null) return found;
            }
        }
        return null;
    }

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
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ';' : ':';
        foreach (var dir in path.Split(sep, StringSplitOptions.RemoveEmptyEntries))
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

    internal static bool CheckConfigured(string[] configPaths)
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
                var text = File.ReadAllText(expanded);
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
