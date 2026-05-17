using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace TunnelAgent.Services;

public interface ILaunchAtLoginService
{
    bool IsSupported { get; }
    Task<bool> GetEnabledAsync();
    Task SetEnabledAsync(bool enabled);
}

public sealed class LaunchAtLoginService : ILaunchAtLoginService
{
    private const string AppName = "TunnelAgent";
    private const string WindowsRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _executablePath;

    public LaunchAtLoginService() : this(GetExecutablePath()) { }

    public LaunchAtLoginService(string executablePath) => _executablePath = executablePath;

    public bool IsSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public Task<bool> GetEnabledAsync()
    {
        if (OperatingSystem.IsWindows()) return Task.FromResult(GetWindowsEnabled());
        if (OperatingSystem.IsMacOS()) return Task.FromResult(File.Exists(MacLaunchAgentPath));
        if (OperatingSystem.IsLinux()) return Task.FromResult(File.Exists(LinuxDesktopFilePath));
        return Task.FromResult(false);
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        if (!IsSupported) return;

        if (OperatingSystem.IsWindows())
        {
            SetWindowsEnabled(enabled);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            await SetMacEnabledAsync(enabled).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            await SetLinuxEnabledAsync(enabled).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private bool GetWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: false);
        var value = key?.GetValue(AppName) as string;
        return !string.IsNullOrWhiteSpace(value);
    }

    [SupportedOSPlatform("windows")]
    private void SetWindowsEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(WindowsRunKey, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(WindowsRunKey, writable: true);

        if (enabled)
            key.SetValue(AppName, $"{QuoteForShell(_executablePath)} --start-in-tray", RegistryValueKind.String);
        else
            key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    private static string MacLaunchAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        "Library", "LaunchAgents", "com.tunnelagent.app.plist");

    private async Task SetMacEnabledAsync(bool enabled)
    {
        if (!enabled)
        {
            TryLaunchctl("bootout", MacLaunchAgentPath);
            if (File.Exists(MacLaunchAgentPath)) File.Delete(MacLaunchAgentPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(MacLaunchAgentPath)!);
        await File.WriteAllTextAsync(MacLaunchAgentPath, BuildLaunchAgentPlist(_executablePath), Encoding.UTF8)
            .ConfigureAwait(false);
        TryLaunchctl("bootstrap", MacLaunchAgentPath);
    }

    private static string LinuxDesktopFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "autostart", "tunnelagent.desktop");

    private async Task SetLinuxEnabledAsync(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(LinuxDesktopFilePath)) File.Delete(LinuxDesktopFilePath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(LinuxDesktopFilePath)!);
        await File.WriteAllTextAsync(LinuxDesktopFilePath, BuildDesktopFile(_executablePath), Encoding.UTF8)
            .ConfigureAwait(false);
    }

    private static string GetExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;

        path = Process.GetCurrentProcess().MainModule?.FileName;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;

        path = typeof(Program).Assembly.Location;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;

        throw new InvalidOperationException("Could not resolve current executable path.");
    }

    private static string QuoteForShell(string path) => $"\"{path.Replace("\"", "\\\"")}\"";

    private static string EscapeXml(string value) => SecurityElement.Escape(value) ?? value;

    private static string EscapeDesktopExecPath(string path) =>
        $"\"{path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%")}\"";

    private static string BuildLaunchAgentPlist(string executablePath) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>com.tunnelagent.app</string>
            <key>ProgramArguments</key>
            <array>
                <string>{EscapeXml(executablePath)}</string>
                <string>--start-in-tray</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <key>KeepAlive</key>
            <false/>
        </dict>
        </plist>
        """.TrimStart();

    private static string BuildDesktopFile(string executablePath) =>
        $"""
        [Desktop Entry]
        Type=Application
        Version=1.0
        Name=Tunnel Agent
        Comment=Start Tunnel Agent at login
        Exec={EscapeDesktopExecPath(executablePath)} --start-in-tray
        Terminal=false
        X-GNOME-Autostart-enabled=true
        """.TrimStart();

    private static void TryLaunchctl(string verb, string plistPath)
    {
        try
        {
            var uid = GetUnixUserId();
            if (string.IsNullOrWhiteSpace(uid)) return;

            var startInfo = new ProcessStartInfo("launchctl") { UseShellExecute = false };
            startInfo.ArgumentList.Add(verb);
            startInfo.ArgumentList.Add($"gui/{uid}");
            startInfo.ArgumentList.Add(plistPath);
            Process.Start(startInfo)?.Dispose();
        }
        catch
        {
            // Plist presence is enough for next login; launchctl only syncs current session.
        }
    }

    private static string? GetUnixUserId()
    {
        var uid = Environment.GetEnvironmentVariable("UID");
        if (!string.IsNullOrWhiteSpace(uid)) return uid;

        try
        {
            var startInfo = new ProcessStartInfo("id", "-u")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit(1000);
            return process.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
