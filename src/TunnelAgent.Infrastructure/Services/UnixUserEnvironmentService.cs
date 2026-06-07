using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Services;

/// <summary>
/// Linux and macOS implementation.
/// <para>
/// On Unix, <see cref="EnvironmentVariableTarget.User"/> has no persistent
/// backing store — it is silently equivalent to <see cref="EnvironmentVariableTarget.Process"/>.
/// This service persists variables through two complementary mechanisms per OS:
/// </para>
/// <list type="bullet">
///   <item>
///     <b>App-owned store</b> (always, both OS): a shell-sourceable file with
///     <c>export KEY=VALUE</c> lines at <c>$XDG_CONFIG_HOME/tunnelagent/environment</c>
///     (Linux) or <c>~/Library/Application Support/tunnelagent/environment</c> (macOS).
///     Read at startup via <see cref="SeedProcessEnvironment"/> to seed the process env.
///   </item>
///   <item>
///     <b>Linux – ~/.profile source hook</b>: a guarded block written once to
///     <c>~/.profile</c> that sources the app-owned store. Visible to all login
///     sessions (terminals, GUI apps) after the next login.
///   </item>
///   <item>
///     <b>macOS – LaunchAgent plist</b>: <c>~/Library/LaunchAgents/com.tunnelagent.environment.plist</c>
///     runs <c>launchctl setenv</c> for every stored variable at each login
///     (<c>RunAtLoad: true</c>). Combined with a direct <c>launchctl setenv</c> call
///     for immediate effect in the current GUI session.
///   </item>
/// </list>
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed class UnixUserEnvironmentService : IUserEnvironmentService
{
    private const string ProfileBlockBegin  = "# BEGIN TunnelAgent";
    private const string ProfileBlockEnd    = "# END TunnelAgent";
    private const string LaunchAgentLabel   = "com.tunnelagent.environment";

    private readonly string _appEnvFile;
    private readonly string _profile;
    private readonly string _launchAgentPlist;
    private readonly object _fileLock = new();

    // Production constructor — uses real OS paths.
    internal UnixUserEnvironmentService()
        : this(
            appEnvFile: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "tunnelagent", "environment"),
            profile: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".profile"),
            launchAgentPlist: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents", $"{LaunchAgentLabel}.plist"))
    { }

    // Testable constructor — caller supplies isolated temp paths.
    internal UnixUserEnvironmentService(string appEnvFile, string profile, string launchAgentPlist)
    {
        _appEnvFile       = appEnvFile;
        _profile          = profile;
        _launchAgentPlist = launchAgentPlist;
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
        {
            WriteLaunchAgent();
            TryLaunchctlSetenv(name, value);
        }
    }

    public void Remove(string name)
    {
        RemoveFromAppStore(name);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);

        if (OperatingSystem.IsLinux())
            CleanProfileHookIfEmpty();
        else if (OperatingSystem.IsMacOS())
        {
            CleanLaunchAgentIfEmpty();
            TryLaunchctlUnsetenv(name);
        }
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

    [SupportedOSPlatform("linux")]
    private void EnsureProfileHook() => EnsureProfileHookCore();

    [SupportedOSPlatform("linux")]
    private void CleanProfileHookIfEmpty() => CleanProfileHookIfEmptyCore();

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
                if (line.TrimEnd() == ProfileBlockBegin) { inBlock = true;  continue; }
                if (line.TrimEnd() == ProfileBlockEnd)   { inBlock = false; continue; }
                if (!inBlock) filtered.Add(line);
            }

            while (filtered.Count > 0 && string.IsNullOrWhiteSpace(filtered[^1]))
                filtered.RemoveAt(filtered.Count - 1);

            File.WriteAllLines(_profile, filtered,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    // ── macOS: LaunchAgent plist ──────────────────────────────────────────────

    [SupportedOSPlatform("macos")]
    private void WriteLaunchAgent() => WriteLaunchAgentCore();

    [SupportedOSPlatform("macos")]
    private void CleanLaunchAgentIfEmpty() => CleanLaunchAgentIfEmptyCore();

    /// <summary>
    /// Rewrites the LaunchAgent plist from the current store state.
    /// The plist runs <c>launchctl setenv</c> for every variable at each login
    /// (<c>RunAtLoad: true</c>), providing persistence across reboots.
    /// </summary>
    internal void WriteLaunchAgentCore()
    {
        lock (_fileLock)
        {
            var vars = ReadAppStoreUnlocked();
            Directory.CreateDirectory(Path.GetDirectoryName(_launchAgentPlist)!);

            if (vars.Count == 0)
            {
                if (File.Exists(_launchAgentPlist)) File.Delete(_launchAgentPlist);
                return;
            }

            File.WriteAllText(_launchAgentPlist, BuildLaunchAgentPlist(vars),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    /// <summary>
    /// Removes the LaunchAgent plist when the store is empty.
    /// </summary>
    internal void CleanLaunchAgentIfEmptyCore()
    {
        lock (_fileLock)
        {
            if (ReadAppStoreUnlocked().Count > 0) return;
            if (File.Exists(_launchAgentPlist)) File.Delete(_launchAgentPlist);
        }
    }

    /// <summary>
    /// Builds the plist XML. Each variable becomes a <c>launchctl setenv KEY VALUE</c>
    /// argument in the shell command executed at login.
    /// </summary>
    internal static string BuildLaunchAgentPlist(Dictionary<string, string> vars)
    {
        // Build the shell command: launchctl setenv K1 V1; launchctl setenv K2 V2; ...
        var cmd = new StringBuilder();
        foreach (var (k, v) in vars)
        {
            if (cmd.Length > 0) cmd.Append("; ");
            cmd.Append("launchctl setenv ")
               .Append(EscapeShell(k))
               .Append(' ')
               .Append(EscapeShell(v));
        }

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>
                <string>{LaunchAgentLabel}</string>
                <key>ProgramArguments</key>
                <array>
                    <string>/bin/sh</string>
                    <string>-c</string>
                    <string>{EscapeXml(cmd.ToString())}</string>
                </array>
                <key>RunAtLoad</key>
                <true/>
            </dict>
            </plist>
            """.TrimStart();
    }

    // ── macOS: launchctl immediate session effect ─────────────────────────────

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

    private static string EscapeXml(string value) =>
        SecurityElement.Escape(value) ?? value;

    /// <summary>
    /// Wraps a shell token in single quotes and escapes any embedded single
    /// quotes using the standard <c>'</c> → <c>'\''</c> technique.
    /// </summary>
    private static string EscapeShell(string value) =>
        $"'{value.Replace("'", "'\\''")}'";

    private static void TryChmod600(string path)
    {
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { }
    }
}
