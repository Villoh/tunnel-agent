using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly EngineService _engine;
    private readonly SettingsService _settings;
    private readonly ProviderCatalogService _catalog;
    private readonly ILaunchAtLoginService _launchAtLogin;
    private readonly IFolderOpenService _folderOpen;
    private readonly TunnelAgent.Services.ModelFetchService _modelFetch;

    private CancellationTokenSource? _modelFetchCts;

    [ObservableProperty] private SectionKey _selectedSection = SectionKey.Providers;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isDark;

    // Engine-forwarded state
    [ObservableProperty] private EngineState _engineState = EngineState.Stopped;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _configHasBadge;
    [ObservableProperty] private bool _showUpdateToast;
    [ObservableProperty] private bool _endpointCopied;
    [ObservableProperty] private bool _showUpdateSuccess;
    [ObservableProperty] private string _engineStatusText = "Stopped";

    // Add-account dialog state
    [ObservableProperty] private bool _showAddAccountDialog;
    [ObservableProperty] private ProviderViewModel? _addAccountTarget;

    // OAuth status feedback
    [ObservableProperty] private bool _showOAuthStatus;
    [ObservableProperty] private bool _oAuthStatusIsError;
    [ObservableProperty] private string _oAuthStatusMessage = "";

    // Configuration feedback and confirmations
    [ObservableProperty] private bool _showConfigurationStatus;
    [ObservableProperty] private bool _configurationStatusIsError;
    [ObservableProperty] private string _configurationStatusMessage = "";
    [ObservableProperty] private bool _showResetCredentialsDialog;

    // Engine release selection
    [ObservableProperty] private bool _isLoadingEngineReleases;
    [ObservableProperty] private EngineReleaseViewModel? _selectedEngineRelease;
    private bool _engineReleaseSelectionReady;

    // Settings-backed properties
    public int Port
    {
        get => _settings.Current.Port;
        set
        {
            if (_settings.Current.Port == value) return;
            _settings.Current.Port = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditablePort));
            OnPropertyChanged(nameof(EndpointUrl));
            _ = ApplyPortChangeAsync();
        }
    }

    public decimal? EditablePort
    {
        get => Port;
        set
        {
            if (value is null) return;
            Port = decimal.ToInt32(value.Value);
        }
    }

    public bool LaunchAtLogin
    {
        get => _settings.Current.LaunchAtLogin;
        set
        {
            if (_settings.Current.LaunchAtLogin == value) return;
            _settings.Current.LaunchAtLogin = value;
            _settings.Save();
            OnPropertyChanged();
            _ = ApplyLaunchAtLoginFromUserAsync(value);
        }
    }

    public bool IsLaunchAtLoginSupported => _launchAtLogin.IsSupported;

    public string ThemeMode
    {
        get => _settings.Current.ThemeMode;
        set
        {
            var normalized = NormalizeThemeMode(value);
            if (_settings.Current.ThemeMode == normalized) return;
            _settings.Current.ThemeMode = normalized;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public static string[] ThemeModes { get; } = { "system", "light", "dark" };

    private static string NormalizeThemeMode(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system"
    };

    public bool AutoCheckForUpdates
    {
        get => _settings.Current.AutoCheckForUpdates;
        set
        {
            if (_settings.Current.AutoCheckForUpdates == value) return;
            _settings.Current.AutoCheckForUpdates = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAutoUpdateEnabled));
        }
    }

    /// <summary>Auto-update requires auto-check to be enabled.</summary>
    public bool IsAutoUpdateEnabled => AutoCheckForUpdates;

    public bool AutoUpdate
    {
        get => _settings.Current.AutoUpdate;
        set { _settings.Current.AutoUpdate = value; _settings.Save(); OnPropertyChanged(); UpdateBadgeState(); }
    }

    public RoutingStrategy RoutingStrategy
    {
        get => _settings.Current.RoutingStrategy;
        set { _settings.Current.RoutingStrategy = value; _settings.Save(); OnPropertyChanged(); }
    }

    public static RoutingStrategy[] RoutingStrategies { get; } =
        { RoutingStrategy.RoundRobin, RoutingStrategy.FillFirst };

    public ServerState ServerState => EngineState switch
    {
        EngineState.Running  => ServerState.Running,
        EngineState.Starting => ServerState.Starting,
        EngineState.Error    => ServerState.Error,
        _                    => ServerState.Stopped
    };

    public string AppVersion { get; } = TunnelAgent.AppVersion.Current;

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();
    public ObservableCollection<AgentViewModel> Agents { get; } = new();
    public ObservableCollection<EngineReleaseViewModel> EngineReleases { get; } = new();
    public ObservableCollection<AvailableModelGroupViewModel> AvailableModelGroups { get; }
    public string EndpointUrl => $"http://127.0.0.1:{Port}";
    public string InstalledEngineHashLabel => _engine.InstalledArchiveSha256 is not null
        ? "Installed package SHA256"
        : "Local binary SHA256";
    public string InstalledEngineHashShort => ShortHash(_engine.InstalledArchiveSha256 ?? _engine.InstalledBinarySha256);
    public string InstalledEngineHashFull => _engine.InstalledArchiveSha256 ?? _engine.InstalledBinarySha256 ?? "Not available";
    public string LatestEngineHashShort => ShortHash(_engine.LatestAssetSha256);
    public string LatestEngineHashFull => _engine.LatestAssetSha256 ?? "Not available";
    public bool HasEngineIntegrityError => _engine.IntegrityError is not null;
    public string EngineIntegrityStatus => _engine.IntegrityError is not null
        ? "Checksum failed"
        : _engine.LatestAssetSha256 is null ? "Checksum pending" : "SHA256 ready";
    public string EngineIntegrityMessage => _engine.IntegrityError ?? "";
    public string LatestEngineAssetName => _engine.LatestAssetName ?? "Not available";
    public bool CanSelectEngineRelease => !IsLoadingEngineReleases &&
        EngineState is not EngineState.Downloading and not EngineState.Installing;
    public bool CanInstallSelectedEngine => SelectedEngineRelease is not null &&
        CanSelectEngineRelease &&
        !VersionsEqual(SelectedEngineRelease.TagName, InstalledVersion);
    public string SelectedEngineVersionDescription => SelectedEngineRelease is null
        ? "Choose a CLIProxyAPI release to install."
        : CanInstallSelectedEngine ? "Install selected release with SHA256 verification." : "Selected release is already installed.";
    public string AuthFilesDescription => "OAuth tokens and custom provider keys are stored in the app auth folder.";

    public int ConnectedProviderCount   => Providers.Count(p => p.Connected || p.ActiveAccountCount > 0);
    public int EnabledAgentCount        => Agents.Count(a => a.Installed && a.Enabled);
    public int TotalAvailableModelCount => AvailableModelGroups.Sum(g => g.ModelCount);

    // Design-time constructor
    public MainWindowViewModel() : this(new SettingsService(), null!, null!, null!, null!) { }

    public MainWindowViewModel(
        SettingsService settings,
        EngineService engine,
        ProviderCatalogService catalog,
        ILaunchAtLoginService? launchAtLogin = null,
        IFolderOpenService? folderOpen = null)
    {
        _settings = settings;
        _engine   = engine  ?? new EngineService(settings);
        var engineConfig = new EngineConfigService(settings);
        _catalog  = catalog ?? new ProviderCatalogService(settings, engineConfig);
        _launchAtLogin = launchAtLogin ?? new LaunchAtLoginService();
        _folderOpen = folderOpen ?? new FolderOpenService();

        AvailableModelGroups = new ObservableCollection<AvailableModelGroupViewModel>();
        AvailableModelGroups.CollectionChanged += (_, _) =>
            OnPropertyChanged(nameof(TotalAvailableModelCount));

        _modelFetch = new TunnelAgent.Services.ModelFetchService(settings);
        _engine.StateChanged  += OnEngineStateChanged;
        _catalog.ProvidersRefreshed += OnProvidersRefreshed;

        SeedDemoAgents();
    }

    private bool _updateToastShown;

    private void OnEngineStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var wasAvailable = UpdateAvailable;

            // Refresh model list when server starts/stops
            if (_engine.State == EngineState.Running)
            {
                _modelFetchCts?.Cancel();
                _modelFetchCts = new CancellationTokenSource();
                _ = _modelFetch.FetchAndApplyAsync(AvailableModelGroups, _modelFetchCts.Token);
            }
            else if (_engine.State == EngineState.Stopped || _engine.State == EngineState.Error)
            {
                _modelFetchCts?.Cancel();
                AvailableModelGroups.Clear();
            }

            EngineState      = _engine.State;
            InstalledVersion = _engine.InstalledVersion;
            LatestVersion    = _engine.LatestVersion;
            DownloadProgress = _engine.DownloadProgress;
            UpdateAvailable  = _engine.UpdateAvailable;
            EngineStatusText = BuildEngineStatusText();
            UpdateBadgeState();
            OnPropertyChanged(nameof(ServerState));
            OnPropertyChanged(nameof(InstalledEngineHashLabel));
            OnPropertyChanged(nameof(InstalledEngineHashShort));
            OnPropertyChanged(nameof(InstalledEngineHashFull));
            OnPropertyChanged(nameof(LatestEngineHashShort));
            OnPropertyChanged(nameof(LatestEngineHashFull));
            OnPropertyChanged(nameof(HasEngineIntegrityError));
            OnPropertyChanged(nameof(EngineIntegrityStatus));
            OnPropertyChanged(nameof(EngineIntegrityMessage));
            OnPropertyChanged(nameof(LatestEngineAssetName));
            OnPropertyChanged(nameof(CanSelectEngineRelease));
            OnPropertyChanged(nameof(CanInstallSelectedEngine));
            OnPropertyChanged(nameof(SelectedEngineVersionDescription));

            if (UpdateAvailable && !wasAvailable && !_updateToastShown && string.IsNullOrWhiteSpace(_settings.Current.PreferredEngineVersion))
            {
                _updateToastShown = true;
                if (AutoUpdate)
                {
                    _ = _engine.DownloadAndInstallAsync();
                }
                else
                {
                    ShowUpdateToast = true;
                    _ = Task.Delay(8000).ContinueWith(_ =>
                        Dispatcher.UIThread.Post(() => ShowUpdateToast = false));
                }
            }
        });
    }

    private void OnProvidersRefreshed(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ConnectedProviderCount));
        });
    }

    partial void OnIsLoadingEngineReleasesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSelectEngineRelease));
        OnPropertyChanged(nameof(CanInstallSelectedEngine));
        OnPropertyChanged(nameof(SelectedEngineVersionDescription));
    }

    partial void OnSelectedEngineReleaseChanged(EngineReleaseViewModel? value)
    {
        OnPropertyChanged(nameof(CanInstallSelectedEngine));
        OnPropertyChanged(nameof(SelectedEngineVersionDescription));
        if (value is null || IsLoadingEngineReleases) return;
        _ = PrepareSelectedEngineReleaseAsync(value.TagName);
        if (!_engineReleaseSelectionReady) return;

        var latestTag = _engine.LatestVersion ?? EngineReleases.FirstOrDefault(r => !r.IsPrerelease)?.TagName;
        _settings.Current.PreferredEngineVersion = VersionsEqual(value.TagName, latestTag) ? "" : value.TagName;
        _settings.Save();
    }

    private async Task PrepareSelectedEngineReleaseAsync(string version)
    {
        try { await _engine.PrepareVersionAsync(version); }
        catch { }
    }

    private static string ShortHash(string? hash) =>
        string.IsNullOrWhiteSpace(hash) ? "Not available" : hash[..Math.Min(12, hash.Length)];

    private static bool VersionsEqual(string? left, string? right) =>
        left is not null && right is not null &&
        string.Equals(left.TrimStart('v'), right.TrimStart('v'), StringComparison.OrdinalIgnoreCase);

    private string BuildEngineStatusText() => _engine.State switch
    {
        EngineState.Downloading  => $"Downloading {_engine.DownloadProgress:0}%",
        EngineState.Installing   => "Installing…",
        EngineState.Running      => $"{_engine.InstalledVersion} · Running",
        EngineState.Starting     => "Starting…",
        EngineState.Error        => _engine.LastError is not null ? $"Error: {_engine.LastError}" : "Engine error",
        EngineState.NotInstalled => "Not installed",
        _                        => "Stopped"
    };

    private void UpdateBadgeState() =>
        ConfigHasBadge = _engine.UpdateAvailable && !AutoUpdate;

    private void RefreshSettingsBindings()
    {
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(EditablePort));
        OnPropertyChanged(nameof(EndpointUrl));
        OnPropertyChanged(nameof(LaunchAtLogin));
        OnPropertyChanged(nameof(IsLaunchAtLoginSupported));
        OnPropertyChanged(nameof(ThemeMode));
        OnPropertyChanged(nameof(AutoCheckForUpdates));
        OnPropertyChanged(nameof(IsAutoUpdateEnabled));
        OnPropertyChanged(nameof(AutoUpdate));
        OnPropertyChanged(nameof(RoutingStrategy));
        UpdateBadgeState();
    }

    private async Task ApplyPortChangeAsync()
    {
        var wasRunning = _engine.IsRunning;
        if (wasRunning) await _engine.StopAsync();
        await _engine.WriteConfigAsync();
        if (wasRunning) await _engine.StartAsync();
    }

    private async Task<bool> TryApplyLaunchAtLoginAsync(bool enabled)
    {
        try
        {
            await _launchAtLogin.SetEnabledAsync(enabled);
            return true;
        }
        catch (Exception ex)
        {
            OAuthStatusIsError = true;
            OAuthStatusMessage = $"Could not update launch at login: {ex.Message}";
            ShowOAuthStatus = true;
            return false;
        }
    }

    private async Task ApplyLaunchAtLoginFromUserAsync(bool enabled)
    {
        if (await TryApplyLaunchAtLoginAsync(enabled)) return;

        _settings.Current.LaunchAtLogin = !enabled;
        _settings.Save();
        OnPropertyChanged(nameof(LaunchAtLogin));
    }

    private async Task ReconcileLaunchAtLoginAsync()
    {
        if (!_launchAtLogin.IsSupported) return;

        var desired = _settings.Current.LaunchAtLogin;
        bool actual;
        try { actual = await _launchAtLogin.GetEnabledAsync(); }
        catch { actual = desired; }

        if (actual == desired) return;
        if (await TryApplyLaunchAtLoginAsync(desired)) return;

        _settings.Current.LaunchAtLogin = actual;
        _settings.Save();
    }

    public async Task InitializeAsync()
    {
        await _settings.LoadAsync();
        await ReconcileLaunchAtLoginAsync();
        RefreshSettingsBindings();
        await _catalog.InitializeAsync();

        // Populate Providers collection from catalog
        Providers.Clear();
        foreach (var vm in _catalog.Providers)
        {
            vm.AddAccountRequested += OnAddAccountRequested;
            Providers.Add(vm);
        }
        OnPropertyChanged(nameof(ConnectedProviderCount));

        await _engine.InitializeAsync();
        await LoadEngineReleasesAsync();
    }

    private async Task LoadEngineReleasesAsync()
    {
        IsLoadingEngineReleases = true;
        try
        {
            var releases = await _engine.ListReleasesAsync();
            EngineReleases.Clear();
            foreach (var release in releases)
                EngineReleases.Add(new EngineReleaseViewModel(release));

            var preferred = _settings.Current.PreferredEngineVersion;
            var selected = EngineReleases.FirstOrDefault(r => VersionsEqual(r.TagName, preferred))
                ?? EngineReleases.FirstOrDefault(r => VersionsEqual(r.TagName, _engine.LatestVersion))
                ?? EngineReleases.FirstOrDefault();

            IsLoadingEngineReleases = false;
            if (selected is not null)
                SelectedEngineRelease = selected;
            _engineReleaseSelectionReady = true;
        }
        catch
        {
            IsLoadingEngineReleases = false;
        }
    }

    // ── OAuth connect / disconnect ──────────────────────────────────────────

    /// <summary>
    /// Starts the OAuth login flow for the given provider.
    /// Returns a user-facing message to display.
    /// </summary>
    public async Task<(bool Success, string Message)> ConnectOAuthAsync(string providerId)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is not null) provider.IsConnecting = true;
        try
        {
            return await _catalog.ConnectOAuthAsync(providerId);
        }
        finally
        {
            if (provider is not null) provider.IsConnecting = false;
        }
    }

    public void DisconnectOAuth(string providerId) =>
        _catalog.DisconnectOAuth(providerId);

    public Task RefreshQuotaAsync(ProviderAccountViewModel account)
    {
        var provider = Providers.FirstOrDefault(p => p.Accounts.Contains(account));
        return provider is not null
            ? _catalog.RefreshAccountQuotaAsync(provider, account)
            : Task.CompletedTask;
    }

    // ── Add-account flow ─────────────────────────────────────────────────────

    private void OnAddAccountRequested(object? sender, EventArgs e)
    {
        if (sender is ProviderViewModel vm)
        {
            AddAccountTarget    = vm;
            ShowAddAccountDialog = true;
        }
    }

    /// <summary>Called by the AddAccount dialog on confirm.</summary>
    public async Task ConfirmAddAccountAsync(
        string providerId, string baseUrl, string apiKey, string? label)
    {
        ShowAddAccountDialog = false;
        await _catalog.AddAccountAsync(providerId, baseUrl, apiKey, label);
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    [RelayCommand] private void DismissAddAccountDialog() => ShowAddAccountDialog = false;

    [RelayCommand]
    public async Task RemoveAccountAsync(ProviderAccountViewModel account)
    {
        if (account.IsCustomKey)
            await _catalog.RemoveAccountAsync(account.ProviderId, account.ApiKey);
        else
            _catalog.RemoveOAuthAccount(account.ProviderId, account.Email);

        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    // ── Engine commands ───────────────────────────────────────────────────────

    [RelayCommand] private void ResetPort() => Port = 8317;

    [RelayCommand]
    public void OpenAuthFolder()
    {
        try
        {
            _folderOpen.OpenFolder(IPlatformInfo.Current.AuthDirectory);
        }
        catch (Exception ex)
        {
            ConfigurationStatusIsError = true;
            ConfigurationStatusMessage = $"Could not open auth folder: {ex.Message}";
            ShowConfigurationStatus = true;
        }
    }

    [RelayCommand]
    private void ResetAllCredentials() => ShowResetCredentialsDialog = true;

    [RelayCommand]
    private void DismissResetCredentialsDialog() => ShowResetCredentialsDialog = false;

    [RelayCommand]
    private async Task ConfirmResetCredentialsAsync()
    {
        ShowResetCredentialsDialog = false;
        await _catalog.ResetAllCredentialsAsync();
        ConfigurationStatusIsError = false;
        ConfigurationStatusMessage = "TunnelAgent-managed credentials were backed up and removed.";
        ShowConfigurationStatus = true;
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    [RelayCommand]
    private void DismissConfigurationStatus() => ShowConfigurationStatus = false;

    [RelayCommand] private void SelectProviders()     => SelectedSection = SectionKey.Providers;
    // [RelayCommand] private void SelectAgents()        => SelectedSection = SectionKey.Agents;  // disabled until implemented
    [RelayCommand] private void SelectConfiguration() => SelectedSection = SectionKey.Configuration;
    [RelayCommand] private void ToggleSidebar()       => IsSidebarCollapsed = !IsSidebarCollapsed;
    [RelayCommand] private void ToggleTheme()         => ThemeMode = IsDark ? "light" : "dark";
    [RelayCommand] private void DismissToast()        => ShowUpdateToast = false;

    private const string IssueUrl = "https://github.com/Villoh/tunnel-agent/issues";

    [RelayCommand]
    private void OpenIssueUrl()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = IssueUrl,
                UseShellExecute = true
            });
        }
        catch { /* swallow — best effort */ }
    }

    [RelayCommand]
    private async Task RefreshEngineReleases() => await LoadEngineReleasesAsync();

    [RelayCommand]
    private async Task UpdateEngine()
    {
        if (!_engine.UpdateAvailable) return;
        await InstallEngineVersionAsync(null);
    }

    [RelayCommand]
    private async Task InstallSelectedEngine()
    {
        if (SelectedEngineRelease is null || !CanInstallSelectedEngine) return;
        await InstallEngineVersionAsync(SelectedEngineRelease.TagName);
    }

    private async Task InstallEngineVersionAsync(string? version)
    {
        SelectedSection = SectionKey.Configuration;
        ShowUpdateToast = false;

        try { await _engine.DownloadAndInstallAsync(version); }
        catch { return; }

        ConfigHasBadge    = false;
        _updateToastShown = false;

        ShowUpdateSuccess = true;
        _ = Task.Delay(4000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => ShowUpdateSuccess = false));
    }

    [RelayCommand]
    public async Task RestartEngineAsync()
    {
        await _engine.StopAsync();
        await _engine.StartAsync();
    }

    [RelayCommand]
    public async Task StartServerAsync()
    {
        if (_engine.State == EngineState.Stopped)
            await _engine.StartAsync();
    }

    [RelayCommand] public async Task StopServerAsync() => await _engine.StopAsync();

    [RelayCommand]
    private void ToggleAgent(AgentViewModel a)
    {
        a.Enabled = !a.Enabled;
        OnPropertyChanged(nameof(EnabledAgentCount));
    }

    // ── Agents seed + activity refresh ────────────────────────────────────────

    private void SeedDemoAgents()
    {
        Agents.Add(new AgentViewModel("claude-code", "Claude Code", "claude",       "Terminal", true)  { Enabled = true,  RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("codex",       "Codex CLI",   "codex",        "Code",     true)  { Enabled = true,  RouteProviderId = "codex"  });
        Agents.Add(new AgentViewModel("cursor",      "Cursor Agent","cursor-agent", "Sparkles", true)  { Enabled = false, RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("aider",       "Aider",       "aider",        "Terminal", false,
            "Install via pip to route through Tunnel."));
    }


}
