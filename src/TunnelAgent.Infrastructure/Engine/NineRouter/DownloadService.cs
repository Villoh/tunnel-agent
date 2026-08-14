using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.Core.Engine;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>Downloads and installs the 9Router npm package into the local engine directory.</summary>
public sealed class DownloadService
{
    internal const string NodeRequiredMessage = "Node.js >= 18 is required to install 9Router.";

    private const string PackageName = "9router";
    private const string RegistryOrigin = "https://registry.npmjs.org";
    private const string PackumentUrl = RegistryOrigin + "/" + PackageName;
    private const string LatestUrl = PackumentUrl + "/latest";

    /// <summary>Gets the default on-disk directory for the 9Router engine under local application data.</summary>
    public static readonly string DefaultEngineDir = Path.Combine(
        IPlatformInfo.Current.LocalDataDirectory,
        "engine",
        EngineCatalog.NineRouter.Id);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(60);
    private static readonly HttpClient SharedHttp = CreateSharedHttpClient();
    private static readonly SemaphoreSlim SharedCacheLock = new(1, 1);
    private static string? SharedCachedPackument;
    private static DateTime SharedCacheExpiry = DateTime.MinValue;

    private readonly HttpClient _http;
    private readonly NodeRuntimeDetector _nodeDetector;
    private readonly bool _useSharedCache;
    private readonly SemaphoreSlim _installLock = new(1, 1);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private string? _cachedPackument;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private int _prepareRequestId;
    private string? _preparedVersion;

    /// <summary>Gets the directory that stores the extracted 9Router package for this instance.</summary>
    public string EngineDir { get; }

    /// <summary>Gets the version recorded from the extracted package or install metadata.</summary>
    public string? InstalledVersion { get; private set; }

    /// <summary>Gets the newest npm version discovered from the registry.</summary>
    public string? LatestVersion { get; private set; }

    /// <summary>Gets the SHA-256 of the installed server entry file, if present.</summary>
    public string? InstalledBinarySha256 { get; private set; }

    /// <summary>Gets the archive digest recorded at install time.</summary>
    public string? InstalledArchiveSha256 { get; private set; }

    /// <summary>Gets the npm tarball file name selected for the latest or prepared version.</summary>
    public string? LatestAssetName { get; private set; }

    /// <summary>Gets the npm integrity string or shasum for the selected tarball.</summary>
    public string? LatestAssetSha256 { get; private set; }

    /// <summary>Gets the last integrity or install-blocking error message, if any.</summary>
    public string? IntegrityError { get; private set; }

    /// <summary>Gets whether <see cref="LatestVersion"/> differs from <see cref="InstalledVersion"/>.</summary>
    public bool UpdateAvailable => InstalledVersion != null && LatestVersion != null &&
        !VersionsEqual(LatestVersion, InstalledVersion);

    /// <summary>Gets the current download progress as a percentage from 0 to 100.</summary>
    public double DownloadProgress { get; private set; }

    /// <summary>Gets the current engine install lifecycle state.</summary>
    public EngineState State { get; private set; } = EngineState.NotInstalled;

    /// <summary>Occurs when install state, progress, or version metadata changes.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Creates a download service that uses the public npm registry and the default engine directory.</summary>
    public DownloadService()
        : this(SharedHttp, new NodeRuntimeDetector(), DefaultEngineDir, useSharedCache: true)
    {
    }

    /// <summary>Creates a download service with an injected HTTP handler, Node detector, and engine directory.</summary>
    internal DownloadService(
        HttpMessageHandler handler,
        NodeRuntimeDetector nodeDetector,
        string engineDir)
        : this(CreateClient(handler), nodeDetector, engineDir, useSharedCache: false)
    {
    }

    /// <summary>Creates a download service with an injected HTTP client, Node detector, and engine directory.</summary>
    internal DownloadService(
        HttpClient httpClient,
        NodeRuntimeDetector nodeDetector,
        string engineDir)
        : this(httpClient, nodeDetector, engineDir, useSharedCache: false)
    {
    }

    private DownloadService(
        HttpClient httpClient,
        NodeRuntimeDetector nodeDetector,
        string engineDir,
        bool useSharedCache)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(nodeDetector);
        ArgumentException.ThrowIfNullOrWhiteSpace(engineDir);

