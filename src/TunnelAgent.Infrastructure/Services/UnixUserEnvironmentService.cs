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
    private const string ProfileBlockBegin = "# BEGIN TunnelAgent";
    private const string ProfileBlockEnd   = "# END TunnelAgent";

    private readonly string _appEnvFile;
    private readonly string _profile;
    private readonly object _fileLock = new();

    // Production constructor — uses real XDG/home paths.
    internal UnixUserEnvironmentService()
        : this(
            appEnvFile: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "tunnelagent", "environment"),
            profile: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".profile"))
    { }

    // Testable constructor — caller supplies isolated temp paths.
    internal UnixUserEnvironmentService(string appEnvFile, string profile)
    {
        _appEnvFile = appEnvFile;
        _profile    = profile;
    }

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
        else if (OperatingSystem.IsMacOS())
            TryLaunchctlSetenv(name, value);
    }

    public void Remove(string name)
    {
        RemoveFromAppStore(name);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);

        if (OperatingSystem.IsLinux())
            CleanProfileHookIfEmpty();
        else if (OperatingSystem.IsMacOS())
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
        if (!File.Exists(_appEnvFile)) return result;

        foreach (var line in File.ReadLines(_appEnvFile))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var entry = line.StartsWith("export ", StringComparison.Ordinal)
                ? line["export ".Length..]
                : line;
            var eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            result[entry[..eq].Trim()] = entry[(eq + 1)..];
        }

        return result;
    }

    private void WriteAppStoreUnlocked(Dictionary<string, string> vars)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_appEnvFile)!);

        var sb = new StringBuilder();
        sb.AppendLine("# Managed by Tunnel Agent — do not edit manually.");
        foreach (var (k, v) in vars)
            sb.Append("export ").Append(k).Append('=').AppendLine(v);

        File.WriteAllText(_appEnvFile, sb.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TryChmod600(_appEnvFile);
    }

    // ── Linux: ~/.profile source hook ────────────────────────────────────────

    /// <summary>
    /// Writes a guarded block into ~/.profile that sources the app-owned store.
    /// If the block already exists it is left untouched (idempotent).
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void EnsureProfileHook() => EnsureProfileHookCore();

    [SupportedOSPlatform("linux")]
    private void CleanProfileHookIfEmpty() => CleanProfileHookIfEmptyCore();

    // Platform-attribute-free implementations so tests can call them directly.
    internal void EnsureProfileHookCore()
    {
        lock (_fileLock)
        {
            var content = File.Exists(_profile) ? File.ReadAllText(_profile) : string.Empty;
            if (content.Contains(ProfileBlockBegin)) return;

            var hook = new StringBuilder();
            if (content.Length > 0 && !content.EndsWith('\n'))
                hook.AppendLine();
            hook.AppendLine(ProfileBlockBegin);
            hook.AppendLine($"[ -f \"{_appEnvFile}\" ] && . \"{_appEnvFile}\"");
            hook.AppendLine(ProfileBlockEnd);

            File.AppendAllText(_profile, hook.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    internal void CleanProfileHookIfEmptyCore()
    {
        lock (_fileLock)
        {
            if (ReadAppStoreUnlocked().Count > 0) return;
            if (!File.Exists(_profile)) return;

            var lines = File.ReadAllLines(_profile);
            var filtered = new List<string>();
            bool inBlock = false;

            foreach (var line in lines)
            {
                if (line.TrimEnd() == ProfileBlockBegin) { inBlock = true; continue; }
                if (line.TrimEnd() == ProfileBlockEnd)   { inBlock = false; continue; }
                if (!inBlock) filtered.Add(line);
            }

            while (filtered.Count > 0 && string.IsNullOrWhiteSpace(filtered[^1]))
                filtered.RemoveAt(filtered.Count - 1);

            File.WriteAllLines(_profile, filtered,
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
