// Services/EngineService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

public sealed partial class EngineService : IProxyServer
{
    private readonly SettingsService _settings;

    private static readonly string EngineDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TunnelAgent", "engine");

    // Name used as the destination binary on disk
    private static readonly string BinaryName =
        OperatingSystem.IsWindows() ? "CLIProxyAPI.exe" : "CLIProxyAPI";

    // Name of the binary inside the release archive (cli-proxy-api or cli-proxy-api.exe)
    private static readonly string ArchiveBinaryName =
        OperatingSystem.IsWindows() ? "cli-proxy-api.exe" : "cli-proxy-api";

    public static string BinaryPath => Path.Combine(EngineDir, BinaryName);

    private EngineState _state = EngineState.NotInstalled;
    public EngineState State
    {
        get => _state;
        private set { _state = value; StateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; private set; }
    public string? LastError { get; private set; }
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

    private Process? _process;

    public async Task InitializeAsync()
    {
        if (!IsBinaryInstalled())
        {
            State = EngineState.NotInstalled;
            // Fetch latest version info first so we know what to download
            await CheckForUpdateAsync();
            try
            {
                await DownloadAndInstallAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[EngineService] InitializeAsync download failed: {ex.Message}");
                LastError = ex.Message;
                State = EngineState.Error;
                return;
            }
        }
        else
        {
            // Use cached version if available, otherwise run --version
            InstalledVersion = _settings.Current.InstalledEngineVersion
                ?? await ReadInstalledVersionAsync();

            if (InstalledVersion != null)
            {
                _settings.Current.InstalledEngineVersion = InstalledVersion;
                _settings.Save();
            }

            State = EngineState.Stopped;
        }

        if (_settings.Current.AutoCheckForUpdates)
            _ = CheckForUpdateAsync(); // fire-and-forget, non-blocking
    }

    public async Task StartAsync(int port, string bindAddress, CancellationToken ct = default)
    {
        if (!IsBinaryInstalled()) return;

        Port = port;
        State = EngineState.Starting;

        if (_process is not null)
        {
            _process.Dispose();
            _process = null;
        }

        _process = new Process
        {
            StartInfo = new ProcessStartInfo(BinaryPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = false,
                RedirectStandardError = false
            },
            EnableRaisingEvents = true
        };

        _process.StartInfo.ArgumentList.Add("--port");
        _process.StartInfo.ArgumentList.Add(port.ToString());
        _process.StartInfo.ArgumentList.Add("--bind");
        _process.StartInfo.ArgumentList.Add(bindAddress);

        _process.Exited += (_, _) =>
        {
            if (State == EngineState.Running)
                State = EngineState.Error;
        };

        _process.Start();
        // Brief delay to let the process bind its port before callers use it
        await Task.Delay(600, ct);
        State = EngineState.Running;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.Dispose();
                _process = null;
            }
        }
        catch { }

        State = EngineState.Stopped;
        return Task.CompletedTask;
    }

    private static readonly HttpClient Http;
    private static readonly HttpClient HttpNoRedirect;

    static EngineService()
    {
        // AllowAutoRedirect=true (default) but we need to read the redirect Location
        // for version detection, so we use a separate no-redirect client for that.
        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TunnelAgent", "0.0.1"));

        HttpNoRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        HttpNoRedirect.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TunnelAgent", "0.0.1"));
    }

    public async Task CheckForUpdateAsync()
    {
        try
        {
            // Use the HTML redirect instead of the API to avoid the 60 req/h rate limit.
            // GET /releases/latest returns a 302 to /releases/tag/vX.Y.Z — we read the
            // Location header without following the redirect to extract the version tag.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://github.com/router-for-me/CLIProxyAPI/releases/latest");
            using var response = await HttpNoRedirect.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            var location = response.Headers.Location?.ToString()
                ?? response.RequestMessage?.RequestUri?.ToString();

            if (location is null)
                return;

            // Location is https://github.com/.../releases/tag/v7.0.2
            var tag = location.Split('/').LastOrDefault(p => p.StartsWith('v'));
            if (tag is null)
                return;

            LatestVersion = tag;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EngineService] CheckForUpdateAsync failed: {ex.Message}");
        }
    }

    public async Task DownloadAndInstallAsync()
    {
        // If we don't know the latest version yet, fetch it now (may happen if the
        // background check failed or hasn't run yet).
        if (LatestVersion is null)
            await CheckForUpdateAsync();

        if (LatestVersion is null)
            throw new InvalidOperationException("Could not determine latest CLIProxyAPI version. Check your network connection.");

        var prevState = State;
        try
        {
            State = EngineState.Downloading;
            DownloadProgress = 0;

            // Build asset URL
            var ver = LatestVersion.TrimStart('v');
            var suffix = GetPlatformSuffix();
            var ext = GetArchiveExtension();
            var assetName = $"CLIProxyAPI_{ver}_{suffix}{ext}";
            var url = $"https://github.com/router-for-me/CLIProxyAPI/releases/download/{LatestVersion}/{assetName}";

            // Download with progress
            Directory.CreateDirectory(EngineDir);
            var tmpPath = Path.Combine(EngineDir, assetName + ".tmp");

            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? -1L;
                await using var stream = await response.Content.ReadAsStreamAsync();
                await using var file = File.Create(tmpPath);

                var buffer = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read));
                    downloaded += read;
                    if (total > 0)
                    {
                        DownloadProgress = downloaded * 100.0 / total;
                        StateChanged?.Invoke(this, EventArgs.Empty);
                    }
                }
            }

            // Extract
            State = EngineState.Installing;
            DownloadProgress = 100;

            var extractDir = Path.Combine(EngineDir, "extract_tmp");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

            if (OperatingSystem.IsWindows())
                ZipFile.ExtractToDirectory(tmpPath, extractDir);
            else
                await ExtractTarGzAsync(tmpPath, extractDir);

            // Find and move binary
            var extracted = FindBinary(extractDir);
            if (extracted is null)
                throw new FileNotFoundException("Binary not found in archive.");

            if (File.Exists(BinaryPath)) File.Delete(BinaryPath);
            File.Move(extracted, BinaryPath);

            if (!OperatingSystem.IsWindows())
                await MakeExecutableAsync(BinaryPath);

            // Cleanup
            File.Delete(tmpPath);
            Directory.Delete(extractDir, true);

            InstalledVersion = LatestVersion;
            _settings.Current.InstalledEngineVersion = InstalledVersion;
            _settings.Save();

            State = EngineState.Stopped;
        }
        catch
        {
            State = prevState == EngineState.NotInstalled ? EngineState.NotInstalled : EngineState.Error;
            throw;
        }
    }

    private static string? FindBinary(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, ArchiveBinaryName, SearchOption.AllDirectories))
            return file;
        return null;
    }

    private static async Task ExtractTarGzAsync(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        // Use tar command (available on macOS and modern Linux)
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("tar")
            {
                ArgumentList = { "-xzf", archivePath, "-C", destDir },
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new Exception($"tar exited with code {proc.ExitCode}");
    }

    private static async Task MakeExecutableAsync(string path)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("chmod")
            {
                ArgumentList = { "+x", path },
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new Exception($"chmod exited with code {proc.ExitCode}");
    }
}
