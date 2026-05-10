using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Per-platform knowledge needed to manage the CLIProxyAPI binary.
/// </summary>
public interface IPlatformInfo
{
    /// <summary>Filename used on disk, e.g. CLIProxyAPI.exe / CLIProxyAPI</summary>
    string BinaryName { get; }

    /// <summary>Filename inside the release archive, e.g. cli-proxy-api.exe / cli-proxy-api</summary>
    string ArchiveBinaryName { get; }

    /// <summary>OS segment of the release asset name, e.g. windows / darwin / linux</summary>
    string OsSuffix { get; }

    /// <summary>Architecture segment of the release asset name, e.g. amd64 / aarch64</summary>
    string ArchSuffix { get; }

    /// <summary>Archive file extension, e.g. .zip / .tar.gz</summary>
    string ArchiveExtension { get; }

    /// <summary>Full platform suffix used in the asset filename, e.g. windows_amd64</summary>
    string PlatformSuffix => $"{OsSuffix}_{ArchSuffix}";

    /// <summary>
    /// Directory for user settings (roams with the user).
    /// Windows: %AppData%\TunnelAgent
    /// macOS:   ~/Library/Preferences/TunnelAgent
    /// Linux:   ~/.config/TunnelAgent
    /// </summary>
    string SettingsDirectory { get; }

    /// <summary>
    /// Directory for machine-local data such as downloaded binaries.
    /// Windows: %LocalAppData%\TunnelAgent
    /// macOS:   ~/Library/Application Support/TunnelAgent
    /// Linux:   ~/.local/share/TunnelAgent
    /// </summary>
    string LocalDataDirectory { get; }

    /// <summary>Any post-install steps needed after the binary is placed (e.g. chmod +x).</summary>
    Task PostInstallAsync(string binaryPath);

    /// <summary>Returns the correct IPlatformInfo for the current runtime.</summary>
    static IPlatformInfo Current { get; } = Detect();

    private static IPlatformInfo Detect()
    {
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64"
            : "amd64";

        if (System.OperatingSystem.IsWindows()) return new WindowsPlatform(arch);
        if (System.OperatingSystem.IsMacOS())   return new MacOsPlatform(arch);
        return new LinuxPlatform(arch);
    }
}

public sealed class WindowsPlatform(string arch) : IPlatformInfo
{
    public string BinaryName          => "CLIProxyAPI.exe";
    public string ArchiveBinaryName   => "cli-proxy-api.exe";
    public string OsSuffix            => "windows";
    public string ArchSuffix          => arch;
    public string ArchiveExtension    => ".zip";
    public string SettingsDirectory   => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TunnelAgent");
    public string LocalDataDirectory  => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelAgent");
    public Task PostInstallAsync(string binaryPath) => Task.CompletedTask;
}

public sealed class MacOsPlatform(string arch) : IPlatformInfo
{
    public string BinaryName          => "CLIProxyAPI";
    public string ArchiveBinaryName   => "cli-proxy-api";
    public string OsSuffix            => "darwin";
    public string ArchSuffix          => arch;
    public string ArchiveExtension    => ".tar.gz";
    public string SettingsDirectory   => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        "Library", "Preferences", "TunnelAgent");
    public string LocalDataDirectory  => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        "Library", "Application Support", "TunnelAgent");
    public async Task PostInstallAsync(string binaryPath) =>
        await UnixHelper.ChmodExecutableAsync(binaryPath);
}

public sealed class LinuxPlatform(string arch) : IPlatformInfo
{
    public string BinaryName          => "CLIProxyAPI";
    public string ArchiveBinaryName   => "cli-proxy-api";
    public string OsSuffix            => "linux";
    public string ArchSuffix          => arch;
    public string ArchiveExtension    => ".tar.gz";
    public string SettingsDirectory   => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "TunnelAgent");
    public string LocalDataDirectory  => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelAgent");
    public async Task PostInstallAsync(string binaryPath) =>
        await UnixHelper.ChmodExecutableAsync(binaryPath);
}
