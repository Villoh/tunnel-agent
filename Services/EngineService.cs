using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

public sealed partial class EngineService : IProxyServer
{
    private readonly SettingsService _settings;
    private static readonly IPlatformInfo Platform = IPlatformInfo.Current;

    private static readonly string EngineDir = Path.Combine(Platform.LocalDataDirectory, "engine");

    public static string BinaryPath => Path.Combine(EngineDir, Platform.BinaryName);

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

    private static readonly HttpClient Http;
    private static readonly HttpClient HttpNoRedirect;

    static EngineService()
    {
        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TunnelAgent", "0.0.1"));

        // Separate no-redirect client for version detection via Location header
        HttpNoRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        HttpNoRedirect.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TunnelAgent", "0.0.1"));
    }

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

    private Process? _process;

    public async Task InitializeAsync()
    {
        if (!IsBinaryInstalled())
        {
            State = EngineState.NotInstalled;
            await CheckForUpdateAsync();
            try
            {
                await DownloadAndInstallAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineService] InitializeAsync download failed: {ex.Message}");
                LastError = ex.Message;
                State = EngineState.Error;
                return;
            }
        }
        else
        {
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
            _ = CheckForUpdateAsync();
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

    public async Task CheckForUpdateAsync()
    {
        try
        {
            // Use the HTML redirect instead of the API to avoid the 60 req/h rate limit.
            // GET /releases/latest returns 302 to /releases/tag/vX.Y.Z — read Location header.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://github.com/router-for-me/CLIProxyAPI/releases/latest");
            using var response = await HttpNoRedirect.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            var location = response.Headers.Location?.ToString()
                ?? response.RequestMessage?.RequestUri?.ToString();

            if (location is null) return;

            var tag = location.Split('/').LastOrDefault(p => p.StartsWith('v'));
            if (tag is null) return;

            LatestVersion = tag;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EngineService] CheckForUpdateAsync failed: {ex.Message}");
        }
    }

    public async Task DownloadAndInstallAsync()
    {
        if (LatestVersion is null)
            await CheckForUpdateAsync();

        if (LatestVersion is null)
            throw new InvalidOperationException("Could not determine latest CLIProxyAPI version. Check your network connection.");

        var prevState = State;
        try
        {
            State = EngineState.Downloading;
            DownloadProgress = 0;

            var ver = LatestVersion.TrimStart('v');
            var assetName = $"CLIProxyAPI_{ver}_{Platform.PlatformSuffix}{Platform.ArchiveExtension}";
            var url = $"https://github.com/router-for-me/CLIProxyAPI/releases/download/{LatestVersion}/{assetName}";

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

            State = EngineState.Installing;
            DownloadProgress = 100;

            var extractDir = Path.Combine(EngineDir, "extract_tmp");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

            if (Platform.ArchiveExtension == ".zip")
                ZipFile.ExtractToDirectory(tmpPath, extractDir);
            else
                await UnixHelper.ExtractTarGzAsync(tmpPath, extractDir);

            var extracted = FindBinary(extractDir);
            if (extracted is null)
                throw new FileNotFoundException("Binary not found in archive.");

            if (File.Exists(BinaryPath)) File.Delete(BinaryPath);
            File.Move(extracted, BinaryPath);

            await Platform.PostInstallAsync(BinaryPath);

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
        foreach (var file in Directory.EnumerateFiles(dir, Platform.ArchiveBinaryName, SearchOption.AllDirectories))
            return file;
        return null;
    }
}
