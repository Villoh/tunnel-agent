using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine.NineRouter;

namespace TunnelAgent.Tests;

public sealed class NineRouterDownloadServiceTests
{
    [Fact]
    public void Constructor_EmptyDirectory_StartsNotInstalled()
    {
        using var temp = new TestTempDirectory();
        using var handler = new FakeNpmHandler();
        var service = CreateService(handler, temp.Path, nodeFound: true);

        Assert.Equal(EngineState.NotInstalled, service.State);
        Assert.Null(service.InstalledVersion);
        Assert.Null(service.LatestVersion);
        Assert.Null(service.InstalledBinarySha256);
        Assert.Null(service.InstalledArchiveSha256);
        Assert.Null(service.LatestAssetName);
        Assert.Null(service.LatestAssetSha256);
        Assert.Null(service.IntegrityError);
        Assert.False(service.UpdateAvailable);
        Assert.False(service.IsInstalled);
        Assert.Equal(0, service.DownloadProgress);
        Assert.Equal(temp.Path, service.EngineDir);
        Assert.Contains(EngineCatalog.NineRouter.Id, DownloadService.DefaultEngineDir, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_EmptyDirectory_SetsNotInstalled()
    {
        using var temp = new TestTempDirectory();
        using var handler = new FakeNpmHandler();
        var service = CreateService(handler, temp.Path, nodeFound: true);
        var raised = false;
        service.StateChanged += (_, _) => raised = true;

        await service.InitializeAsync();

        Assert.Equal(EngineState.NotInstalled, service.State);
        Assert.Null(service.InstalledVersion);
        Assert.False(service.IsInstalled);
        Assert.True(raised);
    }

    [Fact]
    public async Task ListReleasesAsync_PackumentWithDeprecatedVersion_ReturnsNewestFirstSkippingDeprecated()
    {
        using var temp = new TestTempDirectory();
        using var handler = FakeNpmHandler.WithPackumentOnly();
        var service = CreateService(handler, temp.Path, nodeFound: true);

        var releases = await service.ListReleasesAsync();

        Assert.Equal(2, releases.Count);
        Assert.Equal("0.0.2", releases[0].TagName);
        Assert.Equal("0.0.2", releases[0].DisplayName);
        Assert.False(releases[0].IsPrerelease);
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), releases[0].PublishedAt);
        Assert.Equal("0.0.1", releases[1].TagName);
        Assert.DoesNotContain(releases, release => release.TagName.Contains("deprecated", StringComparison.Ordinal));
        Assert.Equal(0, handler.TarballRequestCount);
    }

