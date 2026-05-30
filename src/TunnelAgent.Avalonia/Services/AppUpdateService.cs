using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace TunnelAgent.Services;

public enum AppUpdateState { Idle, Checking, UpdateAvailable, Downloading, ReadyToInstall, Error }

public sealed class AppUpdateService
{
    private const string RepoOwner = "Villoh";
    private const string RepoName  = "tunnel-agent";

    private readonly UpdateManager? _manager;
    private UpdateInfo? _pendingUpdate;

    public AppUpdateState State { get; private set; } = AppUpdateState.Idle;
    public string?        NewVersion { get; private set; }

    public event Action? StateChanged;

    public AppUpdateService(bool allowPrerelease = false)
    {
        try
        {
            var source = new GithubSource($"https://github.com/{RepoOwner}/{RepoName}", null, allowPrerelease);
            _manager = new UpdateManager(source);
        }
        catch
        {
            // Velopack not initialized (e.g. running in test host or without installer).
            // _manager stays null; IsInstalled will return false and all operations no-op.
        }
    }

    public bool IsInstalled => _manager?.IsInstalled == true;

    // Returns true if a new version is available.
    public async Task<bool> CheckAsync()
    {
        if (_manager is null || !_manager.IsInstalled) return false;

        SetState(AppUpdateState.Checking);
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null)
            {
                SetState(AppUpdateState.Idle);
                return false;
            }

            _pendingUpdate = info;
            NewVersion = info.TargetFullRelease.Version.ToString();
            SetState(AppUpdateState.UpdateAvailable);
            return true;
        }
        catch
        {
            SetState(AppUpdateState.Error);
            return false;
        }
    }

    // Downloads the update. Call after CheckAsync returns true.
    public async Task DownloadAsync()
    {
        if (_manager is null || !_manager.IsInstalled || _pendingUpdate is null) return;

        SetState(AppUpdateState.Downloading);
        try
        {
            await _manager.DownloadUpdatesAsync(_pendingUpdate);
            SetState(AppUpdateState.ReadyToInstall);
        }
        catch
        {
            SetState(AppUpdateState.Error);
        }
    }

    // Restarts the app and applies the update. Must be called on the UI thread.
    public void ApplyAndRestart()
    {
        if (_manager is null || State != AppUpdateState.ReadyToInstall || _pendingUpdate is null) return;
        _manager.ApplyUpdatesAndRestart(_pendingUpdate.TargetFullRelease);
    }

    private void SetState(AppUpdateState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
