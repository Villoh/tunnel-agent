using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>
/// Thin orchestrator that composes DownloadService, ConfigService,
/// and ProcessService into a single surface for the ViewModel.
/// </summary>
public sealed class EngineService : IManagedEngine
{
    private readonly DownloadService _download;
    private readonly ConfigService _config;
    private readonly ProcessService _process;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public EngineDefinition Definition { get; } = EngineCatalog.CliProxyApi;

    // Unified observable state exposed to the ViewModel
    public EngineState State { get; private set; } = EngineState.NotInstalled;
    public string? InstalledVersion       => _download.InstalledVersion;
    public string? LatestVersion          => _download.LatestVersion;
    public string? InstalledBinarySha256  => _download.InstalledBinarySha256;
    public string? InstalledArchiveSha256 => _download.InstalledArchiveSha256;
    public string? LatestAssetName        => _download.LatestAssetName;
    public string? LatestAssetSha256      => _download.LatestAssetSha256;
    public string? IntegrityError         => _download.IntegrityError;
    public bool UpdateAvailable           => _download.UpdateAvailable;
    public double DownloadProgress        => _download.DownloadProgress;
    public bool IsRunning           => _process.IsRunning;
    public int Port                 => _process.Port;
    public string? LastError        => _download.IntegrityError ?? _process.LastError;

    public event EventHandler? StateChanged;

    public EngineService(SettingsService settings)
    {
        _settings = settings;
        _download = new DownloadService();
        _config   = new ConfigService(settings);
        _process  = new ProcessService();

        _download.StateChanged += OnSubStateChanged;
        _process.StateChanged  += OnSubStateChanged;
    }

    private void OnSubStateChanged(object? sender, EventArgs e)
    {
        // Process states take priority when running/starting/error;
        // download states take priority when downloading/installing.
        State = _process.State switch
        {
            EngineState.Running  or
            EngineState.Starting or
            EngineState.Error    => _process.State,
            _                    => _download.State
        };

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task InitializeAsync()
    {
        await _download.InitializeAsync();

        // Always write config on startup so secret-key and logging-to-file are current.
        // CLIProxyAPI's file watcher will hot-reload if it is already running.
        if (DownloadService.IsBinaryInstalled())
            await _config.WriteConfigAsync();

        if (!DownloadService.IsBinaryInstalled())
        {
            await _download.CheckForUpdateAsync();
            try
            {
                await _download.DownloadAndInstallAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EngineService] Download failed: {ex.Message}");
                return;
            }
        }

        if (_settings.Current.AutoCheckForUpdates)
            _ = _download.CheckForUpdateAsync();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!DownloadService.IsBinaryInstalled()) return;

        await _config.WriteConfigAsync();
        var defaultKey = TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY") ?? "";
        await _process.StartAsync(
            DownloadService.BinaryPath,
            _config.ConfigPath,
            _settings.Current.GetOrAddEngine(Definition.Id, Definition.DefaultPort).Port,
            defaultKey,
            ct);
    }

    public Task StopAsync() => _process.StopAsync();

    public Task WriteConfigAsync() => _config.WriteConfigAsync();

    public Task CheckForUpdateAsync() => _download.CheckForUpdateAsync();

    public Task<IReadOnlyList<EngineReleaseInfo>> ListReleasesAsync(int limit = 30) =>
        _download.ListReleasesAsync(limit);

    public Task PrepareVersionAsync(string version) => _download.PrepareVersionAsync(version);

    public Task DownloadAndInstallAsync() => DownloadAndInstallAsync(GetPreferredVersionOrNull());

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
        var preferred = _settings.Current.GetOrAddEngine(Definition.Id, Definition.DefaultPort).PreferredVersion;
        return string.IsNullOrWhiteSpace(preferred) ? null : preferred;
    }
}
