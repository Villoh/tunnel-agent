using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Responsible for detecting, downloading, and installing the CLIProxyAPI binary.
/// Does not know how to run or configure the binary.
/// </summary>
public sealed class EngineDownloadService
{
    private static readonly IPlatformInfo Platform = IPlatformInfo.Current;

    public static readonly string EngineDir = Path.Combine(Platform.LocalDataDirectory, "engine");
    public static string BinaryPath => Path.Combine(EngineDir, Platform.BinaryName);

    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; private set; }
    public bool UpdateAvailable => InstalledVersion != null && LatestVersion != null &&
        LatestVersion.TrimStart('v') != InstalledVersion.TrimStart('v');
    public double DownloadProgress { get; private set; }
    public EngineState State { get; private set; } = EngineState.NotInstalled;

    public event EventHandler? StateChanged;

    private static readonly HttpClient Http;
    private static readonly HttpClient HttpNoRedirect;

    static EngineDownloadService()
    {
        var version = AppVersion.Current;
        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TunnelAgent", version));

        HttpNoRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        HttpNoRedirect.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TunnelAgent", version));
    }

    public static bool IsBinaryInstalled() => File.Exists(BinaryPath);

    public static async Task<string?> ReadInstalledVersionAsync()
    {
        if (!IsBinaryInstalled()) return null;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(BinaryPath)
                {
                    ArgumentList = { "-version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            var output = stdout.Length > 0 ? stdout : stderr;

            foreach (var part in output.Split([' ', ',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = part.TrimEnd(',');
                if (candidate.StartsWith('v') && candidate.Length > 1 && char.IsDigit(candidate[1]))
                    return candidate;
                if (candidate.Contains('.') && char.IsDigit(candidate[0]))
                    return $"v{candidate}";
            }
            return null;
        }
        catch { return null; }
    }

    public async Task InitializeAsync()
    {
        if (!IsBinaryInstalled())
        {
            SetState(EngineState.NotInstalled);
            return;
        }

        InstalledVersion = await ReadInstalledVersionAsync();
        SetState(EngineState.Stopped);
    }

    public async Task CheckForUpdateAsync()
    {
        try
        {
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
            Debug.WriteLine($"[EngineDownloadService] CheckForUpdateAsync failed: {ex.Message}");
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
            SetState(EngineState.Downloading);
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

            SetState(EngineState.Installing);
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
            SetState(EngineState.Stopped);
        }
        catch
        {
            SetState(prevState == EngineState.NotInstalled ? EngineState.NotInstalled : EngineState.Error);
            throw;
        }
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string? FindBinary(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, Platform.BinaryName, SearchOption.AllDirectories))
            return file;
        return null;
    }
}
