using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.Perplexity;

/// <summary>Downloads and installs Perplexity WebUI Scraper release binaries.</summary>
public sealed class DownloadService
{
    private static readonly IPlatformInfo Platform = IPlatformInfo.Current;

    public static readonly string EngineDir = Path.Combine(Platform.LocalDataDirectory, "engine", EngineCatalog.PerplexityWebUiScraper.Id);
    public static string BinaryPath => Path.Combine(EngineDir, BinaryName);
    private static string InstallMetadataPath => Path.Combine(EngineDir, "install.json");
    private static string BinaryName => System.OperatingSystem.IsWindows() ? "perplexity-webui-scraper.exe" : "perplexity-webui-scraper";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private int _prepareRequestId;
    private string? _preparedVersion;

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

    // In-memory cache — avoids GitHub rate-limiting across repeated calls within a session
    private static string? _cachedReleasesJson;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    static DownloadService()
    {
        var version = AppVersion.Current;
        Http = new HttpClient();
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TunnelAgent", version));
    }

    /// <summary>Invalidate the cache so the next call fetches fresh data.</summary>
    public static void InvalidateCache() { _cachedReleasesJson = null; _cacheExpiry = DateTime.MinValue; }

    public static bool IsBinaryInstalled() => File.Exists(BinaryPath);

    public async Task InitializeAsync()
    {
        InstalledBinarySha256 = null;
        InstalledArchiveSha256 = null;

        if (!IsBinaryInstalled())
        {
            SetState(EngineState.NotInstalled);
            return;
        }

        InstalledBinarySha256 = await ComputeFileSha256Async(BinaryPath);
        var metadata = await ReadInstallMetadataAsync();
        InstalledVersion = metadata?.Version;
        InstalledArchiveSha256 = NormalizeSha256(metadata?.ArchiveSha256);
        SetState(EngineState.Stopped);
    }

    public async Task<IReadOnlyList<EngineReleaseInfo>> ListReleasesAsync(int limit = 30)
    {
        using var document = await FetchReleasesDocumentAsync(limit);
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
        var asset = await FetchReleaseAssetAsync(tag);
        if (requestId != _prepareRequestId || asset is null)
            return;

        _preparedVersion = tag;
        LatestAssetName = asset.Value.Name;
        LatestAssetSha256 = asset.Value.Sha256;
        IntegrityError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CheckForUpdateAsync()
    {
        try
        {
            using var document = await FetchReleasesDocumentAsync(1);
            var release = document.RootElement.EnumerateArray().FirstOrDefault();
            if (release.ValueKind == JsonValueKind.Undefined)
                return;

            var tag = release.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tag))
                return;

            var previousLatest = LatestVersion;
            LatestVersion = tag;
            StateChanged?.Invoke(this, EventArgs.Empty);

            if (_preparedVersion is null || VersionsEqual(_preparedVersion, previousLatest))
                await PrepareVersionAsync(tag);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DownloadService] CheckForUpdateAsync failed: {ex.Message}");
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
            throw new InvalidOperationException("Could not determine latest Perplexity WebUI Scraper version. Check your network connection.");

        var prevState = State;
        string? tmpPath = null;
        string? extractDir = null;

        try
        {
            SetState(EngineState.Downloading);
            IntegrityError = null;
            DownloadProgress = 0;

            var asset = await FetchReleaseAssetAsync(version)
                ?? throw new InvalidOperationException($"No matching release asset found for {version} on this platform.");

            Directory.CreateDirectory(EngineDir);
            tmpPath = Path.Combine(EngineDir, asset.Name + ".tmp");

            using (var response = await Http.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
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
            if (!Sha256Equals(actualSha256, asset.Sha256))
                throw new InvalidDataException($"Perplexity WebUI Scraper archive SHA256 mismatch. Expected {asset.Sha256}, got {actualSha256}.");

            SetState(EngineState.Installing);
            DownloadProgress = 100;

            extractDir = Path.Combine(EngineDir, "extract_tmp");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

            if (asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
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
            InstalledArchiveSha256 = asset.Sha256;
            InstalledBinarySha256 = await ComputeFileSha256Async(BinaryPath);
            LatestVersion = version;
            LatestAssetName = asset.Name;
            LatestAssetSha256 = asset.Sha256;
            await WriteInstallMetadataAsync(new EngineInstallMetadata
            {
                Version = InstalledVersion,
                AssetName = asset.Name,
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

    private static async Task<JsonDocument> FetchReleasesDocumentAsync(int limit)
    {
        await _cacheLock.WaitAsync();
        try
        {
            if (_cachedReleasesJson is not null && DateTime.UtcNow < _cacheExpiry)
                return JsonDocument.Parse(_cachedReleasesJson);

            var url = $"https://api.github.com/repos/{EngineCatalog.PerplexityWebUiScraper.RepositoryOwner}/{EngineCatalog.PerplexityWebUiScraper.RepositoryName}/releases?per_page=30";
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            if ((int)response.StatusCode is 403 or 429)
            {
                Debug.WriteLine($"[Perplexity.DownloadService] GitHub rate limited ({response.StatusCode})");
                return JsonDocument.Parse(_cachedReleasesJson ?? "[]");
            }

            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            _cachedReleasesJson = json;
            _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);
            return JsonDocument.Parse(json);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private static async Task<(string Name, string DownloadUrl, string Sha256)?> FetchReleaseAssetAsync(string version)
    {
        using var document = await FetchReleasesDocumentAsync(30);
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var tag = release.GetProperty("tag_name").GetString();
            if (!VersionsEqual(tag, version))
                continue;

            if (!release.TryGetProperty("assets", out var assets))
                return null;

            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString();
                if (!string.Equals(name, BuildAssetName(version), StringComparison.OrdinalIgnoreCase))
                    continue;

                var digest = asset.TryGetProperty("digest", out var digestElement) ? digestElement.GetString() : null;
                var downloadUrl = asset.GetProperty("browser_download_url").GetString();
                var sha256 = NormalizeSha256(digest?.Replace("sha256:", "", StringComparison.OrdinalIgnoreCase));
                if (name is not null && downloadUrl is not null && sha256 is not null)
                    return (name, downloadUrl, sha256);
            }
        }

        return null;
    }

    private static string BuildAssetName(string version)
    {
        var normalized = version.TrimStart('v');
        if (System.OperatingSystem.IsWindows())
            return $"perplexity-webui-scraper-v{normalized}-windows-amd64.zip";

        if (System.OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
                ? $"perplexity-webui-scraper-v{normalized}-macos-arm64.tar.gz"
                : $"perplexity-webui-scraper-v{normalized}-macos-26-intel.tar.gz";

        var arch = RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64";
        return $"perplexity-webui-scraper-v{normalized}-linux-{arch}.tar.gz";
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
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
        foreach (var file in Directory.EnumerateFiles(dir, BinaryName, SearchOption.AllDirectories))
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
