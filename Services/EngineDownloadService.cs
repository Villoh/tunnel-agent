using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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

    private static string InstallMetadataPath => Path.Combine(EngineDir, "install.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _installLock = new(1, 1);
    private int _prepareRequestId;
    private string? _preparedVersion;
    private string? _latestAssetSha256Version;

    public string? InstalledVersion { get; private set; }
    public string? LatestVersion { get; private set; }
    public string? InstalledBinarySha256 { get; private set; }
    public string? InstalledArchiveSha256 { get; private set; }
    public string? LatestAssetName { get; private set; }
    public string? LatestAssetSha256 { get; private set; }
    public string? IntegrityError { get; private set; }
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
        InstalledBinarySha256 = null;
        InstalledArchiveSha256 = null;

        if (!IsBinaryInstalled())
        {
            SetState(EngineState.NotInstalled);
            return;
        }

        InstalledVersion = await ReadInstalledVersionAsync();
        InstalledBinarySha256 = await ComputeFileSha256Async(BinaryPath);

        var metadata = await ReadInstallMetadataAsync();
        if (VersionsEqual(metadata?.Version, InstalledVersion) &&
            string.Equals(NormalizeSha256(metadata?.BinarySha256), InstalledBinarySha256, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(metadata?.AssetName, BuildAssetName(InstalledVersion ?? ""), StringComparison.OrdinalIgnoreCase))
        {
            InstalledArchiveSha256 = NormalizeSha256(metadata?.ArchiveSha256);
        }

        SetState(EngineState.Stopped);
    }

    public async Task<IReadOnlyList<EngineReleaseInfo>> ListReleasesAsync(int limit = 30)
    {
        var url = $"https://api.github.com/repos/router-for-me/CLIProxyAPI/releases?per_page={Math.Clamp(limit, 1, 100)}";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);

        var releases = new List<EngineReleaseInfo>();
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
                continue;

            var tag = release.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var name = release.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var prerelease = release.TryGetProperty("prerelease", out var preElement) && preElement.GetBoolean();
            DateTimeOffset? publishedAt = null;
            if (release.TryGetProperty("published_at", out var publishedElement) &&
                DateTimeOffset.TryParse(publishedElement.GetString(), out var parsedDate))
            {
                publishedAt = parsedDate;
            }

            releases.Add(new EngineReleaseInfo(tag, string.IsNullOrWhiteSpace(name) ? tag : name, prerelease, publishedAt));
        }

        return releases;
    }

    public async Task PrepareVersionAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            await CheckForUpdateAsync();
            return;
        }

        var requestId = Interlocked.Increment(ref _prepareRequestId);
        var tag = version.Trim();
        var assetName = BuildAssetName(tag);
        var sha256 = await FetchAssetSha256Async(tag, assetName);

        if (requestId != _prepareRequestId) return;

        _preparedVersion = tag;
        LatestAssetName = assetName;
        LatestAssetSha256 = sha256;
        _latestAssetSha256Version = sha256 is null ? null : tag;
        IntegrityError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
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

            var previousLatest = LatestVersion;
            LatestVersion = tag;
            StateChanged?.Invoke(this, EventArgs.Empty);

            if (_preparedVersion is null || VersionsEqual(_preparedVersion, previousLatest))
                await PrepareVersionAsync(tag);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EngineDownloadService] CheckForUpdateAsync failed: {ex.Message}");
        }
    }

    public Task DownloadAndInstallAsync() => DownloadAndInstallAsync(null);

    public async Task DownloadAndInstallAsync(string? version)
    {
        await _installLock.WaitAsync();
        try
        {
            await DownloadAndInstallCoreAsync(version);
        }
        finally
        {
            _installLock.Release();
        }
    }

    private async Task DownloadAndInstallCoreAsync(string? targetVersion)
    {
        if (!string.IsNullOrWhiteSpace(targetVersion))
            await PrepareVersionAsync(targetVersion);
        else if (LatestVersion is null)
            await CheckForUpdateAsync();

        var version = string.IsNullOrWhiteSpace(targetVersion) ? LatestVersion : targetVersion.Trim();
        if (version is null)
            throw new InvalidOperationException("Could not determine latest CLIProxyAPI version. Check your network connection.");

        var prevState = State;
        string? tmpPath = null;
        string? extractDir = null;

        try
        {
            SetState(EngineState.Downloading);
            IntegrityError = null;
            DownloadProgress = 0;

            var assetName = BuildAssetName(version);
            var expectedSha256 = VersionsEqual(_preparedVersion, version) &&
                                 VersionsEqual(_latestAssetSha256Version, version) &&
                                 string.Equals(LatestAssetName, assetName, StringComparison.OrdinalIgnoreCase)
                ? LatestAssetSha256
                : null;

            if (expectedSha256 is null)
            {
                expectedSha256 = await FetchAssetSha256Async(version, assetName);
                _preparedVersion = version;
                LatestAssetName = assetName;
                LatestAssetSha256 = expectedSha256;
                _latestAssetSha256Version = expectedSha256 is null ? null : version;
            }

            if (expectedSha256 is null)
                throw new InvalidOperationException($"No SHA256 checksum found for {assetName} in checksums.txt.");

            var url = BuildDownloadUrl(version, assetName);

            Directory.CreateDirectory(EngineDir);
            tmpPath = Path.Combine(EngineDir, assetName + ".tmp");

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

            var actualSha256 = await ComputeFileSha256Async(tmpPath);
            if (!Sha256Equals(actualSha256, expectedSha256))
                throw new InvalidDataException($"CLIProxyAPI archive SHA256 mismatch. Expected {expectedSha256}, got {actualSha256}.");

            SetState(EngineState.Installing);
            DownloadProgress = 100;

            extractDir = Path.Combine(EngineDir, "extract_tmp");
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

            InstalledVersion = version;
            InstalledArchiveSha256 = expectedSha256;
            InstalledBinarySha256 = await ComputeFileSha256Async(BinaryPath);
            await WriteInstallMetadataAsync(new EngineInstallMetadata
            {
                Version = InstalledVersion,
                AssetName = assetName,
                ArchiveSha256 = InstalledArchiveSha256,
                BinarySha256 = InstalledBinarySha256,
                InstalledAtUtc = DateTimeOffset.UtcNow
            });

            File.Delete(tmpPath);
            Directory.Delete(extractDir, true);

            SetState(EngineState.Stopped);
        }
        catch (Exception ex)
        {
            IntegrityError = ex is InvalidDataException || ex.Message.Contains("SHA256", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : null;
            TryDeleteFile(tmpPath);
            TryDeleteDirectory(extractDir);
            SetState(prevState == EngineState.NotInstalled ? EngineState.NotInstalled : EngineState.Error);
            throw;
        }
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildAssetName(string version)
    {
        var ver = version.TrimStart('v');
        return $"CLIProxyAPI_{ver}_{Platform.PlatformSuffix}{Platform.ArchiveExtension}";
    }

    private static string BuildDownloadUrl(string version, string assetName) =>
        $"https://github.com/router-for-me/CLIProxyAPI/releases/download/{version}/{assetName}";

    private static async Task<string?> FetchAssetSha256Async(string version, string assetName)
    {
        var url = $"https://github.com/router-for-me/CLIProxyAPI/releases/download/{version}/checksums.txt";
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) return null;

        var checksums = await response.Content.ReadAsStringAsync();
        foreach (var line in checksums.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var hash = NormalizeSha256(parts[0]);
            var name = parts[1].TrimStart('*');
            if (hash is not null && string.Equals(name, assetName, StringComparison.OrdinalIgnoreCase))
                return hash;
        }

        return null;
    }

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool Sha256Equals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));

    private static string? NormalizeSha256(string? value)
    {
        if (value is null) return null;
        var hash = value.Trim().ToLowerInvariant();
        if (hash.Length != 64) return null;
        return hash.All(Uri.IsHexDigit) ? hash : null;
    }

    private static bool VersionsEqual(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(left.TrimStart('v'), right.TrimStart('v'), StringComparison.OrdinalIgnoreCase);

    private static async Task<EngineInstallMetadata?> ReadInstallMetadataAsync()
    {
        try
        {
            if (!File.Exists(InstallMetadataPath)) return null;
            var json = await File.ReadAllTextAsync(InstallMetadataPath);
            return JsonSerializer.Deserialize<EngineInstallMetadata>(json, JsonOptions);
        }
        catch { return null; }
    }

    private static async Task WriteInstallMetadataAsync(EngineInstallMetadata metadata)
    {
        Directory.CreateDirectory(EngineDir);
        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        var tmpPath = Path.Combine(EngineDir, $"install.{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(tmpPath, json);
        if (File.Exists(InstallMetadataPath))
            File.Replace(tmpPath, InstallMetadataPath, null);
        else
            File.Move(tmpPath, InstallMetadataPath);
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { }
    }

    private static string? FindBinary(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, Platform.BinaryName, SearchOption.AllDirectories))
            return file;
        return null;
    }

    private sealed class EngineInstallMetadata
    {
        public string? Version { get; set; }
        public string? AssetName { get; set; }
        public string? ArchiveSha256 { get; set; }
        public string? BinarySha256 { get; set; }
        public DateTimeOffset InstalledAtUtc { get; set; }
    }
}
