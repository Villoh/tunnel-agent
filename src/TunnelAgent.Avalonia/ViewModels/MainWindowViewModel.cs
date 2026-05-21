using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Services;
using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine;
using TunnelAgent.Infrastructure.Engine.CliProxy;

namespace TunnelAgent.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly EngineRegistryService _engineRegistry;
    private readonly ProviderCatalogService _catalog;
    private readonly PerplexityAccountCatalogService _perplexityAccounts;
    private readonly ILaunchAtLoginService _launchAtLogin;
    private readonly IFolderOpenService _folderOpen;
    private readonly TunnelAgent.Services.ModelFetchService _modelFetch;
    private readonly Dictionary<string, bool> _engineUpdateToastShown = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _modelFetchCts;
    private bool _engineReleaseSelectionReady;
    private string? _suppressAutoUpdateForEngineId;

    [ObservableProperty] private SectionKey _selectedSection = SectionKey.Providers;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private string _activeEngineId = EngineCatalog.CliProxyApi.Id;
    [ObservableProperty] private string _providersEngineId = EngineCatalog.CliProxyApi.Id;

    [ObservableProperty] private EngineState _engineState = EngineState.Stopped;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isCheckingForUpdate;
    [ObservableProperty] private bool _configHasBadge;
    [ObservableProperty] private bool _showUpdateToast;
    [ObservableProperty] private bool _showNoUpdateToast;
    [ObservableProperty] private bool _endpointCopied;
    [ObservableProperty] private bool _showUpdateSuccess;
    [ObservableProperty] private string _engineStatusText = "Stopped";

    [ObservableProperty] private bool _showAddAccountDialog;
    [ObservableProperty] private ProviderViewModel? _addAccountTarget;
    [ObservableProperty] private bool _showPerplexityAccountDialog;
    [ObservableProperty] private bool _showResetPerplexityDialog;

    [ObservableProperty] private bool _showOAuthStatus;
    [ObservableProperty] private bool _oAuthStatusIsError;
    [ObservableProperty] private string _oAuthStatusMessage = "";

    [ObservableProperty] private bool _showConfigurationStatus;
    [ObservableProperty] private bool _configurationStatusIsError;
    [ObservableProperty] private string _configurationStatusMessage = "";
    [ObservableProperty] private bool _showResetCredentialsDialog;

    [ObservableProperty] private bool _isLoadingEngineReleases;
    [ObservableProperty] private EngineReleaseViewModel? _selectedEngineRelease;

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();
    public ObservableCollection<PerplexityAccountViewModel> PerplexityAccounts { get; } = new();
    public ObservableCollection<AgentViewModel> Agents { get; } = new();
    public ObservableCollection<EngineReleaseViewModel> EngineReleases { get; } = new();
    public ObservableCollection<AvailableModelGroupViewModel> AvailableModelGroups { get; }
    public ObservableCollection<EngineOptionViewModel> EngineOptions { get; } = new();

    public MainWindowViewModel() : this(new SettingsService(), null!, null!, null!, null!, null!) { }

    public MainWindowViewModel(
        SettingsService settings,
        EngineRegistryService engineRegistry,
        ProviderCatalogService catalog,
        PerplexityAccountCatalogService perplexityAccounts,
        ILaunchAtLoginService? launchAtLogin = null,
        IFolderOpenService? folderOpen = null)
    {
        _settings = settings;
        _engineRegistry = engineRegistry ?? new EngineRegistryService(settings);
        var engineConfig = new ConfigService(settings);
        _catalog = catalog ?? new ProviderCatalogService(settings, engineConfig);
        _perplexityAccounts = perplexityAccounts ?? new PerplexityAccountCatalogService();
        _launchAtLogin = launchAtLogin ?? new LaunchAtLoginService();
        _folderOpen = folderOpen ?? new FolderOpenService();

        AvailableModelGroups = new ObservableCollection<AvailableModelGroupViewModel>();
        AvailableModelGroups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TotalAvailableModelCount));
        _modelFetch = new TunnelAgent.Services.ModelFetchService(settings);

        foreach (var engine in _engineRegistry.Engines)
            engine.StateChanged += OnAnyEngineStateChanged;

        _catalog.ProvidersRefreshed += OnProvidersRefreshed;
        _perplexityAccounts.AccountsChanged += OnPerplexityAccountsChanged;

        foreach (var definition in EngineCatalog.All)
            EngineOptions.Add(new EngineOptionViewModel(definition));

        SeedDemoAgents();
    }

    // Providers submenu highlight — independent from Config section
    public bool IsCliProxyEngineSelected => string.Equals(ProvidersEngineId, EngineCatalog.CliProxyApi.Id, StringComparison.OrdinalIgnoreCase);
    public bool IsPerplexityEngineSelected => string.Equals(ProvidersEngineId, EngineCatalog.PerplexityWebUiScraper.Id, StringComparison.OrdinalIgnoreCase);

    // Tab indices for SlidingTabBar
    public int ProvidersTabIndex => IsPerplexityEngineSelected ? 1 : 0;
    public int ConfigTabIndex => SelectedSection switch
    {
        SectionKey.ConfigCliProxy => 1,
        SectionKey.ConfigPerplexity => 2,
        _ => 0
    };
    public string ActiveEngineName => ActiveEngine.Definition.DisplayName;
    public string ActiveEngineDescription => ActiveEngine.Definition.Description;
    public string EndpointUrl => $"http://127.0.0.1:{Port}";
    public string AppVersion { get; } = TunnelAgent.AppVersion.Current;
    public bool IsLaunchAtLoginSupported => _launchAtLogin.IsSupported;
    public int ConnectedProviderCount => Providers.Count(p => p.Connected || p.ActiveAccountCount > 0);
    public int EnabledAgentCount => Agents.Count(a => a.Installed && a.Enabled);
    public int TotalAvailableModelCount => AvailableModelGroups.Sum(g => g.ModelCount);
    public int PerplexityAccountCount => PerplexityAccounts.Count;
    public bool HasPerplexityAccounts => PerplexityAccounts.Count > 0;
    public string PerplexityEmptyStateText => "Perplexity needs at least one saved WebUI session token account.";
    public string AuthFilesDescription => IsPerplexityEngineSelected
        ? "Perplexity session accounts are stored in app settings."
        : "OAuth tokens and custom provider keys are stored in the app auth folder.";

    public IManagedEngine ActiveEngine => _engineRegistry.Get(ActiveEngineId);
    private IManagedEngine CliProxyEngine => _engineRegistry.Get(EngineCatalog.CliProxyApi.Id);
    private IManagedEngine PerplexityEngine => _engineRegistry.Get(EngineCatalog.PerplexityWebUiScraper.Id);

    public int Port
    {
        get => _settings.Current.GetOrAddEngine(ActiveEngineId, ActiveEngine.Definition.DefaultPort).Port;
        set
        {
            var runtime = _settings.Current.GetOrAddEngine(ActiveEngineId, ActiveEngine.Definition.DefaultPort);
            if (runtime.Port == value) return;
            runtime.Port = value;
            if (string.Equals(ActiveEngineId, EngineCatalog.CliProxyApi.Id, StringComparison.OrdinalIgnoreCase))
                _settings.Current.Port = value;
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditablePort));
            OnPropertyChanged(nameof(EndpointUrl));
            RefreshEngineSectionProperties();
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

    public static RoutingStrategy[] RoutingStrategies { get; } = { RoutingStrategy.RoundRobin, RoutingStrategy.FillFirst };

    public ServerState ServerState => ToServerState(EngineState);

    public string SelectedEngineVersionDescription => SelectedEngineRelease is null
        ? $"Choose {ActiveEngineName} release to install."
        : CanInstallSelectedEngine ? "Install selected release with SHA256 verification." : "Selected release is already installed.";

    public string InstalledEngineHashLabel => ActiveEngine.InstalledArchiveSha256 is not null ? "Installed package SHA256" : "Local binary SHA256";
    public string InstalledEngineHashShort => ShortHash(ActiveEngine.InstalledArchiveSha256 ?? ActiveEngine.InstalledBinarySha256);
    public string InstalledEngineHashFull => ActiveEngine.InstalledArchiveSha256 ?? ActiveEngine.InstalledBinarySha256 ?? "Not available";
    public string LatestEngineHashShort => ShortHash(ActiveEngine.LatestAssetSha256);
    public string LatestEngineHashFull => ActiveEngine.LatestAssetSha256 ?? "Not available";
    public bool HasEngineIntegrityError => ActiveEngine.IntegrityError is not null;
    public string EngineIntegrityStatus => ActiveEngine.IntegrityError is not null ? "Checksum failed" : ActiveEngine.LatestAssetSha256 is null ? "Checksum pending" : "SHA256 ready";
    public string EngineIntegrityMessage => ActiveEngine.IntegrityError ?? "";
    public string LatestEngineAssetName => ActiveEngine.LatestAssetName ?? "Not available";
    public bool CanSelectEngineRelease => !IsLoadingEngineReleases && EngineState is not EngineState.Downloading and not EngineState.Installing;
    public bool CanInstallSelectedEngine => SelectedEngineRelease is not null && CanSelectEngineRelease && !VersionsEqual(SelectedEngineRelease.TagName, InstalledVersion);

    public string CliProxyInstalledVersion => CliProxyEngine.InstalledVersion ?? "Not installed";
    public string? CliProxyLatestVersion => CliProxyEngine.LatestVersion;
    public bool CliProxyUpdateAvailable => CliProxyEngine.UpdateAvailable;
    public string CliProxyStatusText => BuildEngineStatusText(CliProxyEngine);
    public ServerState CliProxyServerState => ToServerState(CliProxyEngine.State);
    public int CliProxyPort => _settings.Current.GetOrAddEngine(EngineCatalog.CliProxyApi.Id, EngineCatalog.CliProxyApi.DefaultPort).Port;
    public string CliProxyEndpointUrl => $"http://127.0.0.1:{CliProxyPort}";
    public bool IsCliProxyFocused => IsCliProxyEngineSelected;

    public string PerplexityInstalledVersion => PerplexityEngine.InstalledVersion ?? "Not installed";
    public string? PerplexityLatestVersion => PerplexityEngine.LatestVersion;
    public bool PerplexityUpdateAvailable => PerplexityEngine.UpdateAvailable;
    public string PerplexityStatusText => BuildEngineStatusText(PerplexityEngine);
    public ServerState PerplexityServerState => ToServerState(PerplexityEngine.State);
    public int PerplexityPort => _settings.Current.GetOrAddEngine(EngineCatalog.PerplexityWebUiScraper.Id, EngineCatalog.PerplexityWebUiScraper.DefaultPort).Port;
    public string PerplexityEndpointUrl => $"http://127.0.0.1:{PerplexityPort}";
    public bool IsPerplexityFocused => IsPerplexityEngineSelected;

    partial void OnSelectedSectionChanged(SectionKey value)
    {
        OnPropertyChanged(nameof(IsConfigSection));
        OnPropertyChanged(nameof(ConfigTabIndex));
        // Drive ActiveEngineId from config tab so shared engine commands/properties resolve correctly.
        // This does NOT affect ProvidersEngineId (sidebar highlight stays independent).
        if (value == SectionKey.ConfigCliProxy)
            ActiveEngineId = EngineCatalog.CliProxyApi.Id;
        else if (value == SectionKey.ConfigPerplexity)
            ActiveEngineId = EngineCatalog.PerplexityWebUiScraper.Id;
    }

    partial void OnProvidersEngineIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsCliProxyEngineSelected));
        OnPropertyChanged(nameof(IsPerplexityEngineSelected));
        OnPropertyChanged(nameof(ProvidersTabIndex));
    }

    partial void OnActiveEngineIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        // Not persisted — ActiveEngineId is transient UI state, not a user preference
        RefreshFocusedEngineState();
        _ = LoadEngineReleasesAsync();
        if (ActiveEngine.State == EngineState.Running)
        {
            _modelFetchCts?.Cancel();
            _modelFetchCts = new CancellationTokenSource();
            _ = _modelFetch.FetchAndApplyAsync(AvailableModelGroups, ActiveEngine.Port, ActiveEngineId, _modelFetchCts.Token);
        }
        OnPropertyChanged(nameof(ActiveEngineName));
        OnPropertyChanged(nameof(ActiveEngineDescription));
        OnPropertyChanged(nameof(AuthFilesDescription));
        OnPropertyChanged(nameof(IsCliProxyFocused));
        OnPropertyChanged(nameof(IsPerplexityFocused));
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

        var runtime = _settings.Current.GetOrAddEngine(ActiveEngineId, ActiveEngine.Definition.DefaultPort);
        var latestTag = ActiveEngine.LatestVersion ?? EngineReleases.FirstOrDefault(r => !r.IsPrerelease)?.TagName;
        runtime.PreferredVersion = VersionsEqual(value.TagName, latestTag) ? string.Empty : value.TagName;
        if (string.Equals(ActiveEngineId, EngineCatalog.CliProxyApi.Id, StringComparison.OrdinalIgnoreCase))
            _settings.Current.PreferredEngineVersion = runtime.PreferredVersion;
        _settings.Save();
    }

    private static string NormalizeThemeMode(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system"
    };

    public async Task InitializeAsync()
    {
        await _settings.LoadAsync();
        await ReconcileLaunchAtLoginAsync();
        NormalizeActiveEngineSetting();
        RefreshSettingsBindings();

        await _catalog.InitializeAsync();
        Providers.Clear();
        foreach (var vm in _catalog.Providers)
        {
            vm.AddAccountRequested += OnAddAccountRequested;
            Providers.Add(vm);
        }
        OnPropertyChanged(nameof(ConnectedProviderCount));

        // Pass any legacy accounts from settings.json for one-time migration to files
        await _perplexityAccounts.InitializeAsync(_settings.Current.PerplexityAccounts);
        // Clear from settings after migration so they don't persist there anymore
        if (_settings.Current.PerplexityAccounts.Count > 0)
        {
            _settings.Current.PerplexityAccounts.Clear();
            await _settings.SaveImmediateAsync();
        }
        ReloadPerplexityAccounts();

        foreach (var engine in _engineRegistry.Engines)
        {
            try { await engine.InitializeAsync(); }
            catch { }
        }

        RefreshFocusedEngineState();
        await LoadEngineReleasesAsync();
    }

    private void NormalizeActiveEngineSetting()
    {
        // Providers page always starts on CLIProxy regardless of last session
        ProvidersEngineId = EngineCatalog.CliProxyApi.Id;
        // ActiveEngineId follows Providers selection on startup
        ActiveEngineId = EngineCatalog.CliProxyApi.Id;
    }

    private void ReloadPerplexityAccounts()
    {
        PerplexityAccounts.Clear();
        foreach (var account in _perplexityAccounts.List())
            PerplexityAccounts.Add(account);
        OnPropertyChanged(nameof(PerplexityAccountCount));
        OnPropertyChanged(nameof(HasPerplexityAccounts));
    }

    private void OnPerplexityAccountsChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(ReloadPerplexityAccounts);

    private void OnProvidersRefreshed(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ConnectedProviderCount)));

    private void OnAnyEngineStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is IManagedEngine engine && string.Equals(engine.Definition.Id, ActiveEngineId, StringComparison.OrdinalIgnoreCase))
            {
                var wasAvailable = UpdateAvailable;

                if (engine.State == EngineState.Running)
                {
                    _modelFetchCts?.Cancel();
                    _modelFetchCts = new CancellationTokenSource();
                    _ = _modelFetch.FetchAndApplyAsync(AvailableModelGroups, engine.Port, engine.Definition.Id, _modelFetchCts.Token);
                }
                else if (engine.State == EngineState.Stopped || engine.State == EngineState.Error)
                {
                    _modelFetchCts?.Cancel();
                    AvailableModelGroups.Clear();
                }

                RefreshFocusedEngineState();
                if (UpdateAvailable && !wasAvailable && !_engineUpdateToastShown.GetValueOrDefault(ActiveEngineId))
                {
                    if (string.Equals(_suppressAutoUpdateForEngineId, ActiveEngineId, StringComparison.OrdinalIgnoreCase))
                    {
                        _suppressAutoUpdateForEngineId = null;
                    }
                    else
                    {
                        _engineUpdateToastShown[ActiveEngineId] = true;
                        if (AutoUpdate)
                        {
                            _ = ActiveEngine.DownloadAndInstallAsync();
                        }
                        else
                        {
                            ShowUpdateToast = true;
                            _ = Task.Delay(8000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowUpdateToast = false));
                        }
                    }
                }
            }

            RefreshEngineSectionProperties();
        });
    }

    private void RefreshFocusedEngineState()
    {
        EngineState = ActiveEngine.State;
        InstalledVersion = ActiveEngine.InstalledVersion;
        LatestVersion = ActiveEngine.LatestVersion;
        DownloadProgress = ActiveEngine.DownloadProgress;
        UpdateAvailable = ActiveEngine.UpdateAvailable;
        EngineStatusText = BuildEngineStatusText(ActiveEngine);
        UpdateBadgeState();
        RefreshEngineSectionProperties();
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
    }

    private void RefreshEngineSectionProperties()
    {
        OnPropertyChanged(nameof(CliProxyInstalledVersion));
        OnPropertyChanged(nameof(CliProxyLatestVersion));
        OnPropertyChanged(nameof(CliProxyUpdateAvailable));
        OnPropertyChanged(nameof(CliProxyStatusText));
        OnPropertyChanged(nameof(CliProxyServerState));
        OnPropertyChanged(nameof(CliProxyPort));
        OnPropertyChanged(nameof(CliProxyEndpointUrl));
        OnPropertyChanged(nameof(PerplexityInstalledVersion));
        OnPropertyChanged(nameof(PerplexityLatestVersion));
        OnPropertyChanged(nameof(PerplexityUpdateAvailable));
        OnPropertyChanged(nameof(PerplexityStatusText));
        OnPropertyChanged(nameof(PerplexityServerState));
        OnPropertyChanged(nameof(PerplexityPort));
        OnPropertyChanged(nameof(PerplexityEndpointUrl));
        OnPropertyChanged(nameof(EndpointUrl));
        OnPropertyChanged(nameof(Port));
        OnPropertyChanged(nameof(EditablePort));
    }

    private static string ShortHash(string? hash) => string.IsNullOrWhiteSpace(hash) ? "Not available" : hash[..Math.Min(12, hash.Length)];

    private static bool VersionsEqual(string? left, string? right) =>
        left is not null && right is not null && string.Equals(left.TrimStart('v'), right.TrimStart('v'), StringComparison.OrdinalIgnoreCase);

    private static ServerState ToServerState(EngineState state) => state switch
    {
        EngineState.Running => ServerState.Running,
        EngineState.Starting => ServerState.Starting,
        EngineState.Error => ServerState.Error,
        _ => ServerState.Stopped
    };

    private string BuildEngineStatusText(IManagedEngine engine) => engine.State switch
    {
        EngineState.Downloading => $"Downloading {engine.DownloadProgress:0}%",
        EngineState.Installing => "Installing…",
        EngineState.Running => $"{engine.InstalledVersion} · Running",
        EngineState.Starting => "Starting…",
        EngineState.Error => engine.LastError is not null ? $"Error: {engine.LastError}" : "Engine error",
        EngineState.NotInstalled => "Not installed",
        _ => "Stopped"
    };

    private void UpdateBadgeState() => ConfigHasBadge = ActiveEngine.UpdateAvailable && !AutoUpdate;

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

    private async Task PrepareSelectedEngineReleaseAsync(string version)
    {
        try { await ActiveEngine.PrepareVersionAsync(version); }
        catch { }
    }

    private async Task LoadEngineReleasesAsync()
    {
        IsLoadingEngineReleases = true;
        try
        {
            var releases = await ActiveEngine.ListReleasesAsync();
            EngineReleases.Clear();
            foreach (var release in releases)
                EngineReleases.Add(new EngineReleaseViewModel(release));

            var runtime = _settings.Current.GetOrAddEngine(ActiveEngineId, ActiveEngine.Definition.DefaultPort);
            var preferred = runtime.PreferredVersion;
            var selected = EngineReleases.FirstOrDefault(r => VersionsEqual(r.TagName, preferred))
                ?? EngineReleases.FirstOrDefault(r => VersionsEqual(r.TagName, ActiveEngine.LatestVersion))
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

    private async Task ApplyPortChangeAsync()
    {
        var engine = ActiveEngine;
        var wasRunning = engine.IsRunning;
        if (wasRunning) await engine.StopAsync();
        await engine.WriteConfigAsync();
        if (wasRunning) await engine.StartAsync();
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

    public async Task<(bool Success, string Message)> ConnectOAuthAsync(string providerId)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider is not null) provider.IsConnecting = true;
        try { return await _catalog.ConnectOAuthAsync(providerId); }
        finally
        {
            if (provider is not null) provider.IsConnecting = false;
        }
    }

    public void DisconnectOAuth(string providerId) => _catalog.DisconnectOAuth(providerId);

    public Task RefreshQuotaAsync(ProviderAccountViewModel account)
    {
        var provider = Providers.FirstOrDefault(p => p.Accounts.Contains(account));
        return provider is not null ? _catalog.RefreshAccountQuotaAsync(provider, account) : Task.CompletedTask;
    }

    private void OnAddAccountRequested(object? sender, EventArgs e)
    {
        if (sender is ProviderViewModel vm)
        {
            AddAccountTarget = vm;
            ShowAddAccountDialog = true;
        }
    }

    public async Task ConfirmAddAccountAsync(string providerId, string baseUrl, string apiKey, string? label)
    {
        ShowAddAccountDialog = false;
        await _catalog.AddAccountAsync(providerId, baseUrl, apiKey, label);
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    public async Task ConfirmAddPerplexityAccountAsync(string? label, string sessionToken)
    {
        ShowPerplexityAccountDialog = false;
        await _perplexityAccounts.AddAsync(label, sessionToken);
    }

    [RelayCommand] private void ShowAddPerplexityAccount() => ShowPerplexityAccountDialog = true;
    [RelayCommand] private void DismissPerplexityAccountDialog() => ShowPerplexityAccountDialog = false;
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

    [RelayCommand]
    public async Task RemovePerplexityAccountAsync(PerplexityAccountViewModel account) =>
        await _perplexityAccounts.RemoveAsync(account.Id);

    [RelayCommand]
    public async Task SetDefaultPerplexityAccountAsync(PerplexityAccountViewModel account) =>
        await _perplexityAccounts.SetDefaultAsync(account.Id);

    [RelayCommand] private void ResetPort() => Port = ActiveEngine.Definition.DefaultPort;

    [RelayCommand]
    private void FocusCliProxy()
    {
        ProvidersEngineId = EngineCatalog.CliProxyApi.Id;
        ActiveEngineId = EngineCatalog.CliProxyApi.Id;
    }

    [RelayCommand]
    private void FocusPerplexity()
    {
        ProvidersEngineId = EngineCatalog.PerplexityWebUiScraper.Id;
        ActiveEngineId = EngineCatalog.PerplexityWebUiScraper.Id;
    }

    [RelayCommand]
    public void OpenAuthFolder()
    {
        try { _folderOpen.OpenFolder(IPlatformInfo.Current.AuthDirectory); }
        catch (Exception ex)
        {
            ConfigurationStatusIsError = true;
            ConfigurationStatusMessage = $"Could not open auth folder: {ex.Message}";
            ShowConfigurationStatus = true;
        }
    }

    [RelayCommand]
    public void OpenSettingsFolder()
    {
        try { _folderOpen.OpenFolder(IPlatformInfo.Current.SettingsDirectory); }
        catch (Exception ex)
        {
            ConfigurationStatusIsError = true;
            ConfigurationStatusMessage = $"Could not open settings folder: {ex.Message}";
            ShowConfigurationStatus = true;
        }
    }

    [RelayCommand] private void ResetPerplexityAccounts() => ShowResetPerplexityDialog = true;
    [RelayCommand] private void DismissResetPerplexityDialog() => ShowResetPerplexityDialog = false;

    [RelayCommand]
    public Task ConfirmResetPerplexityAccountsAsync()
    {
        ShowResetPerplexityDialog = false;
        _perplexityAccounts.RemoveAll();
        ConfigurationStatusIsError = false;
        ConfigurationStatusMessage = "Perplexity session accounts removed.";
        ShowConfigurationStatus = true;
        return Task.CompletedTask;
    }

    [RelayCommand] private void ResetAllCredentials() => ShowResetCredentialsDialog = true;
    [RelayCommand] private void DismissResetCredentialsDialog() => ShowResetCredentialsDialog = false;

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

    [RelayCommand] private void DismissConfigurationStatus() => ShowConfigurationStatus = false;
    [RelayCommand]
    private void SelectProviders()
    {
        SelectedSection = SectionKey.Providers;
        ActiveEngineId = ProvidersEngineId;
    }
    [RelayCommand] private void SelectConfiguration() => SelectedSection = SectionKey.ConfigGeneral;
    [RelayCommand] private void SelectConfigGeneral() => SelectedSection = SectionKey.ConfigGeneral;
    [RelayCommand] private void SelectConfigCliProxy() => SelectedSection = SectionKey.ConfigCliProxy;
    [RelayCommand] private void SelectConfigPerplexity() => SelectedSection = SectionKey.ConfigPerplexity;

    public bool IsConfigSection => SelectedSection is SectionKey.ConfigGeneral or SectionKey.ConfigCliProxy or SectionKey.ConfigPerplexity;
    [RelayCommand] private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;
    [RelayCommand] private void ToggleTheme() => ThemeMode = IsDark ? "light" : "dark";
    [RelayCommand] private void DismissToast() => ShowUpdateToast = false;

    private const string IssueUrl = "https://github.com/Villoh/tunnel-agent/issues";

    [RelayCommand]
    private void OpenIssueUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = IssueUrl, UseShellExecute = true });
        }
        catch { }
    }

    [RelayCommand]
    private async Task CheckForUpdate()
    {
        if (IsCheckingForUpdate) return;
        IsCheckingForUpdate = true;
        // Invalidate cache so manual check always fetches fresh data
        TunnelAgent.Infrastructure.Engine.CliProxy.DownloadService.InvalidateCache();
        TunnelAgent.Infrastructure.Engine.Perplexity.DownloadService.InvalidateCache();
        try
        {
            await ActiveEngine.CheckForUpdateAsync();
            if (!ActiveEngine.UpdateAvailable)
            {
                ShowNoUpdateToast = true;
                _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowNoUpdateToast = false));
            }
        }
        catch { }
        finally { IsCheckingForUpdate = false; }
    }

    [RelayCommand]
    private async Task RefreshEngineReleases()
    {
        // Invalidate cache so Reload always fetches fresh data from GitHub
        TunnelAgent.Infrastructure.Engine.CliProxy.DownloadService.InvalidateCache();
        TunnelAgent.Infrastructure.Engine.Perplexity.DownloadService.InvalidateCache();
        await LoadEngineReleasesAsync();
    }

    [RelayCommand]
    private async Task UpdateEngine()
    {
        if (!ActiveEngine.UpdateAvailable) return;
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
        // Stay on correct config section for the active engine
        SelectedSection = string.Equals(ActiveEngineId, EngineCatalog.PerplexityWebUiScraper.Id, StringComparison.OrdinalIgnoreCase)
            ? SectionKey.ConfigPerplexity
            : SectionKey.ConfigCliProxy;
        ShowUpdateToast = false;
        var requestedVersion = string.IsNullOrWhiteSpace(version) ? ActiveEngine.LatestVersion : version;
        if (!string.IsNullOrWhiteSpace(requestedVersion) && !VersionsEqual(requestedVersion, ActiveEngine.LatestVersion))
            _suppressAutoUpdateForEngineId = ActiveEngineId;
        try { await ActiveEngine.DownloadAndInstallAsync(requestedVersion); }
        catch { return; }
        ConfigHasBadge = false;
        _engineUpdateToastShown[ActiveEngineId] = false;
        ShowUpdateSuccess = true;
        _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowUpdateSuccess = false));
    }

    [RelayCommand]
    public async Task RestartEngineAsync()
    {
        await ActiveEngine.StopAsync();
        await ActiveEngine.StartAsync();
    }

    [RelayCommand]
    public async Task StartServerAsync()
    {
        if (ActiveEngine.State is EngineState.Stopped or EngineState.NotInstalled)
            try { await ActiveEngine.StartAsync(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[StartServer] {ex.Message}");
            }
    }

    [RelayCommand] public async Task StopServerAsync() => await ActiveEngine.StopAsync();

    [RelayCommand]
    private void ToggleAgent(AgentViewModel a)
    {
        a.Enabled = !a.Enabled;
        OnPropertyChanged(nameof(EnabledAgentCount));
    }

    private void SeedDemoAgents()
    {
        Agents.Add(new AgentViewModel("claude-code", "Claude Code", "claude", "Terminal", true) { Enabled = true, RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("codex", "Codex CLI", "codex", "Code", true) { Enabled = true, RouteProviderId = "codex" });
        Agents.Add(new AgentViewModel("cursor", "Cursor Agent", "cursor-agent", "Sparkles", true) { Enabled = false, RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("aider", "Aider", "aider", "Terminal", false, "Install via pip to route through Tunnel."));
    }
}
