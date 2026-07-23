using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.Core.Skills;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Skills;

public sealed class AsmProvisionService
{
    public const string DefaultVersion = "2.14.0";
    private readonly IPlatformInfo _platform;
    private readonly SemaphoreSlim _installLock = new(1, 1);

    public AsmProvisionService(IPlatformInfo? platform = null) => _platform = platform ?? IPlatformInfo.Current;

    public string InstallDirectory => Path.Combine(_platform.LocalDataDirectory, "skills-engine");
    public string EntryPointPath => Path.Combine(InstallDirectory, "node_modules", "agent-skill-manager", "dist", "agent-skill-manager.js");
    public string PackageJsonPath => Path.Combine(InstallDirectory, "node_modules", "agent-skill-manager", "package.json");
    public string? NodePath { get; private set; }
    public string? NpmPath { get; private set; }

    public async Task<AsmPrerequisiteStatus> CheckPrerequisitesAsync(CancellationToken ct = default)
    {
        var node = await DetectAsync("node", "--version", ct).ConfigureAwait(false);
        var npm = await DetectAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", "--version", ct).ConfigureAwait(false);
        NodePath = node.Path;
        NpmPath = npm.Path;

        var nodeVersion = ParseVersion(node.Output);
        var npmVersion = ParseVersion(npm.Output);
        var compatible = nodeVersion is { Major: >= 18 } && npmVersion is { Major: >= 9 };
        var reason = compatible ? "" : BuildFailureReason(node.Path, nodeVersion, npm.Path, npmVersion);
        return new(node.Path, nodeVersion?.ToString(), npm.Path, npmVersion?.ToString(), compatible, reason);
    }

    public bool IsAsmInstalled() => File.Exists(EntryPointPath) && File.Exists(PackageJsonPath);

    public async Task<string?> GetInstalledVersionAsync(CancellationToken ct = default)
    {
        if (!File.Exists(PackageJsonPath)) return null;
        await using var stream = File.OpenRead(PackageJsonPath);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return json.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }

    public async Task<string> CheckForUpdateAsync(CancellationToken ct = default)
    {
        EnsureNpm();
        var result = await RunAsync(NpmPath!, ["view", "agent-skill-manager", "version", "--json"], ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<string>(result.Output) ?? result.Output.Trim().Trim('"');
    }

    public async Task InstallAsync(string version, CancellationToken ct = default)
    {
        EnsureNpm();
        await _installLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(InstallDirectory);
            await RunAsync(NpmPath!, ["install", "--prefix", InstallDirectory, $"agent-skill-manager@{version}", "--save-exact"], ct).ConfigureAwait(false);
            if (!IsAsmInstalled()) throw new InvalidOperationException("ASM installation completed but local entry point is missing.");
        }
        finally
        {
            _installLock.Release();
        }
    }

    public Task UpdateAsync(string version, CancellationToken ct = default) => InstallAsync(version, ct);

    private void EnsureNpm()
    {
        if (string.IsNullOrWhiteSpace(NpmPath)) throw new InvalidOperationException("Run prerequisite check before using npm.");
    }

    private static string BuildFailureReason(string? nodePath, Version? node, string? npmPath, Version? npm)
    {
        if (nodePath is null) return "Node.js was not found. Skills requires Node.js 18 or newer and npm 9 or newer.";
        if (node is null || node.Major < 18) return $"Node.js {node?.ToString() ?? "version unknown"} is incompatible. Skills requires Node.js 18 or newer.";
        if (npmPath is null) return "npm was not found. Skills requires npm 9 or newer.";
        return $"npm {npm?.ToString() ?? "version unknown"} is incompatible. Skills requires npm 9 or newer.";
    }

    internal static Version? ParseVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var token = value.Trim().TrimStart('v').Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        var core = token.Split('-', 2)[0];
        return Version.TryParse(core, out var version) ? version : null;
    }

    private static async Task<(string? Path, string Output)> DetectAsync(string fileName, string argument, CancellationToken ct)
    {
        try
        {
            var path = ResolveExecutable(fileName);
            var result = await RunAsync(path, [argument], ct).ConfigureAwait(false);
            return (path, result.Output.Trim());
        }
        catch
        {
            return (null, "");
        }
    }

    private static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathRooted(fileName)) return fileName;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [""];
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        foreach (var extension in extensions)
        {
            var candidate = Path.Combine(directory.Trim('"'), Path.HasExtension(fileName) ? fileName : fileName + extension.ToLowerInvariant());
            if (File.Exists(candidate)) return candidate;
        }
        return fileName;
    }

    private static async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var isCommandScript = OperatingSystem.IsWindows() && Path.GetExtension(fileName).Equals(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isCommandScript ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (isCommandScript)
        {
            psi.ArgumentList.Add("/d");
            psi.ArgumentList.Add("/s");
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(fileName);
        }
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"Failed to start {fileName}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException($"{fileName} was not found.", ex);
        }
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? output.Trim() : error.Trim());
        return new(output, error);
    }

    private sealed record ProcessResult(string Output, string Error);
}
