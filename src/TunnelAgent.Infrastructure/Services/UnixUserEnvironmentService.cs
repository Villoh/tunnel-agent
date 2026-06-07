using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Services;

/// <summary>
/// Linux and macOS implementation.
/// <para>
/// On Unix, <see cref="EnvironmentVariableTarget.User"/> has no persistent
/// backing store — it is silently equivalent to <see cref="EnvironmentVariableTarget.Process"/>.
/// This service persists variables through two complementary mechanisms:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>App-owned store</b> ($XDG_CONFIG_HOME/tunnelagent/environment):
///     a shell-sourceable file with <c>export KEY=VALUE</c> lines, read at
///     startup to seed the process environment.
///   </item>
///   <item>
///     <b>Linux – ~/.profile source hook</b>: a single line
///     <c>[ -f "..." ] &amp;&amp; . "..."</c> is written once to ~/.profile so
///     that all login sessions (terminals, GUI apps) inherit the variables
///     after the next login — without modifying ~/.profile on every change.
///   </item>
///   <item>
///     <b>macOS – launchctl setenv</b>: propagates the variable to the current
///     macOS GUI session immediately (visible to all apps launched from the
///     Dock/Spotlight without needing a logout).
///   </item>
/// </list>
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixUserEnvironmentService : IUserEnvironmentService
{
    private static readonly string Profile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".profile");

    // App-owned store — shell-sourceable, managed entirely by TunnelAgent.
    // SpecialFolder.ApplicationData on Linux resolves $XDG_CONFIG_HOME
    // (fallback ~/.config), honouring the XDG Base Directory spec.
    private static readonly string AppEnvFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "tunnelagent", "environment");

    // The single source hook line we inject into ~/.profile (Linux only).
    private static readonly string ProfileSourceLine =
        $". \"{AppEnvFile}\"";

    // Guard comment that brackets our block so we can detect it.
    private const string ProfileBlockBegin = "# BEGIN TunnelAgent";
    private const string ProfileBlockEnd   = "# END TunnelAgent";

    private readonly object _fileLock = new();

    // ── Startup seeding ───────────────────────────────────────────────────────

    /// <summary>
    /// Called once at startup: seeds the current process environment from the
    /// app-owned store so that the app itself sees persisted variables.
    /// </summary>
    internal void SeedProcessEnvironment()
    {
        foreach (var (k, v) in ReadAppStore())
            Environment.SetEnvironmentVariable(k, v, EnvironmentVariableTarget.Process);
    }

    // ── IUserEnvironmentService ───────────────────────────────────────────────

    public string? Get(string name) =>
        ReadAppStore().TryGetValue(name, out var stored)
            ? stored
            : Environment.GetEnvironmentVariable(name);

    public void Set(string name, string value)
    {
        UpdateAppStore(name, value);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

        if (OperatingSystem.IsLinux())
            EnsureProfileHook();

        if (OperatingSystem.IsMacOS())
            TryLaunchctlSetenv(name, value);
    }

    public void Remove(string name)
    {
        RemoveFromAppStore(name);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);

        if (OperatingSystem.IsLinux())
            CleanProfileHookIfEmpty();

        if (OperatingSystem.IsMacOS())
            TryLaunchctlUnsetenv(name);
    }

    // ── App-owned store ───────────────────────────────────────────────────────

    private Dictionary<string, string> ReadAppStore()
    {
        lock (_fileLock)
            return ReadAppStoreUnlocked();
    }

    private void UpdateAppStore(string name, string value)
    {
        lock (_fileLock)
        {
            var vars = ReadAppStoreUnlocked();
            vars[name] = value;
            WriteAppStoreUnlocked(vars);
        }
    }

    private void RemoveFromAppStore(string name)
    {
        lock (_fileLock)
        {
            var vars = ReadAppStoreUnlocked();
            if (!vars.Remove(name)) return;
            WriteAppStoreUnlocked(vars);
        }
    }

    private Dictionary<string, string> ReadAppStoreUnlocked()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(AppEnvFile)) return result;

        foreach (var line in File.ReadLines(AppEnvFile))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            // Strip leading "export " if present
            var entry = line.StartsWith("export ", StringComparison.Ordinal)
                ? line["export ".Length..]
                : line;
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            result[entry[..eq].Trim()] = entry[(eq + 1)..];
        }

        return result;
    }

    private static void WriteAppStoreUnlocked(Dictionary<string, string> vars)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppEnvFile)!);

        var sb = new StringBuilder();
        sb.AppendLine("# Managed by Tunnel Agent — do not edit manually.");
        foreach (var (k, v) in vars)
            sb.Append("export ").Append(k).Append('=').AppendLine(v);

        File.WriteAllText(AppEnvFile, sb.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TryChmod600(AppEnvFile);
    }

    // ── Linux: ~/.profile source hook ────────────────────────────────────────

    /// <summary>
    /// Writes a guarded block into ~/.profile that sources the app-owned store.
    /// If the block already exists it is left untouched (idempotent).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void EnsureProfileHook()
    {
        lock (_fileLock)
        {
            var content = File.Exists(Profile) ? File.ReadAllText(Profile) : string.Empty;
            if (content.Contains(ProfileBlockBegin)) return; // already installed

            var hook = new StringBuilder();
            if (content.Length > 0 && !content.EndsWith('\n'))
                hook.AppendLine();
            hook.AppendLine(ProfileBlockBegin);
            hook.AppendLine($"[ -f \"{AppEnvFile}\" ] && . \"{AppEnvFile}\"");
            hook.AppendLine(ProfileBlockEnd);

            File.AppendAllText(Profile, hook.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    /// <summary>
    /// Removes the guarded block from ~/.profile when the store becomes empty
    /// (no variables left to export — no point keeping the source hook).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void CleanProfileHookIfEmpty()
    {
        lock (_fileLock)
        {
            if (ReadAppStoreUnlocked().Count > 0) return; // still has variables
            if (!File.Exists(Profile)) return;

            var lines = File.ReadAllLines(Profile);
            var filtered = new List<string>();
            bool inBlock = false;

            foreach (var line in lines)
            {
                if (line.TrimEnd() == ProfileBlockBegin) { inBlock = true; continue; }
                if (line.TrimEnd() == ProfileBlockEnd)   { inBlock = false; continue; }
                if (!inBlock) filtered.Add(line);
            }

            // Trim trailing blank lines left by the removed block
            while (filtered.Count > 0 && string.IsNullOrWhiteSpace(filtered[^1]))
                filtered.RemoveAt(filtered.Count - 1);

            File.WriteAllLines(Profile, filtered,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    // ── macOS: launchctl ──────────────────────────────────────────────────────

    [SupportedOSPlatform("macos")]
    private static void TryLaunchctlSetenv(string name, string value)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("launchctl")
            {
                ArgumentList = { "setenv", name, value },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    [SupportedOSPlatform("macos")]
    private static void TryLaunchctlUnsetenv(string name)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("launchctl")
            {
                ArgumentList = { "unsetenv", name },
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(2000);
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void TryChmod600(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { }
    }
}
