using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>Describes a Node.js executable that was found and version-checked.</summary>
public sealed record NodeRuntime(string ExecutablePath, Version Version);

/// <summary>
/// Detects a Node.js executable on PATH or common install locations that satisfies the 9Router runtime requirement.
/// </summary>
public sealed class NodeRuntimeDetector
{
    /// <summary>Minimum Node.js major version required by 9Router.</summary>
    public const int MinimumMajorVersion = 18;

    private readonly IReadOnlyList<string> _candidateExecutables;
    private readonly Func<string, string?> _runVersion;

    /// <summary>Creates a detector that searches PATH and well-known Node.js install locations on this machine.</summary>
    public NodeRuntimeDetector()
        : this(EnumerateDefaultCandidates(), RunNodeVersion)
    {
    }

    /// <summary>Creates a detector that uses the supplied candidate executables and version-command runner.</summary>
    public NodeRuntimeDetector(
        IEnumerable<string> candidateExecutables,
        Func<string, string?> runVersion)
    {
        ArgumentNullException.ThrowIfNull(candidateExecutables);
        ArgumentNullException.ThrowIfNull(runVersion);
        _candidateExecutables = candidateExecutables as IReadOnlyList<string> ?? candidateExecutables.ToArray();
        _runVersion = runVersion;
    }

    /// <summary>Detects the first candidate Node.js executable whose reported version is supported.</summary>
    public NodeRuntime? Detect()
    {
        foreach (var path in _candidateExecutables)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            string? output;
            try
            {
                output = _runVersion(path);
            }
            catch
            {
                continue;
            }

            if (!TryParseVersion(output ?? string.Empty, out var version))
                continue;
            if (!IsSupported(version))
                continue;

            return new NodeRuntime(path, version);
        }

        return null;
    }

    /// <summary>Parses a Node.js version string such as <c>v18.20.4</c> or <c>18.0.0</c>.</summary>
    public static bool TryParseVersion(string? output, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var trimmed = output.Trim();
        var token = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
        if (token.Length > 0 && (token[0] is 'v' or 'V'))
            token = token[1..];

        if (token.Length == 0)
            return false;

        if (!Version.TryParse(token, out var parsed))
            return false;

        version = parsed;
        return true;
    }

    /// <summary>Returns whether the given Node.js version meets the 9Router minimum (major 18 or higher).</summary>
    public static bool IsSupported(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return version.Major >= MinimumMajorVersion;
    }

    internal static IEnumerable<string> EnumerateDefaultCandidates()
    {
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var path in EnumeratePathCandidates().Concat(EnumerateWellKnownCandidates()))
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;
            if (seen.Add(path))
                yield return path;
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates()
    {
        yield return OperatingSystem.IsWindows() ? "node.exe" : "node";

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
            yield break;

        var fileName = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var directory in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim().Trim('"');
            if (trimmed.Length == 0)
                continue;

            var candidate = Path.Combine(trimmed, fileName);
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> EnumerateWellKnownCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var path in EnumerateWindowsCandidates())
                yield return path;
            yield break;
        }

        foreach (var path in EnumerateUnixCandidates())
            yield return path;
    }

    private static IEnumerable<string> EnumerateWindowsCandidates()
    {
        yield return ExistingFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "nodejs",
            "node.exe"));
        yield return ExistingFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "nodejs",
            "node.exe"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var path in EnumerateVersionedExecutables(
                     Path.Combine(localAppData, "fnm", "node-versions"),
                     Path.Combine("installation", "node.exe")))
            yield return path;

        var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR");
        if (!string.IsNullOrWhiteSpace(fnmDir))
        {
            foreach (var path in EnumerateVersionedExecutables(
                         Path.Combine(fnmDir, "node-versions"),
                         Path.Combine("installation", "node.exe")))
                yield return path;
        }

        var nvmHome = Environment.GetEnvironmentVariable("NVM_HOME");
        if (string.IsNullOrWhiteSpace(nvmHome))
            nvmHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nvm");

        foreach (var path in EnumerateVersionedExecutables(nvmHome, "node.exe"))
            yield return path;
    }

    private static IEnumerable<string> EnumerateUnixCandidates()
    {
        yield return ExistingFile("/usr/local/bin/node");
        yield return ExistingFile("/opt/homebrew/bin/node");
        yield return ExistingFile("/usr/bin/node");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var path in EnumerateVersionedExecutables(
                     Path.Combine(home, ".nvm", "versions", "node"),
                     Path.Combine("bin", "node")))
            yield return path;

        foreach (var fnmRoot in UnixFnmRoots(home))
        {
            foreach (var path in EnumerateVersionedExecutables(
                         Path.Combine(fnmRoot, "node-versions"),
                         Path.Combine("installation", "bin", "node")))
                yield return path;
        }
    }

    private static IEnumerable<string> UnixFnmRoots(string home)
    {
        yield return Path.Combine(home, ".fnm");
        yield return Path.Combine(home, ".local", "share", "fnm");

        var fnmDir = Environment.GetEnvironmentVariable("FNM_DIR");
        if (!string.IsNullOrWhiteSpace(fnmDir))
            yield return fnmDir;
    }

    private static IEnumerable<string> EnumerateVersionedExecutables(string versionsDirectory, string relativeExecutable)
    {
        foreach (var versionDir in SafeEnumerateDirectories(versionsDirectory))
        {
            var candidate = Path.Combine(versionDir, relativeExecutable);
            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        if (!Directory.Exists(path))
            yield break;

        IEnumerator<string>? enumerator = null;
        try
        {
            enumerator = Directory.EnumerateDirectories(path).GetEnumerator();
        }
        catch
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                string current;
                try
                {
                    if (!enumerator.MoveNext())
                        yield break;
                    current = enumerator.Current;
                }
                catch
                {
                    yield break;
                }

                yield return current;
            }
        }
    }

    private static string ExistingFile(string path) => File.Exists(path) ? path : string.Empty;

    private static string? RunNodeVersion(string executable)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-v");

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return output;
        }
        catch
        {
            return null;
        }
    }
}
