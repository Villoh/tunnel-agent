using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Core.Engine;

/// <summary>Shared contract for managed local engines.</summary>
public interface IManagedEngine
{
    EngineDefinition Definition { get; }
    EngineState State { get; }
    string? InstalledVersion { get; }
    string? LatestVersion { get; }
    string? InstalledBinarySha256 { get; }
    string? InstalledArchiveSha256 { get; }
    string? LatestAssetName { get; }
    string? LatestAssetSha256 { get; }
    string? IntegrityError { get; }
    bool UpdateAvailable { get; }
    double DownloadProgress { get; }
    bool IsRunning { get; }
    int Port { get; }
    string? LastError { get; }
    EngineErrorKind LastErrorKind { get; }

    event EventHandler? StateChanged;

    Task InitializeAsync();
    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
    Task WriteConfigAsync();
    Task CheckForUpdateAsync();
    Task<IReadOnlyList<EngineReleaseInfo>> ListReleasesAsync(int limit = 30);
    Task PrepareVersionAsync(string version);
    Task DownloadAndInstallAsync();
    Task DownloadAndInstallAsync(string? version);
}
