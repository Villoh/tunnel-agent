using System;
using System.Runtime.InteropServices;
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
    public event Action<int>? DownloadProgressChanged;
    public int DownloadProgress { get; private set; }

    public AppUpdateService(bool allowPrerelease = false)
    {
        try
        {
            var source = new GithubSource($"https://github.com/{RepoOwner}/{RepoName}", null, allowPrerelease);
            var options = new UpdateOptions { ExplicitChannel = GetUpdateChannel() };
            _manager = new UpdateManager(source, options);
        }
        catch
        {
            // Velopack not initialized (e.g. running in test host or without installer).
            // _manager stays null; IsInstalled will return false and all operations no-op.
        }
    }

    private static string GetUpdateChannel()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
            return arch == Architecture.Arm64 ? "win-arm64" : "win-x64";
        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        // Linux
        return arch == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
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

        DownloadProgress = 0;
        SetState(AppUpdateState.Downloading);
        try
        {
            await _manager.DownloadUpdatesAsync(_pendingUpdate, p =>
            {
                DownloadProgress = p;
                DownloadProgressChanged?.Invoke(p);
            });
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
