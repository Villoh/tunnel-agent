using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.Core.Engine;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.NineRouter;

/// <summary>
/// Managed engine implementation for 9Router. Composes
/// <see cref="DownloadService"/>, <see cref="ProcessService"/>, and
/// <see cref="SettingsService"/> into a single <see cref="IManagedEngine"/> surface.
/// </summary>
public sealed class EngineService : IManagedEngine
{
    private readonly DownloadService _download;
    private readonly ProcessService _process;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _updateLock = new(1, 1);
    private string? _cannotStartError;

    /// <inheritdoc />
    public EngineDefinition Definition { get; } = EngineCatalog.NineRouter;

    /// <inheritdoc />
    public EngineState State { get; private set; } = EngineState.NotInstalled;

    /// <inheritdoc />
    public string? InstalledVersion => _download.InstalledVersion;

    /// <inheritdoc />
    public string? LatestVersion => _download.LatestVersion;

    /// <inheritdoc />
    public string? InstalledBinarySha256 => _download.InstalledBinarySha256;

    /// <inheritdoc />
    public string? InstalledArchiveSha256 => _download.InstalledArchiveSha256;

    /// <inheritdoc />
    public string? LatestAssetName => _download.LatestAssetName;

    /// <inheritdoc />
    public string? LatestAssetSha256 => _download.LatestAssetSha256;

    /// <inheritdoc />
    public string? IntegrityError => _download.IntegrityError;

    /// <inheritdoc />
    public bool UpdateAvailable => _download.UpdateAvailable;

    /// <inheritdoc />
    public double DownloadProgress => _download.DownloadProgress;

    /// <inheritdoc />
    public bool IsRunning => _process.IsRunning;

    /// <inheritdoc />
    public int Port => GetRuntimeSettings().Port;

    /// <inheritdoc />
    public string? LastError => _cannotStartError ?? _download.IntegrityError ?? _process.LastError;

    /// <inheritdoc />
    public EngineErrorKind LastErrorKind => _cannotStartError is not null
        ? EngineErrorKind.LaunchFailed
        : _download.IntegrityError is not null ? EngineErrorKind.None : _process.LastErrorKind;

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <summary>Creates a 9Router engine that installs into the default local-data directory.</summary>
    /// <param name="settings">Persisted app settings used for the engine port and preferred version.</param>
    public EngineService(SettingsService settings)
    {
        _settings = settings;
        _download = new DownloadService();
        _process = new ProcessService();

        _download.StateChanged += OnSubStateChanged;
        _process.StateChanged += OnSubStateChanged;
    }

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _download.InitializeAsync();

        if (!DownloadService.IsBinaryInstalled())
        {
            await _download.CheckForUpdateAsync();
            try { await _download.DownloadAndInstallAsync(); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NineRouter.EngineService] Download failed: {ex.Message}");
                return;
            }
        }
        else if (_settings.Current.AutoCheckForUpdates)
            _ = _download.CheckForUpdateAsync();
    }

    /// <summary>
    /// Starts the 9Router Node.js server when the package is installed and Node.js is available.
    /// Returns without throwing when the engine is not installed or Node.js cannot be detected.
    /// </summary>
    /// <param name="ct">Token used to cancel the process health-check wait.</param>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!DownloadService.IsBinaryInstalled()) return;

        var node = new NodeRuntimeDetector().Detect();
        if (node is null)
        {
            _cannotStartError = DownloadService.NodeRequiredMessage;
            State = EngineState.Error;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var serverEntryPath = _download.ServerEntryPath;
        if (serverEntryPath is null) return;

        _cannotStartError = null;
        await _process.StartAsync(node.ExecutablePath, serverEntryPath, Port, ct);
    }

    /// <inheritdoc />
    public Task StopAsync() => _process.StopAsync();

    /// <summary>No-op: 9Router persists configuration in SQLite and HTTP, not a file Tunnel Agent writes.</summary>
    public Task WriteConfigAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public Task CheckForUpdateAsync() => _download.CheckForUpdateAsync();

    /// <inheritdoc />
    public Task<IReadOnlyList<EngineReleaseInfo>> ListReleasesAsync(int limit = 30) =>
        _download.ListReleasesAsync(limit);

    /// <inheritdoc />
    public Task PrepareVersionAsync(string version) => _download.PrepareVersionAsync(version);

    /// <inheritdoc />
    public Task DownloadAndInstallAsync() => DownloadAndInstallAsync(GetPreferredVersionOrNull());

    /// <inheritdoc />
    public async Task DownloadAndInstallAsync(string? version)
    {
        await _updateLock.WaitAsync();
        try
        {
            var wasRunning = IsRunning;
            if (wasRunning) await StopAsync();

            await _download.DownloadAndInstallAsync(string.IsNullOrWhiteSpace(version) ? GetPreferredVersionOrNull() : version);

            if (wasRunning) await StartAsync();
        }
        finally
        {
            _updateLock.Release();
        }
    }

    private string? GetPreferredVersionOrNull()
    {
        var preferred = GetRuntimeSettings().PreferredVersion;
        return string.IsNullOrWhiteSpace(preferred) ? null : preferred;
    }

    private void OnSubStateChanged(object? sender, EventArgs e)
    {
        State = _process.State switch
        {
            EngineState.Running or EngineState.Starting or EngineState.Error => _process.State,
            _ => _download.State
        };

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private EngineRuntimeSettings GetRuntimeSettings() =>
        _settings.Current.GetOrAddEngine(Definition.Id, Definition.DefaultPort);
}
