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
    /// <summary>Binary filename, both inside the archive and on disk. e.g. cli-proxy-api / cli-proxy-api.exe</summary>
    string BinaryName { get; }

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
    /// Linux:   $XDG_CONFIG_HOME/TunnelAgent  (defaults to ~/.config/TunnelAgent)
    /// </summary>
    string SettingsDirectory { get; }

    /// <summary>
    /// Directory for machine-local data such as downloaded binaries.
    /// Windows: %LocalAppData%\TunnelAgent
    /// macOS:   ~/Library/Application Support/TunnelAgent
    /// Linux:   ~/.local/share/TunnelAgent
    /// </summary>
    string LocalDataDirectory { get; }

    /// <summary>
    /// Absolute path to the CLIProxyAPI OAuth credentials directory.
    /// This is the default auth-dir the binary uses; always absolute (no ~).
    /// Windows: %UserProfile%\.cli-proxy-api
    /// macOS:   ~/Library/Application Support/.cli-proxy-api  (inside home)
    /// Linux:   ~/.cli-proxy-api
    /// </summary>
    string AuthDirectory { get; }

    /// <summary>
    /// Directory for Perplexity WebUI session account files.
    /// Each account is stored as a separate JSON file: {id}.json
    /// </summary>
    string PerplexityAccountsDirectory => Path.Combine(SettingsDirectory, "perplexity-accounts");

    /// <summary>
    /// Asset name suffix used by the Perplexity WebUI Scraper release assets,
    /// e.g. "windows-amd64", "macos-arm64", "linux-amd64".
    /// Perplexity uses different OS/arch labels than CLIProxyAPI.
    /// </summary>
    string PerplexityAssetSuffix { get; }

    /// <summary>Binary filename for the Perplexity WebUI Scraper engine.</summary>
    string PerplexityBinaryName { get; }

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
    public string BinaryName            => "cli-proxy-api.exe";
    public string OsSuffix              => "windows";
    public string ArchSuffix            => arch;
    public string ArchiveExtension      => ".zip";
    public string PerplexityBinaryName  => "perplexity-webui-scraper.exe";
    // Perplexity only ships amd64 for Windows.
    public string PerplexityAssetSuffix => "windows-amd64";
    public string SettingsDirectory     => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TunnelAgent");
    public string LocalDataDirectory    => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelAgent");
    public string AuthDirectory         => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cli-proxy-api");
    public Task PostInstallAsync(string binaryPath) => Task.CompletedTask;
}

public sealed class MacOsPlatform(string arch) : IPlatformInfo
{
    public string BinaryName            => "cli-proxy-api";
    public string OsSuffix              => "darwin";
    public string ArchSuffix            => arch;
    public string ArchiveExtension      => ".tar.gz";
    public string PerplexityBinaryName  => "perplexity-webui-scraper";
    // Perplexity uses "macos" (not "darwin") and "arm64" / "26-intel" as arch labels.
    public string PerplexityAssetSuffix => arch == "aarch64" ? "macos-arm64" : "macos-26-intel";
    public string SettingsDirectory     => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        "Library", "Preferences", "TunnelAgent");
    public string LocalDataDirectory    => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        "Library", "Application Support", "TunnelAgent");
    public string AuthDirectory         => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Personal),
        ".cli-proxy-api");
    public async Task PostInstallAsync(string binaryPath) =>
        await UnixHelper.ChmodExecutableAsync(binaryPath);
}

public sealed class LinuxPlatform(string arch) : IPlatformInfo
{
    public string BinaryName            => "cli-proxy-api";
    public string OsSuffix              => "linux";
    public string ArchSuffix            => arch;
    public string ArchiveExtension      => ".tar.gz";
    public string PerplexityBinaryName  => "perplexity-webui-scraper";
    // Perplexity uses "arm64" on Linux (not "aarch64" like CLIProxy).
    public string PerplexityAssetSuffix => arch == "aarch64" ? "linux-arm64" : "linux-amd64";
    // Use SpecialFolder.ApplicationData: on Linux .NET resolves it as
    // $XDG_CONFIG_HOME when set, otherwise ~/.config — honouring the XDG spec.
    public string SettingsDirectory     => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TunnelAgent");
    public string LocalDataDirectory    => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelAgent");
    public string AuthDirectory         => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cli-proxy-api");
    public async Task PostInstallAsync(string binaryPath) =>
        await UnixHelper.ChmodExecutableAsync(binaryPath);
}
