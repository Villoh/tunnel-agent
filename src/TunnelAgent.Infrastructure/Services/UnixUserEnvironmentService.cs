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
///     <b>App-owned store</b> ($XDG_CONFIG_HOME/tunnelagent/environment, default ~/.config/tunnelagent/environment): a simple
///     KEY=VALUE file read at startup. Ensures the app sees saved variables on next launch.
///   </item>
///   <item>
///     <b>Linux – systemd environment.d</b>
///     ($XDG_CONFIG_HOME/environment.d/tunnelagent.conf): picked up automatically by
///     systemd ≥ 233 user sessions and by PAM on most modern distros, so the
///     variables are available to all graphical and login sessions after the
///     next login.
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
    // App-owned persistent store — always written on every OS.
    // SpecialFolder.ApplicationData on Linux resolves $XDG_CONFIG_HOME (fallback ~/.config),
    // honouring the XDG Base Directory spec instead of hardcoding ~/.config.
    private static readonly string AppEnvFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "tunnelagent", "environment");

    // Linux: systemd/PAM environment.d drop-in.
    // environment.d must live under the real XDG config dir, not a custom one.
    private static readonly string LinuxEnvDFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "environment.d", "tunnelagent.conf");

    private readonly object _fileLock = new();

    // ── Startup seeding ───────────────────────────────────────────────────────

    /// <summary>
    /// Called once at startup: seeds the current process environment from the
    /// app-owned store so that child processes inherit persisted variables.
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
            WriteLinuxEnvD();

        if (OperatingSystem.IsMacOS())
            TryLaunchctlSetenv(name, value);
    }

    public void Remove(string name)
    {
        RemoveFromAppStore(name);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);

        if (OperatingSystem.IsLinux())
            WriteLinuxEnvD();

        if (OperatingSystem.IsMacOS())
            TryLaunchctlUnsetenv(name);
    }

    // ── App-owned store ───────────────────────────────────────────────────────

    /// <summary>Reads the entire app-owned env file as a dictionary.</summary>
    private Dictionary<string, string> ReadAppStore()
    {
        lock (_fileLock)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!File.Exists(AppEnvFile)) return result;

            foreach (var line in File.ReadLines(AppEnvFile))
            {
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                result[line[..eq].Trim()] = line[(eq + 1)..];
            }

            return result;
        }
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

    /// <remarks>Must be called with <see cref="_fileLock"/> held.</remarks>
    private Dictionary<string, string> ReadAppStoreUnlocked()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(AppEnvFile)) return result;

        foreach (var line in File.ReadLines(AppEnvFile))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq].Trim()] = line[(eq + 1)..];
        }

        return result;
    }

    /// <remarks>Must be called with <see cref="_fileLock"/> held.</remarks>
    private static void WriteAppStoreUnlocked(Dictionary<string, string> vars)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppEnvFile)!);

        var sb = new StringBuilder();
        sb.AppendLine("# Managed by Tunnel Agent — do not edit manually.");
        foreach (var (k, v) in vars)
            sb.Append(k).Append('=').AppendLine(v);

        File.WriteAllText(AppEnvFile, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        TryChmod600(AppEnvFile);
    }

    // ── Linux: environment.d ──────────────────────────────────────────────────

    /// <summary>
    /// Rewrites ~/.config/environment.d/tunnelagent.conf from the current
    /// app store state.  The format follows the systemd.environment(7)
    /// spec: one KEY=VALUE per line, no export keyword, no quoting needed
    /// for simple values, but we quote values that contain whitespace.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private void WriteLinuxEnvD()
    {
        lock (_fileLock)
        {
            var vars = ReadAppStoreUnlocked();
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxEnvDFile)!);

            if (vars.Count == 0)
            {
                if (File.Exists(LinuxEnvDFile)) File.Delete(LinuxEnvDFile);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("# Managed by Tunnel Agent — do not edit manually.");
            foreach (var (k, v) in vars)
            {
                // systemd environment.d: quote values that contain spaces or special chars.
                var needsQuotes = v.IndexOfAny([' ', '\t', '"', '\'', '\\', '#']) >= 0;
                if (needsQuotes)
                    sb.Append(k).Append("=\"").Append(v.Replace("\\", "\\\\").Replace("\"", "\\\"")).AppendLine("\"");
                else
                    sb.Append(k).Append('=').AppendLine(v);
            }

            File.WriteAllText(LinuxEnvDFile, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    // ── macOS: launchctl ──────────────────────────────────────────────────────

    /// <summary>
    /// Sets a variable in the macOS GUI environment via <c>launchctl setenv</c>.
    /// This makes the variable visible to apps launched after this call within
    /// the current login session (no logout required).
    /// </summary>
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
        catch { /* launchctl not found or denied — app store is still written */ }
    }

    /// <summary>Removes a variable from the macOS GUI environment.</summary>
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