    [Fact]
    public async Task CheckForUpdateAsync_Packument_SetsLatestVersionFromDistTags()
    {
        using var temp = new TestTempDirectory();
        using var handler = FakeNpmHandler.WithPackumentOnly();
        var service = CreateService(handler, temp.Path, nodeFound: true);

        await service.CheckForUpdateAsync();

        Assert.Equal("0.0.2", service.LatestVersion);
        Assert.Equal("9router-0.0.2.tgz", service.LatestAssetName);
        Assert.False(string.IsNullOrWhiteSpace(service.LatestAssetSha256));
        Assert.False(service.UpdateAvailable);
        Assert.Equal(0, handler.TarballRequestCount);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_ValidTarball_ExtractsFlattenedLayoutAndWritesMetadata()
    {
        using var temp = new TestTempDirectory();
        var tarball = CreateNpmTarball("0.0.1");
        using var handler = FakeNpmHandler.WithTarball("0.0.1", tarball);
        var service = CreateService(handler, temp.Path, nodeFound: true);

        await service.DownloadAndInstallAsync("0.0.1");

        Assert.Equal(EngineState.Stopped, service.State);
        Assert.Equal("0.0.1", service.InstalledVersion);
        Assert.True(service.IsInstalled);
        Assert.True(File.Exists(Path.Combine(temp.Path, "package.json")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "app", "custom-server.js")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "package")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "install.json")));

        using var install = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(temp.Path, "install.json")));
        Assert.Equal("0.0.1", install.RootElement.GetProperty("Version").GetString());
        Assert.Equal("9router-0.0.1.tgz", install.RootElement.GetProperty("AssetName").GetString());
        Assert.False(string.IsNullOrWhiteSpace(install.RootElement.GetProperty("ArchiveSha256").GetString()));
        Assert.Equal(1, handler.TarballRequestCount);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_MissingNode_LeavesNotInstalledAndSetsError()
    {
        using var temp = new TestTempDirectory();
        var tarball = CreateNpmTarball("0.0.1");
        using var handler = FakeNpmHandler.WithTarball("0.0.1", tarball);
        var service = CreateService(handler, temp.Path, nodeFound: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DownloadAndInstallAsync("0.0.1"));

        Assert.Equal(DownloadService.NodeRequiredMessage, ex.Message);
        Assert.Equal(DownloadService.NodeRequiredMessage, service.IntegrityError);
        Assert.Equal(EngineState.NotInstalled, service.State);
        Assert.False(service.IsInstalled);
        Assert.Null(service.InstalledVersion);
        Assert.False(File.Exists(Path.Combine(temp.Path, "package.json")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "app", "custom-server.js")));
        Assert.Equal(0, handler.TarballRequestCount);
    }

    [Fact]
    public async Task DownloadAndInstallAsync_IntegrityMismatch_SetsIntegrityErrorAndDoesNotInstall()
    {
        using var temp = new TestTempDirectory();
        var tarball = CreateNpmTarball("0.0.1");
        using var handler = FakeNpmHandler.WithTarball(
            "0.0.1",
            tarball,
            integrity: "sha512-" + Convert.ToBase64String(new byte[64]));
        var service = CreateService(handler, temp.Path, nodeFound: true);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadAndInstallAsync("0.0.1"));

        Assert.Contains("mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("mismatch", service.IntegrityError, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EngineState.NotInstalled, service.State);
        Assert.False(service.IsInstalled);
        Assert.Null(service.InstalledVersion);
        Assert.False(File.Exists(Path.Combine(temp.Path, "package.json")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "app", "custom-server.js")));
    }

    private static DownloadService CreateService(HttpMessageHandler handler, string engineDir, bool nodeFound)
    {
        var detector = nodeFound
            ? new NodeRuntimeDetector(["fake-node"], _ => "v20.11.1")
            : new NodeRuntimeDetector(["missing-node"], _ => null);

        return new DownloadService(handler, detector, engineDir);
    }

    internal static byte[] CreateNpmTarball(string version)
    {
        using var tarBuffer = new MemoryStream();
        using (var writer = new TarWriter(tarBuffer, TarEntryFormat.Ustar, leaveOpen: true))
        {
            WriteTarFile(writer, "package/package.json", $$"""{"name":"9router","version":"{{version}}"}""");
            WriteTarFile(writer, "package/app/custom-server.js", "module.exports = {};\n");
        }

        tarBuffer.Position = 0;
        using var gzipBuffer = new MemoryStream();
        using (var gzip = new GZipStream(gzipBuffer, CompressionLevel.Fastest, leaveOpen: true))
            tarBuffer.CopyTo(gzip);

        return gzipBuffer.ToArray();
    }

    private static void WriteTarFile(TarWriter writer, string name, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var stream = new MemoryStream(bytes);
        var entry = new UstarTarEntry(TarEntryType.RegularFile, name)
        {
            DataStream = stream
        };
        writer.WriteEntry(entry);
    }

    private sealed class FakeNpmHandler : HttpMessageHandler
    {
        private readonly string _packumentJson;
        private readonly Dictionary<string, byte[]> _tarballs = new(StringComparer.OrdinalIgnoreCase);

        public int TarballRequestCount { get; private set; }

        public FakeNpmHandler(string packumentJson)
        {
            _packumentJson = packumentJson;
        }

        public FakeNpmHandler()
            : this(BuildPackument("0.0.2", Array.Empty<(string Version, byte[] Tarball, string? Integrity)>()))
        {
        }

        public static FakeNpmHandler WithPackumentOnly()
        {
            var dummy = CreateNpmTarball("0.0.1");
            var dummy2 = CreateNpmTarball("0.0.2");
            return new FakeNpmHandler(BuildPackument(
                "0.0.2",
                [
                    ("0.0.1", dummy, IntegrityFor(dummy)),
                    ("0.0.2", dummy2, IntegrityFor(dummy2)),
                    ("0.0.0-deprecated", dummy, IntegrityFor(dummy))
                ],
                deprecated: "0.0.0-deprecated"));
        }

        public static FakeNpmHandler WithTarball(string version, byte[] tarball, string? integrity = null)
        {
            var handler = new FakeNpmHandler(BuildPackument(
                version,
                [(version, tarball, integrity ?? IntegrityFor(tarball))]));
            handler._tarballs[$"9router-{version}.tgz"] = tarball;
            return handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI is required.");
            if (!string.Equals(uri.Host, "registry.npmjs.org", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(NotFound());

            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.Equals("/9router", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(Json(_packumentJson));

            if (path.Equals("/9router/latest", StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(NotFound());

            if (path.StartsWith("/9router/-/", StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(path);
                if (_tarballs.TryGetValue(name, out var bytes))
                {
                    TarballRequestCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(bytes)
                    });
                }
            }

            return Task.FromResult(NotFound());
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);

        private static string IntegrityFor(byte[] tarball) =>
            "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball));

        private static string BuildPackument(
            string latest,
            (string Version, byte[] Tarball, string? Integrity)[] versions,
            string? deprecated = null)
        {
            var times = new Dictionary<string, string>
            {
                ["created"] = "2026-01-01T00:00:00.000Z",
                ["modified"] = "2026-03-01T00:00:00.000Z"
            };
            var versionObjects = new Dictionary<string, object>();
            foreach (var (version, tarball, integrity) in versions)
            {
                times[version] = version switch
                {
                    "0.0.2" => "2026-03-01T00:00:00.000Z",
                    "0.0.1" => "2026-02-01T00:00:00.000Z",
                    _ => "2025-01-01T00:00:00.000Z"
                };

                var dist = new Dictionary<string, string?>
                {
                    ["tarball"] = $"https://registry.npmjs.org/9router/-/9router-{version}.tgz",
                    ["integrity"] = integrity,
                    ["shasum"] = Convert.ToHexString(SHA1.HashData(tarball)).ToLowerInvariant()
                };
                var versionObject = new Dictionary<string, object?>
                {
                    ["version"] = version,
                    ["dist"] = dist
                };
                if (deprecated is not null && version == deprecated)
                    versionObject["deprecated"] = "this version is deprecated";

                versionObjects[version] = versionObject;
            }

            if (deprecated is not null && !versionObjects.ContainsKey(deprecated))
            {
                times[deprecated] = "2025-01-01T00:00:00.000Z";
                versionObjects[deprecated] = new Dictionary<string, object?>
                {
                    ["version"] = deprecated,
                    ["deprecated"] = "this version is deprecated",
                    ["dist"] = new Dictionary<string, string>
                    {
                        ["tarball"] = $"https://registry.npmjs.org/9router/-/9router-{deprecated}.tgz",
                        ["shasum"] = new string('0', 40)
                    }
                };
            }

            var packument = new Dictionary<string, object>
            {
                ["name"] = "9router",
                ["dist-tags"] = new Dictionary<string, string> { ["latest"] = latest },
                ["versions"] = versionObjects,
                ["time"] = times
            };

            return JsonSerializer.Serialize(packument);
        }
    }
}
