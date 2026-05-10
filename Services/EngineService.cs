using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Thin orchestrator that composes EngineDownloadService, EngineConfigService,
/// and EngineProcessService into a single surface for the ViewModel.
/// </summary>
public sealed class EngineService
{
    private readonly EngineDownloadService _download;
    private readonly EngineConfigService _config;
    private readonly EngineProcessService _process;
    private readonly SettingsService _settings;

    // Unified observable state exposed to the ViewModel
    public EngineState State { get; private set; } = EngineState.NotInstalled;
    public string? InstalledVersion => _download.InstalledVersion;
    public string? LatestVersion    => _download.LatestVersion;
    public bool UpdateAvailable     => _download.UpdateAvailable;
    public double DownloadProgress  => _download.DownloadProgress;
    public bool IsRunning           => _process.IsRunning;
    public int Port                 => _process.Port;
    public string? LastError        => _process.LastError;

    public event EventHandler? StateChanged;

    public EngineService(SettingsService settings)
    {
        _settings = settings;
        _download = new EngineDownloadService();
        _config   = new EngineConfigService(settings);
        _process  = new EngineProcessService();

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

        if (!EngineDownloadService.IsBinaryInstalled())
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
        if (!EngineDownloadService.IsBinaryInstalled()) return;

        await _config.WriteConfigAsync();
        await _process.StartAsync(
            EngineDownloadService.BinaryPath,
            _config.ConfigPath,
            _settings.Current.Port,
            ct);
    }

    public Task StopAsync() => _process.StopAsync();

    public Task CheckForUpdateAsync() => _download.CheckForUpdateAsync();

    public async Task DownloadAndInstallAsync()
    {
        var wasRunning = IsRunning;
        if (wasRunning) await StopAsync();

        await _download.DownloadAndInstallAsync();

        if (wasRunning) await StartAsync();
    }
}
