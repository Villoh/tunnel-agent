using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.Perplexity;

/// <summary>Managed engine implementation for Perplexity WebUI Scraper.</summary>
public sealed class EngineService : IManagedEngine
{
    private readonly DownloadService _download;
    private readonly ProcessService _process;
    private readonly SettingsService _settings;
    private readonly SemaphoreSlim _updateLock = new(1, 1);

    public EngineDefinition Definition { get; } = EngineCatalog.PerplexityWebUiScraper;
    public EngineState State { get; private set; } = EngineState.NotInstalled;
    public string? InstalledVersion => _download.InstalledVersion;
    public string? LatestVersion => _download.LatestVersion;
    public string? InstalledBinarySha256 => _download.InstalledBinarySha256;
    public string? InstalledArchiveSha256 => _download.InstalledArchiveSha256;
    public string? LatestAssetName => _download.LatestAssetName;
    public string? LatestAssetSha256 => _download.LatestAssetSha256;
    public string? IntegrityError => _download.IntegrityError;
    public bool UpdateAvailable => _download.UpdateAvailable;
    public double DownloadProgress => _download.DownloadProgress;
    public bool IsRunning => _process.IsRunning;
    public int Port => GetRuntimeSettings().Port;
    public string? LastError => _download.IntegrityError ?? _process.LastError;
    public EngineErrorKind LastErrorKind => _download.IntegrityError is not null ? EngineErrorKind.None : _process.LastErrorKind;

    public event EventHandler? StateChanged;

    public EngineService(SettingsService settings)
    {
        _settings = settings;
        _download = new DownloadService();
        _process = new ProcessService();

        _download.StateChanged += OnSubStateChanged;
        _process.StateChanged += OnSubStateChanged;
    }

    public async Task InitializeAsync()
    {
        await _download.InitializeAsync();

        if (!DownloadService.IsBinaryInstalled())
        {
            await _download.CheckForUpdateAsync();
            try { await _download.DownloadAndInstallAsync(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Perplexity.EngineService] Download failed: {ex.Message}");
                return;
            }
        }
        else if (_settings.Current.AutoCheckForUpdates)
            _ = _download.CheckForUpdateAsync();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!DownloadService.IsBinaryInstalled()) return;
        await _process.StartAsync(DownloadService.BinaryPath, Port, ct);
    }

    public Task StopAsync() => _process.StopAsync();

    public Task WriteConfigAsync() => Task.CompletedTask;

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
