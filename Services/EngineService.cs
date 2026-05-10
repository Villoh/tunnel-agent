// Services/EngineService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

public sealed partial class EngineService : IProxyServer
{
    private readonly SettingsService _settings;

    private static readonly string EngineDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TunnelAgent", "engine");

    private static readonly string BinaryName =
        OperatingSystem.IsWindows() ? "CLIProxyAPI.exe" : "CLIProxyAPI";

    public static string BinaryPath => Path.Combine(EngineDir, BinaryName);

    private EngineState _state = EngineState.NotInstalled;
    public EngineState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; private set; }
    public bool UpdateAvailable => InstalledVersion != null && LatestVersion != null && LatestVersion != InstalledVersion;
    public double DownloadProgress { get; private set; }

    // IProxyServer
    public bool IsRunning => State == EngineState.Running;
    public int Port { get; private set; }
    public event EventHandler? StateChanged;

    public EngineService(SettingsService settings) => _settings = settings;

    public static bool IsBinaryInstalled() => File.Exists(BinaryPath);

    public static async Task<string?> ReadInstalledVersionAsync()
    {
        if (!IsBinaryInstalled()) return null;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(BinaryPath, "--version")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            // Output is typically "CLIProxyAPI version v7.0.2" or just "v7.0.2"
            var parts = output.Trim().Split(' ');
            foreach (var part in parts)
                if (part.StartsWith('v') && part.Length > 1)
                    return part;
            return output.Trim().Length > 0 ? output.Trim() : null;
        }
        catch { return null; }
    }

    // Resolve platform asset name suffix: e.g. "windows_amd64", "darwin_aarch64"
    internal static string GetPlatformSuffix()
    {
        string os = OperatingSystem.IsWindows() ? "windows"
                  : OperatingSystem.IsMacOS()   ? "darwin"
                  : "linux";

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            _                  => "amd64"
        };

        return $"{os}_{arch}";
    }

    // Resolve asset file extension: zip on Windows, tar.gz elsewhere
    internal static string GetArchiveExtension() =>
        OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";

    // Stubs — full implementation in Task 5
    public Task StartAsync(int port, string bindAddress, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task StopAsync(CancellationToken ct = default) =>
        throw new NotImplementedException();
}