        _http = httpClient;
        _nodeDetector = nodeDetector;
        EngineDir = engineDir;
        _useSharedCache = useSharedCache;
        EnsureUserAgent(_http);
    }

    /// <summary>Clears the shared in-memory packument cache so the next fetch hits the registry.</summary>
    public static void InvalidateCache()
    {
        SharedCachedPackument = null;
        SharedCacheExpiry = DateTime.MinValue;
    }

    /// <summary>Returns whether the default engine directory contains a 9Router server entry file.</summary>
    public static bool IsBinaryInstalled() => FindServerEntry(DefaultEngineDir) is not null;

    /// <summary>Gets whether this instance's engine directory contains a 9Router server entry file.</summary>
    public bool IsInstalled => FindServerEntry(EngineDir) is not null;

    /// <summary>
    /// Gets the extracted standalone server path (<c>app/custom-server.js</c> or <c>app/server.js</c>)
    /// in <see cref="EngineDir"/>, or <see langword="null"/> when the engine is not installed.
    /// </summary>
    public string? ServerEntryPath => FindServerEntry(EngineDir);

    /// <summary>Loads install metadata from disk and sets <see cref="State"/> to stopped or not installed.</summary>
    public async Task InitializeAsync()
    {
        InstalledBinarySha256 = null;
        InstalledArchiveSha256 = null;
        InstalledVersion = null;

        var serverPath = FindServerEntry(EngineDir);
        if (serverPath is null)
        {
            SetState(EngineState.NotInstalled);
            return;
        }

        InstalledBinarySha256 = await ComputeFileSha256Async(serverPath);
        var metadata = await ReadInstallMetadataAsync();
        InstalledVersion = ReadPackageJsonVersion() ?? metadata?.Version;
        InstalledArchiveSha256 = metadata?.ArchiveSha256;
        SetState(EngineState.Stopped);
    }

    /// <summary>Lists published npm versions for 9Router, newest first, skipping deprecated releases.</summary>
    public async Task<IReadOnlyList<EngineReleaseInfo>> ListReleasesAsync(int limit = 30)
    {
        using var document = await FetchPackumentAsync();
        var releases = new List<EngineReleaseInfo>();
        if (!document.RootElement.TryGetProperty("versions", out var versions) ||
            versions.ValueKind != JsonValueKind.Object)
        {
            return releases;
        }

        foreach (var versionProperty in versions.EnumerateObject())
        {
            var versionObject = versionProperty.Value;
            if (versionObject.ValueKind != JsonValueKind.Object)
                continue;
            if (versionObject.TryGetProperty("deprecated", out _))
                continue;

            var tag = versionProperty.Name;
            if (string.IsNullOrWhiteSpace(tag))
                continue;

            var prerelease = tag.Contains('-', StringComparison.Ordinal);
            var publishedAt = ReadPublishedAt(document.RootElement, tag);
            releases.Add(new EngineReleaseInfo(tag, tag, prerelease, publishedAt));
        }

        releases.Sort(CompareNewestFirst);
        if (limit > 0 && releases.Count > limit)
            releases.RemoveRange(limit, releases.Count - limit);

        return releases;
    }

    /// <summary>Resolves tarball metadata for the given npm version without downloading it.</summary>
    public async Task PrepareVersionAsync(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            await CheckForUpdateAsync();
            return;
        }

        var requestId = Interlocked.Increment(ref _prepareRequestId);
        var dist = await FetchVersionDistAsync(version.Trim());
        if (requestId != _prepareRequestId || dist is null)
            return;

        _preparedVersion = dist.Version;
        LatestAssetName = dist.AssetName;
        LatestAssetSha256 = dist.RecordedDigest;
        IntegrityError = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refreshes <see cref="LatestVersion"/> from the npm packument <c>dist-tags.latest</c> field.</summary>
    public async Task CheckForUpdateAsync()
    {
        try
        {
            using var document = await FetchPackumentAsync();
            var tag = ReadLatestTag(document.RootElement);
            if (string.IsNullOrWhiteSpace(tag))
                tag = await FetchLatestVersionFallbackAsync();
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
            Debug.WriteLine($"[NineRouter.DownloadService] CheckForUpdateAsync failed: {ex.Message}");
        }
    }

    /// <summary>Downloads and installs the latest 9Router npm package.</summary>
    public Task DownloadAndInstallAsync() => DownloadAndInstallAsync(null);

    /// <summary>Downloads and installs the specified 9Router npm version, or the latest version when <paramref name="version"/> is null.</summary>
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
        if (_nodeDetector.Detect() is null)
        {
            IntegrityError = NodeRequiredMessage;
            SetState(EngineState.NotInstalled);
            throw new InvalidOperationException(NodeRequiredMessage);
        }

        if (!string.IsNullOrWhiteSpace(targetVersion))
            await PrepareVersionAsync(targetVersion);
        else if (LatestVersion is null)
            await CheckForUpdateAsync();

        var version = string.IsNullOrWhiteSpace(targetVersion) ? LatestVersion : targetVersion.Trim();
        if (version is null)
            throw new InvalidOperationException("Could not determine latest 9Router version. Check your network connection.");

        var prevState = State;
        string? tmpPath = null;
        string? extractDir = null;

        try
        {
            SetState(EngineState.Downloading);
            IntegrityError = null;
            DownloadProgress = 0;

            var dist = await FetchVersionDistAsync(version)
                ?? throw new InvalidOperationException($"No npm tarball found for 9Router {version}.");

            Directory.CreateDirectory(EngineDir);
            tmpPath = Path.Combine(EngineDir, dist.AssetName + ".tmp");

            using (var response = await _http.GetAsync(dist.TarballUrl, HttpCompletionOption.ResponseHeadersRead))
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

            var digest = await VerifyTarballAsync(tmpPath, dist.Integrity, dist.Shasum);

            SetState(EngineState.Installing);
            DownloadProgress = 100;

            extractDir = Path.Combine(EngineDir, "extract_tmp");
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
            Directory.CreateDirectory(extractDir);

            await ExtractNpmTarballAsync(tmpPath, extractDir);
            FlattenPackageDirectory(extractDir, EngineDir);

            var serverPath = FindServerEntry(EngineDir)
                ?? throw new FileNotFoundException("9Router server entry (app/custom-server.js or app/server.js) was not found in the npm package.");

            TryRunPostInstall();

            InstalledVersion = ReadPackageJsonVersion() ?? dist.Version;
            InstalledArchiveSha256 = digest;
            InstalledBinarySha256 = await ComputeFileSha256Async(serverPath);
            LatestVersion = dist.Version;
            LatestAssetName = dist.AssetName;
            LatestAssetSha256 = dist.RecordedDigest;
            await WriteInstallMetadataAsync(new EngineInstallMetadata
            {
                Version = InstalledVersion,
                AssetName = dist.AssetName,
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
            IntegrityError = ex is InvalidDataException ||
                             ex.Message.Contains("SHA", StringComparison.OrdinalIgnoreCase) ||
                             ex.Message.Contains("integrity", StringComparison.OrdinalIgnoreCase)
                ? ex.Message
                : IntegrityError;
            TryDeleteFile(tmpPath);
            TryDeleteDirectory(extractDir);
            SetState(prevState == EngineState.NotInstalled ? EngineState.NotInstalled : EngineState.Error);
            throw;
        }
    }

    private async Task<JsonDocument> FetchPackumentAsync()
    {
        var cacheLock = _useSharedCache ? SharedCacheLock : _cacheLock;
        await cacheLock.WaitAsync();
        try
        {
            var cached = _useSharedCache ? SharedCachedPackument : _cachedPackument;
            var expiry = _useSharedCache ? SharedCacheExpiry : _cacheExpiry;
            if (cached is not null && DateTime.UtcNow < expiry)
                return JsonDocument.Parse(cached);

            using var response = await _http.GetAsync(PackumentUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();

            if (_useSharedCache)
            {
                SharedCachedPackument = json;
                SharedCacheExpiry = DateTime.UtcNow.Add(CacheTtl);
            }
            else
            {
                _cachedPackument = json;
                _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);
            }

            return JsonDocument.Parse(json);
        }
        finally
        {
            cacheLock.Release();
        }
    }

    private async Task<NpmDist?> FetchVersionDistAsync(string version)
    {
        using var document = await FetchPackumentAsync();
        return FindVersionDist(document.RootElement, version);
    }

    private async Task<string?> FetchLatestVersionFallbackAsync()
    {
        try
        {
            using var response = await _http.GetAsync(LatestUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NineRouter.DownloadService] latest fetch failed: {ex.Message}");
            return null;
        }
    }

    private static NpmDist? FindVersionDist(JsonElement root, string version)
    {
        if (!root.TryGetProperty("versions", out var versions) || versions.ValueKind != JsonValueKind.Object)
            return null;

        if (versions.TryGetProperty(version, out var exact))
            return ReadDist(exact, version);

        var trimmed = version.TrimStart('v');
        if (trimmed != version && versions.TryGetProperty(trimmed, out var withoutPrefix))
            return ReadDist(withoutPrefix, trimmed);

        foreach (var property in versions.EnumerateObject())
        {
            if (VersionsEqual(property.Name, version))
                return ReadDist(property.Value, property.Name);
        }

        return null;
    }

    private static NpmDist ReadDist(JsonElement versionObject, string version)
    {
        string? tarball = null;
        string? integrity = null;
        string? shasum = null;
        if (versionObject.TryGetProperty("dist", out var dist) && dist.ValueKind == JsonValueKind.Object)
        {
            tarball = dist.TryGetProperty("tarball", out var tarballElement) ? tarballElement.GetString() : null;
            integrity = dist.TryGetProperty("integrity", out var integrityElement) ? integrityElement.GetString() : null;
            shasum = dist.TryGetProperty("shasum", out var shasumElement) ? shasumElement.GetString() : null;
        }

        if (string.IsNullOrWhiteSpace(tarball))
            tarball = $"{RegistryOrigin}/{PackageName}/-/{PackageName}-{version}.tgz";

        return new NpmDist(version, tarball, integrity, shasum);
    }

    private static string? ReadLatestTag(JsonElement root)
    {
        if (root.TryGetProperty("dist-tags", out var tags) &&
            tags.ValueKind == JsonValueKind.Object &&
            tags.TryGetProperty("latest", out var latest))
        {
            return latest.GetString();
        }

        return null;
    }

    private static DateTimeOffset? ReadPublishedAt(JsonElement root, string version)
    {
        if (!root.TryGetProperty("time", out var time) || time.ValueKind != JsonValueKind.Object)
            return null;
        if (!time.TryGetProperty(version, out var value))
            return null;
        return DateTimeOffset.TryParse(value.GetString(), out var parsed) ? parsed : null;
    }

    private static int CompareNewestFirst(EngineReleaseInfo left, EngineReleaseInfo right)
    {
        var dateCompare = Nullable.Compare(right.PublishedAt, left.PublishedAt);
        if (dateCompare != 0)
            return dateCompare;

        if (Version.TryParse(NormalizeVersion(left.TagName), out var leftVersion) &&
            Version.TryParse(NormalizeVersion(right.TagName), out var rightVersion))
        {
            return rightVersion.CompareTo(leftVersion);
        }

        return string.Compare(right.TagName, left.TagName, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ExtractNpmTarballAsync(string tarballPath, string destinationDirectory)
    {
        await using var file = File.OpenRead(tarballPath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, destinationDirectory, overwriteFiles: true);
    }

    private static void FlattenPackageDirectory(string extractDir, string engineDir)
    {
        var packageDir = Path.Combine(extractDir, "package");
        var sourceDir = Directory.Exists(packageDir) ? packageDir : extractDir;
        CopyDirectoryContents(sourceDir, engineDir);
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var directory in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destDir, Path.GetRelativePath(sourceDir, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            var destFolder = Path.GetDirectoryName(dest);
            if (!string.IsNullOrEmpty(destFolder))
                Directory.CreateDirectory(destFolder);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private void TryRunPostInstall()
    {
        var script = Path.Combine("hooks", "postinstall.js");
        if (!File.Exists(Path.Combine(EngineDir, script)))
            return;

        var runtime = _nodeDetector.Detect();
        if (runtime is null)
            return;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runtime.ExecutablePath,
                WorkingDirectory = EngineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(script);

            using var process = Process.Start(startInfo);
            if (process is null)
                return;

            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                Debug.WriteLine("[NineRouter.DownloadService] postinstall timed out.");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NineRouter.DownloadService] postinstall failed: {ex.Message}");
        }
    }

    private string? ReadPackageJsonVersion()
    {
        var path = Path.Combine(EngineDir, "package.json");
        if (!File.Exists(path))
            return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindServerEntry(string engineDir)
    {
        var custom = Path.Combine(engineDir, "app", "custom-server.js");
        if (File.Exists(custom))
            return custom;

        var server = Path.Combine(engineDir, "app", "server.js");
        return File.Exists(server) ? server : null;
    }

    private static async Task<string> VerifyTarballAsync(string path, string? integrity, string? shasum)
    {
        await using var stream = File.OpenRead(path);
        if (!string.IsNullOrWhiteSpace(integrity))
        {
            var trimmed = integrity.Trim();
            if (trimmed.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase))
            {
                var actual = await SHA512.HashDataAsync(stream);
                AssertMatches("sha512", trimmed["sha512-".Length..], actual);
                return trimmed;
            }

            if (trimmed.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase))
            {
                var actual = await SHA256.HashDataAsync(stream);
                AssertMatches("sha256", trimmed["sha256-".Length..], actual);
                return trimmed;
            }

            if (trimmed.StartsWith("sha1-", StringComparison.OrdinalIgnoreCase))
            {
                var actual = await SHA1.HashDataAsync(stream);
                AssertMatches("sha1", trimmed["sha1-".Length..], actual);
                return trimmed;
            }
        }

        if (!string.IsNullOrWhiteSpace(shasum))
        {
            var actual = await SHA1.HashDataAsync(stream);
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(shasum.Trim());
            }
            catch (FormatException)
            {
                throw new InvalidDataException($"9Router tarball SHA1 shasum is not valid hex: {shasum}.");
            }

            if (expected.Length != actual.Length ||
                !CryptographicOperations.FixedTimeEquals(actual, expected))
            {
                throw new InvalidDataException(
                    $"9Router tarball SHA1 mismatch. Expected {shasum.Trim().ToLowerInvariant()}, got {Convert.ToHexString(actual).ToLowerInvariant()}.");
            }

            return Convert.ToHexString(actual).ToLowerInvariant();
        }

        throw new InvalidDataException("9Router npm packument did not include dist.integrity or dist.shasum.");
    }

    private static void AssertMatches(string algorithm, string expectedBase64, byte[] actual)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedBase64);
        }
        catch (FormatException)
        {
            throw new InvalidDataException($"9Router tarball {algorithm} integrity is not valid base64.");
        }

        if (expected.Length != actual.Length ||
            !CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException(
                $"9Router tarball {algorithm} mismatch. Expected {expectedBase64}, got {Convert.ToBase64String(actual)}.");
        }
    }

    private void SetState(EngineState state)
    {
        State = state;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private string InstallMetadataPath => Path.Combine(EngineDir, "install.json");

    private async Task<EngineInstallMetadata?> ReadInstallMetadataAsync()
    {
        try
        {
            if (!File.Exists(InstallMetadataPath)) return null;
            var json = await File.ReadAllTextAsync(InstallMetadataPath);
            return JsonSerializer.Deserialize<EngineInstallMetadata>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task WriteInstallMetadataAsync(EngineInstallMetadata metadata)
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

    private static async Task<string> ComputeFileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool VersionsEqual(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(NormalizeVersion(left), NormalizeVersion(right), StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string version) => version.Trim().TrimStart('v');

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (path is not null && File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch { /* best-effort */ }
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var http = new HttpClient();
        EnsureUserAgent(http);
        return http;
    }

    private static HttpClient CreateClient(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var http = new HttpClient(handler);
        EnsureUserAgent(http);
        return http;
    }

    private static void EnsureUserAgent(HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count > 0)
            return;

        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TunnelAgent", AppVersion.Current));
    }

    private sealed record NpmDist(string Version, string TarballUrl, string? Integrity, string? Shasum)
    {
        public string AssetName
        {
            get
            {
                var fileName = Path.GetFileName(new Uri(TarballUrl).AbsolutePath);
                return string.IsNullOrWhiteSpace(fileName) ? $"{PackageName}-{Version}.tgz" : fileName;
            }
        }

        public string? RecordedDigest => !string.IsNullOrWhiteSpace(Integrity) ? Integrity : Shasum;
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
