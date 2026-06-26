using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Mail;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TunnelAgent.Services;
using TunnelAgent.Core.Engine;
using TunnelAgent.Infrastructure.Engine;
using TunnelAgent.Infrastructure.Engine.CliProxy;
using TunnelAgent.Infrastructure.Engine.Perplexity;

namespace TunnelAgent.ViewModels;

/// <summary>Per-agent result inside a multi-agent configure operation.</summary>
public sealed record AgentConfigItemResult(
    string AgentName, bool Success, string? Error, string? ConfigPath);

public sealed record CliProxyApiKeyViewModel(string Value, bool IsDefault)
{
    public string Masked => string.IsNullOrEmpty(Value)
        ? ""
        : Value.Length > 12 ? $"{Value[..8]}...{Value[^4..]}" : Value;
    public bool CanRemove => true;
    public bool CanSetDefault => !IsDefault;
}

public sealed class RoutingStrategyOption(RoutingStrategy value, string displayKey) : ObservableObject
{
    public RoutingStrategy Value { get; } = value;
    public string Display => LocalizationService.Instance.GetString(displayKey);

    public void Refresh() => OnPropertyChanged(nameof(Display));

    public override string ToString() => Display;
}

public partial class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly LocalizationService _localization;
    // OS UI culture captured at construction, before any SetCulture override mutates
    // CultureInfo.CurrentUICulture. Used to resolve "System default" reliably.
    private readonly System.Globalization.CultureInfo _systemCulture = System.Globalization.CultureInfo.CurrentUICulture;
    private readonly SettingsService _settings;
    private readonly EngineRegistryService _engineRegistry;
    private readonly ProviderCatalogService _catalog;
    private readonly PerplexityAccountCatalogService _perplexityAccounts;
    private readonly ILaunchAtLoginService _launchAtLogin;
    private readonly IFolderOpenService _folderOpen;
    private readonly TunnelAgent.Services.ModelFetchService _modelFetch;
    private readonly TunnelAgent.Services.UpstreamModelFetchService _upstreamModelFetch = new();
    private CancellationTokenSource? _customProviderModelFetchCts;
    private ConfigService _configService = null!;
    private readonly TokenGeneratorService _perplexityTokenGenerator = new();
    private readonly Dictionary<string, bool> _engineUpdateToastShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastEngineErrorShown = new(StringComparer.OrdinalIgnoreCase);
    private readonly QuotaFetchService _quota = new(IPlatformInfo.Current.AuthDirectory);
    private readonly QuotaProviderService _quotaProviders = new();
    private static readonly HashSet<string> QuotaSupportedProviderIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "claude",
        "codex",
        "antigravity",
        "xai",
    };
    private readonly IAgentDetectionService _agentDetection = new AgentDetectionService();
    private readonly AgentConfigurationService _agentConfiguration = new AgentConfigurationService();
    private bool _agentsDetectedOnce;
    private bool _quotaScannedOnce;
    private bool _quotaScanInProgress;

    private CancellationTokenSource? _cliProxyModelFetchCts;
    private CancellationTokenSource? _perplexityModelFetchCts;
    private string? _pendingCliProxyModelOwner;
    private int _pendingCliProxyModelCount;
    private bool _engineReleaseSelectionReady;
    private string? _suppressAutoUpdateForEngineId;
    private readonly AppUpdateService _appUpdate = new();
    private readonly LogsService _logs = new(IPlatformInfo.Current.AuthDirectory);
    private readonly UsageStore _usageStore = new(System.IO.Path.Combine(IPlatformInfo.Current.LocalDataDirectory, "usage.db"));
    private readonly UsageService _usage;
    public LogsViewModel Logs { get; } = new();
    public DashboardViewModel Dashboard { get; } = new();
    public FallbackViewModel Fallback { get; }
    private bool _logsInitialLoadPending;
    private bool _isWindowVisibleForLogs = true;
    private bool _managementKeyRepairAttempted;
    private bool _disposed;

    public static IReadOnlyList<int> LogsRefreshIntervalOptions { get; } = [2, 5, 10, 30];

    private void ConfigureLogsService(int? portOverride = null)
    {
        var runtime = _settings.Current.GetOrAddEngine(EngineCatalog.CliProxyApi.Id, EngineCatalog.CliProxyApi.DefaultPort);
        _logs.Configure(portOverride ?? runtime.Port, _settings.Current.ManagementKey);
        _usage.Configure(portOverride ?? runtime.Port, _settings.Current.ManagementKey);
    }

    public void SetWindowVisibleForLogs(bool visible)
    {
        if (_isWindowVisibleForLogs == visible) return;
        _isWindowVisibleForLogs = visible;
        UpdateLogsPollingState();
    }

private bool AreLogsActive => _isWindowVisibleForLogs &&
SelectedSection is SectionKey.Logs;

    private void UpdateLogsPollingState()
    {
        var effectiveAutoRefresh = _settings.Current.LogsAutoRefresh && AreLogsActive;
        _logs.SetAutoRefresh(effectiveAutoRefresh, _settings.Current.LogsRefreshIntervalSeconds);
    }

    private void OnLogEntriesLoaded(IReadOnlyList<RequestLogEntry> entries, bool isInitial) => Logs.OnEntriesLoaded(entries, isInitial);
    private void OnUsageEventsLoaded(IReadOnlyList<UsageEvent> events)
    {
        Dashboard.OnUsageEventsLoaded(events);
        Logs.OnUsageEventsLoaded(events);
    }
    private void OnRawLogLinesLoaded(IReadOnlyList<string> lines, bool isInitial) => Logs.OnRawLinesLoaded(lines, isInitial);
    private void OnLogsCleared() => Logs.OnCleared();

    public bool LogsAutoRefresh
    {
        get => _settings.Current.LogsAutoRefresh;
        set
        {
            _settings.Current.LogsAutoRefresh = value;
            _settings.Save();
            UpdateLogsPollingState();
            OnPropertyChanged();
        }
    }

    public int LogsRefreshIntervalSeconds
    {
        get => _settings.Current.LogsRefreshIntervalSeconds;
        set
        {
            _settings.Current.LogsRefreshIntervalSeconds = value;
            _settings.Save();
            UpdateLogsPollingState();
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private SectionKey _selectedSection = SectionKey.Home;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private string _focusedConfigEngineId = EngineCatalog.CliProxyApi.Id;
    [ObservableProperty] private string _providersEngineId = EngineCatalog.CliProxyApi.Id;
    [ObservableProperty] private ProviderViewModel? _selectedQuotaProvider;
    [ObservableProperty] private QuotaProviderViewModel? _selectedQuotaAccount;
    [ObservableProperty] private bool _isRefreshingAllQuotaProviders;

    [ObservableProperty] private EngineState _engineState = EngineState.Stopped;
    [ObservableProperty] private string? _installedVersion;
    [ObservableProperty] private string? _latestVersion;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isCheckingForUpdate;
    [ObservableProperty] private bool _configHasBadge;
    [ObservableProperty] private AppUpdateState _appUpdateState = AppUpdateState.Idle;
    [ObservableProperty] private string? _appUpdateNewVersion;
    [ObservableProperty] private bool _showUpdateToast;
    [ObservableProperty] private string _updateToastEngineId = "";
    [ObservableProperty] private string _updateToastText = "";
    [ObservableProperty] private bool _showNoUpdateToast;
    [ObservableProperty] private bool _showAppNoUpdateToast;
    [ObservableProperty] private bool _showManagementKeyRepairedToast;
    [ObservableProperty] private bool _endpointCopied;
    [ObservableProperty] private bool _showUpdateSuccess;
    [ObservableProperty] private bool _showCliProxyUpdateSuccess;
    [ObservableProperty] private bool _showPerplexityUpdateSuccess;
    [ObservableProperty] private string _engineStatusText = "Stopped";

    [ObservableProperty] private bool _showAddAccountDialog;
    [ObservableProperty] private ProviderViewModel? _addAccountTarget;
    [ObservableProperty] private bool _showAddAccountModeDialog;
    [ObservableProperty] private bool _addAccountUseApiKey;
    [ObservableProperty] private bool _showAddCustomProviderDialog;
    [ObservableProperty] private bool _showEditCustomProviderDialog;
    private string? _editingCustomProviderId;
    [ObservableProperty] private string _customProviderNameDraft = "";
    [ObservableProperty] private string _customProviderBaseUrlDraft = "";
    [ObservableProperty] private string _customProviderApiKeyDraft = "";
    [ObservableProperty] private bool _showCustomProviderApiKey;
    [ObservableProperty] private bool _isFetchingCustomProviderModels;
    [ObservableProperty] private bool _showCustomProviderModelsDialog;
    [ObservableProperty] private string _customProviderModelSearch = "";
    // Non-null when the model dialog is editing an existing custom provider; null when adding a new one.
    private string? _editingProviderModelsId;
    partial void OnCustomProviderModelSearchChanged(string value) => ApplyCustomProviderModelFilter();
    [ObservableProperty] private bool _showPerplexityAccountDialog;
    [ObservableProperty] private string _perplexitySessionTokenDraft = "";
    [ObservableProperty] private bool _isPerplexityTokenGenerationMode;
    [ObservableProperty] private bool _isPerplexityTokenFlowBusy;
    [ObservableProperty] private TokenFlowStage _perplexityTokenStage = TokenFlowStage.Email;
    [ObservableProperty] private string _perplexityTokenInputWatermark = "Email, code, or magic link";
    [ObservableProperty] private string _perplexityTokenContinueLabel = "Continue";
    [ObservableProperty] private string _perplexityTokenStepLabel = "Step 1 of 3";
    [ObservableProperty] private string _perplexityTokenPrompt = "";
    [ObservableProperty] private string _perplexityTokenDetail = "";
    [ObservableProperty] private bool _perplexityTokenHasError;
    [ObservableProperty] private string _perplexityGeneratedToken = "";
    [ObservableProperty] private bool _showEditPerplexityLabelDialog;
    [ObservableProperty] private PerplexityAccountViewModel? _editPerplexityLabelTarget;
    [ObservableProperty] private string _editPerplexityLabelDraft = "";
    [ObservableProperty] private bool _showResetPerplexityDialog;

    [ObservableProperty] private bool _showOAuthStatus;
    [ObservableProperty] private bool _oAuthStatusIsError;
    [ObservableProperty] private string _oAuthStatusMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OAuthStatusHasUrl))]
    private string _oAuthStatusUrl = "";

    /// <summary>True when the OAuth status carries a sign-in URL the user can copy.</summary>
    public bool OAuthStatusHasUrl => !string.IsNullOrEmpty(OAuthStatusUrl);

    /// <summary>Briefly true after the user copies the sign-in URL (drives the copy/check icon swap).</summary>
    [ObservableProperty] private bool _oAuthUrlCopied;

    // Provider whose OAuth login is in progress; the toast auto-closes once its
    // account count rises (new account) or its token file is rewritten (re-auth of
    // an existing account), relative to the baseline captured when the flow started.
    private string? _pendingOAuthProviderId;
    private int _pendingOAuthBaselineAccounts;
    private DateTime? _pendingOAuthBaselineWriteUtc;

    private CancellationTokenSource? _oauthStatusDismissCts;

    partial void OnShowOAuthStatusChanged(bool value)
    {
        _oauthStatusDismissCts?.Cancel();
        _oauthStatusDismissCts?.Dispose();
        _oauthStatusDismissCts = null;
        if (!value) return;
        // Keep the toast open when it shows a URL so the user has time to copy it.
        if (OAuthStatusHasUrl) return;

        var cts = new CancellationTokenSource();
        _oauthStatusDismissCts = cts;
        _ = Task.Delay(6000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_oauthStatusDismissCts == cts) ShowOAuthStatus = false;
            });
        }, TaskScheduler.Default);
    }

    [RelayCommand]
    private void DismissOAuthStatus()
    {
        _pendingOAuthProviderId = null;
        ShowOAuthStatus = false;
    }

    [ObservableProperty] private bool _showConfigurationStatus;
    [ObservableProperty] private bool _configurationStatusIsError;
    [ObservableProperty] private string _configurationStatusMessage = "";
    private CancellationTokenSource? _configurationStatusDismissCts;

    partial void OnShowConfigurationStatusChanged(bool value)
    {
        _configurationStatusDismissCts?.Cancel();
        _configurationStatusDismissCts?.Dispose();
        _configurationStatusDismissCts = null;
        if (!value) return;

        var cts = new CancellationTokenSource();
        _configurationStatusDismissCts = cts;
        _ = Task.Delay(6000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (_configurationStatusDismissCts == cts) ShowConfigurationStatus = false;
            });
        }, TaskScheduler.Default);
    }
    [ObservableProperty] private bool _showResetCredentialsDialog;
    [ObservableProperty] private bool _showApiKeysDialog;
    [ObservableProperty] private string _apiKeyDraft = "";
    [ObservableProperty] private bool _showApiKeyDraft;
    [ObservableProperty] private string _addAccountApiKeyDraft = "";
    [ObservableProperty] private string _addAccountBaseUrlDraft = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ApiKeyDialogTitle))]
    [NotifyPropertyChangedFor(nameof(ApiKeyDialogApplyText))]
    private ProviderAccountViewModel? _editApiAccountTarget;
    [ObservableProperty] private bool _showAddAccountApiKey;
    public string ApiKeyDialogTitle => EditApiAccountTarget is null
        ? Localization.GetString("Dialog_AddAccount_ApiKeyTitle")
        : Localization.GetString("Dialog_EditApiKey_Title");
    public string ApiKeyDialogApplyText => EditApiAccountTarget is null
        ? Localization.GetString("Dialog_AddAccount_ApiKeyAdd")
        : Localization.GetString("Dialog_EditApiKey_Save");
    [ObservableProperty] private bool _showPerplexitySessionToken;
    [ObservableProperty] private bool _isModelsExpanded;
    [ObservableProperty] private string _modelSearchText = "";

    [ObservableProperty] private bool _isLoadingEngineReleases;
    [ObservableProperty] private EngineReleaseViewModel? _selectedEngineRelease;
    [ObservableProperty]
    private bool _isDetectingAgents;

    // Agent config dialog state
    [ObservableProperty] private bool _showAgentConfigDialog;
    [ObservableProperty] private AgentViewModel? _agentConfigTarget;
    [ObservableProperty] private bool _isAgentConfigBulkMode;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyAgentConfigCommand))]
    private bool _isApplyingAgentConfig;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowAgentConfigApplyButton))]
    [NotifyPropertyChangedFor(nameof(ShowAgentConfigCopyButton))]
    private bool _isAgentConfigManualMode;
    [ObservableProperty] private bool _isAgentConfigDefaultMode;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AgentConfigHasResult))]
    [NotifyPropertyChangedFor(nameof(AgentConfigSuccess))]
    [NotifyPropertyChangedFor(nameof(ShowAgentConfigApplyButton))]
    [NotifyPropertyChangedFor(nameof(ShowAgentConfigCopyButton))]
    private AgentConfigApplyResult? _agentConfigResult;
    [ObservableProperty] private IReadOnlyList<RawConfigPreview> _agentConfigPreviews = Array.Empty<RawConfigPreview>();
    [ObservableProperty] private IReadOnlyList<AgentConfigItemResult> _agentConfigMultiResults = Array.Empty<AgentConfigItemResult>();
    [ObservableProperty] private string _ampUpstreamApiKeyDraft = "";
    [ObservableProperty] private bool _showAmpUpstreamApiKey;

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();
    public ObservableCollection<ProviderViewModel> StandaloneQuotaProviders { get; } = new();
    public ObservableCollection<QuotaProviderViewModel> QuotaAccounts { get; } = new();
    public ObservableCollection<PerplexityAccountViewModel> PerplexityAccounts { get; } = new();
    public ObservableCollection<AgentViewModel> Agents { get; } = new();
    public ObservableCollection<EngineReleaseViewModel> EngineReleases { get; } = new();
    public ObservableCollection<AvailableModelGroupViewModel> AvailableModelGroups { get; }
    public ObservableCollection<AvailableModelGroupViewModel> CliProxyModelGroups { get; }
    public ObservableCollection<AvailableModelGroupViewModel> PerplexityModelGroups { get; }
    public ObservableCollection<EngineOptionViewModel> EngineOptions { get; } = new();
    public ObservableCollection<CliProxyApiKeyViewModel> CliProxyApiKeys { get; } = new();
    public ObservableCollection<SelectableModelViewModel> SelectableModels { get; } = new();
    private bool _suppressSelectableModelState;
    public ObservableCollection<SelectableModelViewModel> CustomProviderModels { get; } = new();
    private bool _suppressCustomProviderModelState;

    public MainWindowViewModel() : this(new SettingsService(), null!, null!, null!, null!, null!) { }

    public MainWindowViewModel(
        SettingsService settings,
        EngineRegistryService engineRegistry,
        ProviderCatalogService catalog,
        PerplexityAccountCatalogService perplexityAccounts,
        ILaunchAtLoginService? launchAtLogin = null,
        IFolderOpenService? folderOpen = null)
    {
        _localization = LocalizationService.Instance;
        _settings = settings;

        // Resolve language: saved preference → system language → English fallback.
        var languageCode = _settings.Current.Language ?? GetSystemLanguageOrEnglish();
        _localization.SetCulture(languageCode);
        _selectedLanguage = GetSelectedLanguageOption();
        _selectedThemeMode = ThemeModes.First(mode => mode.Value == NormalizeThemeMode(_settings.Current.ThemeMode));
        _selectedRoutingStrategy = RoutingStrategyOptions.First(strategy => strategy.Value == _settings.Current.RoutingStrategy);
        _engineRegistry = engineRegistry ?? new EngineRegistryService(settings);
        Fallback = new FallbackViewModel(_settings, ApplyFallbackChangeAsync);
        _configService = new ConfigService(settings);
        var engineConfig = _configService;
        _catalog = catalog ?? new ProviderCatalogService(settings, engineConfig);
        _perplexityAccounts = perplexityAccounts ?? new PerplexityAccountCatalogService();
        _launchAtLogin = launchAtLogin ?? new LaunchAtLoginService();
        _folderOpen = folderOpen ?? new FolderOpenService();

        CliProxyModelGroups = new ObservableCollection<AvailableModelGroupViewModel>();
        PerplexityModelGroups = new ObservableCollection<AvailableModelGroupViewModel>();
        AvailableModelGroups = new ObservableCollection<AvailableModelGroupViewModel>();

        void OnEngineModelsChanged(object? s, System.Collections.Specialized.NotifyCollectionChangedEventArgs _)
        {
            AvailableModelGroups.Clear();
            foreach (var g in CliProxyModelGroups) AvailableModelGroups.Add(g);
            foreach (var g in PerplexityModelGroups) AvailableModelGroups.Add(g);
            OnPropertyChanged(nameof(FocusedModelGroups));
            OnPropertyChanged(nameof(TotalAvailableModelCount));
            OnPropertyChanged(nameof(HasCliProxySelectableModels));
            OnPropertyChanged(nameof(HasPerplexitySelectableModels));
            RefreshFallbackModelOptions();
        }
        CliProxyModelGroups.CollectionChanged += OnEngineModelsChanged;
        PerplexityModelGroups.CollectionChanged += OnEngineModelsChanged;
        AvailableModelGroups.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TotalAvailableModelCount));
            if (ShowAgentConfigDialog && !AgentConfigHasResult)
                PopulateSelectableModels();
        };
        _modelFetch = new TunnelAgent.Services.ModelFetchService(settings);
        _usage = new UsageService(_usageStore);
        // Seed telemetry-backed views before logs load, so request_id → provider
        // overrides are available when log entries are parsed.
        var persistedUsage = _usageStore.LoadRecent(50_000);
        Dashboard.OnUsageEventsLoaded(persistedUsage);
        Logs.OnUsageEventsLoaded(persistedUsage);
        _logs.ManagementKeyRejected += OnManagementKeyRejected;
        ConfigureLogsService();

        foreach (var engine in _engineRegistry.Engines)
            engine.StateChanged += OnAnyEngineStateChanged;

        _catalog.ProvidersRefreshed    += OnProvidersRefreshed;
        _catalog.ProvidersRebuilt      += OnProvidersRebuilt;
        _catalog.ProviderFirstConnected += OnProviderFirstConnected;
        _perplexityAccounts.AccountsChanged += OnPerplexityAccountsChanged;

        foreach (var definition in EngineCatalog.All)
            EngineOptions.Add(new EngineOptionViewModel(definition));

        Providers.CollectionChanged += (_, _) => RefreshQuotaNavigation();
        StandaloneQuotaProviders.CollectionChanged += (_, _) => RefreshQuotaNavigation();

        var cursorIcon = ProviderIconRegistry.Get("cursor");
        QuotaAccounts.Add(new QuotaProviderViewModel("cursor", "Cursor", cursorIcon.IconKind, cursorIcon.LogoColor, "Cursor AI IDE.", cursorIcon.CustomIconData));
        var kiroIcon = ProviderIconRegistry.Get("kiro");
        QuotaAccounts.Add(new QuotaProviderViewModel("kiro", "Kiro", kiroIcon.IconKind, kiroIcon.LogoColor, "Amazon Kiro AI editor.", kiroIcon.CustomIconData));
        var traeIcon = ProviderIconRegistry.Get("trae");
        QuotaAccounts.Add(new QuotaProviderViewModel("trae", "Trae", traeIcon.IconKind, traeIcon.LogoColor, "ByteDance Trae AI editor.", traeIcon.CustomIconData));

        InitApiKeysFromSettings();
        InitAgentsFromCatalog();

        _logs.EntriesLoaded  += OnLogEntriesLoaded;
        _logs.RawLinesLoaded += OnRawLogLinesLoaded;
        _logs.Cleared        += OnLogsCleared;
        _usage.EventsLoaded  += OnUsageEventsLoaded;
        UpdateLogsPollingState();
        _logsInitialLoadPending = true;
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
    public int QuotaTabIndex =>
        (SelectedQuotaAccount?.Id ?? SelectedQuotaProvider?.Id) switch
        {
            "codex"          => 1,
            "antigravity"    => 2,
            "xai"            => 3,
            "cursor"         => 4,
            "kiro"           => 5,
            "trae"           => 6,
            _                => 0,
        };
    public string ActiveEngineName => FocusedConfigEngine.Definition.DisplayName;
    public string ActiveEngineDescription => FocusedConfigEngine.Definition.Id switch
    {
        "cliproxyapi" => _localization.GetString("Provider_cliproxyapi_Description"),
        "perplexity-webui-scraper" => _localization.GetString("Provider_perplexity-webui-scraper_Description"),
        _ => FocusedConfigEngine.Definition.Description,
    };
    public string EndpointUrl => $"http://127.0.0.1:{Port}";
    public string AppVersion { get; } = TunnelAgent.AppVersion.Current;
    public LocalizationService Localization => _localization;
    public bool IsLaunchAtLoginSupported => _launchAtLogin.IsSupported;
    public int ConnectedProviderCount => Providers.Count(p => p.Connected || p.ActiveAccountCount > 0);
    public IEnumerable<ProviderViewModel> QuotaProviders => Providers.Where(IsQuotaSupportedProvider).Concat(StandaloneQuotaProviders);
    private static IEnumerable<ProviderAccountViewModel> QuotaAccountsFor(ProviderViewModel provider) =>
        provider.Accounts.Where(a => !a.IsCustomKey);
    /// <summary>Quota providers that actually have at least one connected account — used by the tray usage popup.</summary>
    public IEnumerable<ProviderViewModel> QuotaProvidersForRail => QuotaProviders.Where(p => QuotaAccountsFor(p).Any());
    public int QuotaProviderCount => QuotaProviders.Count(p => QuotaAccountsFor(p).Any());
    public IEnumerable<ProviderAccountViewModel> SelectedQuotaAccounts => SelectedQuotaProvider is null ? Enumerable.Empty<ProviderAccountViewModel>() : QuotaAccountsFor(SelectedQuotaProvider);
    public bool HasQuotaProviders => QuotaProviders.Any();
    public bool HasQuotaAccounts => QuotaProviders.Any(p => QuotaAccountsFor(p).Any());
    public bool HasAnyQuotaData => QuotaProviders.SelectMany(QuotaAccountsFor).Any(a => a.HasQuota);
    public bool HasSelectedQuotaAccounts => SelectedQuotaAccounts.Any();
    public bool ShowQuotaAccountEmptyState => HasQuotaProviders && !HasSelectedQuotaAccounts;
    public string QuotaEmptyStateText => HasQuotaProviders
        ? "Select a supported provider with connected accounts, then refresh quota for an account."
        : "Quota tracking is available for Claude, OpenAI Codex, and Antigravity after accounts are connected.";
    public int EnabledAgentCount    => Agents.Count(a => a.Installed && a.Enabled);
    public int InstalledAgentCount   => Agents.Count(a => a.Installed);
    public int ConfiguredAgentCount  => Agents.Count(a => a.Configured);
    public int NotInstalledAgentCount => Agents.Count(a => !a.Installed);
    public IEnumerable<AgentViewModel> InstalledAgents    => Agents.Where(a => a.Installed);
    public IEnumerable<AgentViewModel> NotInstalledAgents => Agents.Where(a => !a.Installed);

    // Proxy URL agents should point to (CLIProxy v1 endpoint)
    public string AgentProxyBaseUrl => CliProxyEndpointUrl + "/v1";
    public string CurrentAgentApiKey => TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY") ?? "";
    public bool AgentConfigHasResult         => AgentConfigResult != null;
    public bool AgentConfigSuccess            => AgentConfigResult?.Success == true;
    public bool ShowAgentConfigApplyButton    => !AgentConfigHasResult && !IsAgentConfigManualMode;
    public bool ShowAgentConfigCopyButton     => !AgentConfigHasResult && IsAgentConfigManualMode;
    public bool ShowAgentConfigAgentPicker    => IsAgentConfigBulkMode;
    public bool ShowSingleAgentSummary        => !IsAgentConfigBulkMode && AgentConfigTarget != null;
    public bool AgentConfigSupportsModelSelection =>
        IsAgentConfigBulkMode ||
        AgentConfigTarget?.Id is not ("codex" or "claude-code" or "amp");
    public bool HasSelectableModels           => SelectableModels.Count > 0;
    public int VisibleSelectableModelCount    => SelectableModels.Count(m => m.IsVisible);
    public bool HasVisibleSelectableModels    => VisibleSelectableModelCount > 0;
    public bool ShowNoModelSearchResults      => HasSelectableModels && !HasVisibleSelectableModels;
    public bool ShowAmpUpstreamApiKeyField    => AgentConfigTarget?.Id == "amp" && !IsAgentConfigDefaultMode;
    public bool? AllVisibleModelsSelected
    {
        get
        {
            var visible = SelectableModels.Where(m => m.IsVisible).ToList();
            if (visible.Count == 0) return false;
            var selected = visible.Count(m => m.IsSelected);
            if (selected == 0) return false;
            return selected == visible.Count ? true : null;
        }
        set
        {
            var select = value != false;
            _suppressSelectableModelState = true;
            try
            {
                foreach (var model in SelectableModels.Where(m => m.IsVisible))
                    model.IsSelected = select;
            }
            finally
            {
                _suppressSelectableModelState = false;
            }
            OnPropertyChanged(nameof(ModelsExpanderLabel));
            OnPropertyChanged(nameof(AllVisibleModelsSelected));
            if (IsAgentConfigManualMode && ShowAgentConfigDialog && !AgentConfigHasResult)
                _ = RefreshManualPreviewAsync();
        }
    }
    public string ModelsExpanderLabel => _localization.GetString(
        "AgentConfigOverlay_ModelsSelectedLabel",
        SelectableModels.Count(m => m.IsSelected),
        SelectableModels.Count);
    public int AgentConfigSelectedCount       => IsAgentConfigBulkMode
        ? Agents.Count(a => a.IsSelectedForConfig && a.Installed)
        : AgentConfigTarget?.Installed == true ? 1 : 0;
    public string AgentConfigApplyLabel => IsAgentConfigBulkMode
        ? _localization.GetString("AgentConfigOverlay_ApplySelectedButton", AgentConfigSelectedCount)
        : _localization.GetString("AgentConfigOverlay_ApplyButton");
    public string AgentConfigDialogTitle => IsAgentConfigBulkMode
        ? _localization.GetString("AgentConfigOverlay_BulkTitle")
        : AgentConfigTarget is { } target
            ? _localization.GetString("AgentConfigOverlay_SingleTitle", target.Name)
            : _localization.GetString("AgentConfigOverlay_SingleFallbackTitle");
    public string AgentConfigDialogDescription => IsAgentConfigBulkMode
        ? _localization.GetString("AgentConfigOverlay_BulkDescription")
        : AgentConfigTarget?.Description ?? "";
    public IEnumerable<AvailableModelGroupViewModel> FocusedModelGroups =>
        IsPerplexityEngineSelected ? PerplexityModelGroups : CliProxyModelGroups;
    public int TotalAvailableModelCount => FocusedModelGroups.Sum(g => g.ModelCount);
    public int PerplexityAccountCount => PerplexityAccounts.Count;
    public bool HasPerplexityAccounts => PerplexityAccounts.Count > 0;
    public string PerplexityEmptyStateText => "Perplexity needs at least one saved WebUI session token account.";
    public string AuthFilesDescription => IsPerplexityEngineSelected
        ? "Perplexity session accounts are stored in app settings."
        : "OAuth tokens and custom provider keys are stored in the app auth folder.";

    private IManagedEngine FocusedConfigEngine => _engineRegistry.Get(FocusedConfigEngineId);
    private IManagedEngine CliProxyEngine => _engineRegistry.Get(EngineCatalog.CliProxyApi.Id);
    private IManagedEngine PerplexityEngine => _engineRegistry.Get(EngineCatalog.PerplexityWebUiScraper.Id);

    public bool EngineAutoStart
    {
        get => _settings.Current.GetOrAddEngine(FocusedConfigEngineId, FocusedConfigEngine.Definition.DefaultPort).AutoStart;
        set
        {
            var runtime = _settings.Current.GetOrAddEngine(FocusedConfigEngineId, FocusedConfigEngine.Definition.DefaultPort);
            if (runtime.AutoStart == value) return;
            runtime.AutoStart = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public int Port
    {
        get => _settings.Current.GetOrAddEngine(FocusedConfigEngineId, FocusedConfigEngine.Definition.DefaultPort).Port;
        set
        {
            var runtime = _settings.Current.GetOrAddEngine(FocusedConfigEngineId, FocusedConfigEngine.Definition.DefaultPort);
            if (runtime.Port == value) return;
            if (IsPortUsedByOtherEngine(value, FocusedConfigEngineId))
            {
                ConfigurationStatusIsError = true;
                ConfigurationStatusMessage = _localization.GetString("ConfigView_PortConflict", value, GetOtherEngineDisplayName(FocusedConfigEngineId));
                ShowConfigurationStatus = true;
                _editablePort = runtime.Port;
                OnPropertyChanged(nameof(EditablePort));
                OnPropertyChanged(nameof(CanApplyPort));
                return;
            }
            runtime.Port = value;
            if (string.Equals(FocusedConfigEngineId, EngineCatalog.CliProxyApi.Id, StringComparison.OrdinalIgnoreCase))
                ConfigureLogsService(value);
            _settings.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EditablePort));
            OnPropertyChanged(nameof(EndpointUrl));
            RefreshEngineSectionProperties();
            _ = ApplyPortChangeAsync();
        }
    }

    private decimal? _editablePort;
    public decimal? EditablePort
    {
        get => _editablePort ?? Port;
        set
        {
            if (SetProperty(ref _editablePort, value))
                OnPropertyChanged(nameof(CanApplyPort));
        }
    }

    public bool CanApplyPort => EditablePort is { } value
        && value == decimal.Truncate(value)
        && value is >= 1 and <= 65535
        && decimal.ToInt32(value) != Port;

    [RelayCommand]
    private void ApplyPort()
    {
        if (!CanApplyPort || EditablePort is null)
            return;

        Port = decimal.ToInt32(EditablePort.Value);
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
            SelectedThemeMode = ThemeModes.First(mode => mode.Value == normalized);
        }
    }

    public IReadOnlyList<ThemeModeOption> ThemeModes => LocalizationService.SupportedThemeModes;

    private ThemeModeOption _selectedThemeMode = LocalizationService.SupportedThemeModes[0];
    public ThemeModeOption SelectedThemeMode
    {
        get => _selectedThemeMode;
        set
        {
            if (value != null && SetProperty(ref _selectedThemeMode, value))
                ThemeMode = value.Value;
        }
    }

    public IReadOnlyList<LanguageOption> SupportedLanguages => LocalizationService.SupportedLanguages;

    private LanguageOption _selectedLanguage;
    public LanguageOption SelectedLanguage
    {
        get => _selectedLanguage;
        set
        {
            if (value != null && SetProperty(ref _selectedLanguage, value))
            {
                var languageCode = value.Code == LocalizationService.SystemLanguageCode
                    ? GetSystemLanguageOrEnglish()
                    : value.Code;
                _localization.SetCulture(languageCode);
                _settings.Current.Language = value.Code == LocalizationService.SystemLanguageCode ? null : value.Code;
                _settings.Save();
                OnPropertyChanged(nameof(ActiveEngineDescription));
                OnPropertyChanged(nameof(AgentConfigApplyLabel));
                OnPropertyChanged(nameof(AgentConfigDialogTitle));
                OnPropertyChanged(nameof(AgentConfigDialogDescription));
                OnPropertyChanged(nameof(ModelsExpanderLabel));
                OnPropertyChanged(nameof(SelectedEngineVersionDescription));
                OnPropertyChanged(nameof(InstalledEngineHashLabel));
                foreach (var mode in ThemeModes)
                    mode.Refresh();
                foreach (var strategy in RoutingStrategyOptions)
                    strategy.Refresh();
                OnPropertyChanged(nameof(SelectedThemeMode));
                OnPropertyChanged(nameof(SelectedRoutingStrategy));
            }
        }
    }

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

    public bool AutoCheckForAppUpdates
    {
        get => _settings.Current.AutoCheckForAppUpdates;
        set
        {
            if (_settings.Current.AutoCheckForAppUpdates == value) return;
            _settings.Current.AutoCheckForAppUpdates = value;
            _settings.Save();
            OnPropertyChanged();
        }
    }

    public bool AutoUpdate
    {
        get => _settings.Current.AutoUpdate;
        set { _settings.Current.AutoUpdate = value; _settings.Save(); OnPropertyChanged(); UpdateBadgeState(); }
    }

    public bool MaskEmails
    {
        get => _settings.Current.MaskEmails;
        set
        {
            if (_settings.Current.MaskEmails == value) return;
            _settings.Current.MaskEmails = value;
            _settings.Save();
            OnPropertyChanged();
            PropagateEmailMasking(value);
        }
    }

    private void PropagateEmailMasking(bool mask)
    {
        foreach (var p in Providers)
            foreach (var a in p.Accounts)
                a.MaskEmails = mask;
        foreach (var p in StandaloneQuotaProviders)
            foreach (var a in p.Accounts)
                a.MaskEmails = mask;
        foreach (var q in QuotaAccounts)
            q.MaskEmails = mask;
    }

    public RoutingStrategy RoutingStrategy
    {
        get => _settings.Current.RoutingStrategy;
        set
        {
            if (_settings.Current.RoutingStrategy == value) return;
            _settings.Current.RoutingStrategy = value;
            _settings.Save();
            OnPropertyChanged();
            SelectedRoutingStrategy = RoutingStrategyOptions.First(strategy => strategy.Value == value);
        }
    }

    public static RoutingStrategy[] RoutingStrategies { get; } = { RoutingStrategy.RoundRobin, RoutingStrategy.FillFirst };

    public IReadOnlyList<RoutingStrategyOption> RoutingStrategyOptions { get; } =
    [
        new RoutingStrategyOption(RoutingStrategy.RoundRobin, "ConfigView_CLIProxy_RoutingStrategy_RoundRobin"),
        new RoutingStrategyOption(RoutingStrategy.FillFirst, "ConfigView_CLIProxy_RoutingStrategy_FillFirst"),
    ];

    private RoutingStrategyOption _selectedRoutingStrategy = null!;
    public RoutingStrategyOption SelectedRoutingStrategy
    {
        get => _selectedRoutingStrategy;
        set
        {
            if (value != null && SetProperty(ref _selectedRoutingStrategy, value))
                RoutingStrategy = value.Value;
        }
    }

    public ServerState ServerState => ToServerState(EngineState);

    /// <summary>Localized last-error reason for the focused engine, shown as the status label
    /// tooltip so the cause stays visible after the transient error toast disappears.</summary>
    public string? FocusedEngineErrorTooltip =>
        FocusedConfigEngine.State == EngineState.Error ? BuildEngineErrorMessage(FocusedConfigEngine) : null;

    public string SelectedEngineVersionDescription => SelectedEngineRelease is null
        ? LocalizationService.Instance.GetString("ConfigView_CLIProxy_EngineVersion_Choose", ActiveEngineName)
        : CanInstallSelectedEngine
            ? LocalizationService.Instance.GetString("ConfigView_CLIProxy_EngineVersion_InstallDescription")
            : LocalizationService.Instance.GetString("ConfigView_CLIProxy_EngineVersion_AlreadyInstalled");

    public string InstalledEngineHashLabel => FocusedConfigEngine.InstalledArchiveSha256 is not null
        ? LocalizationService.Instance.GetString("ConfigView_CLIProxy_Integrity_InstalledPackageSha256")
        : LocalizationService.Instance.GetString("ConfigView_CLIProxy_Integrity_LocalBinarySha256");
    public string InstalledEngineHashShort => ShortHash(FocusedConfigEngine.InstalledArchiveSha256 ?? FocusedConfigEngine.InstalledBinarySha256);
    public string InstalledEngineHashFull => FocusedConfigEngine.InstalledArchiveSha256 ?? FocusedConfigEngine.InstalledBinarySha256 ?? "Not available";
    public string LatestEngineHashShort => ShortHash(FocusedConfigEngine.LatestAssetSha256);
    public string LatestEngineHashFull => FocusedConfigEngine.LatestAssetSha256 ?? "Not available";
    public bool HasEngineIntegrityError => FocusedConfigEngine.IntegrityError is not null;
    public string EngineIntegrityStatus => FocusedConfigEngine.IntegrityError is not null ? "Checksum failed" : FocusedConfigEngine.LatestAssetSha256 is null ? "Checksum pending" : "SHA256 ready";
    public string EngineIntegrityMessage => FocusedConfigEngine.IntegrityError ?? "";
    public string LatestEngineAssetName => FocusedConfigEngine.LatestAssetName ?? "Not available";
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
    public ServerState FallbackProxyServerState =>
        _settings.Current.Fallback.Enabled && CliProxyEngine.State == EngineState.Running
            ? ServerState.Running : ServerState.Stopped;
    public bool IsFallbackBridgeActive => FallbackProxyServerState == ServerState.Running;
    public int CliProxyInternalPort => FallbackProxyService.InternalPortFor(CliProxyPort);

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
        UpdateLogsPollingState();

        if (value == SectionKey.Quota)
        {
            RefreshQuotaNavigation();
            _ = ScanAndRefreshQuotaOnceAsync();
        }
        else if (value == SectionKey.Agents && !_agentsDetectedOnce)
        {
            _ = DetectAgentsAsync();
        }

        // Drive FocusedConfigEngineId from config tab so engine commands/properties resolve correctly.
        if (value == SectionKey.ConfigCliProxy)
            FocusedConfigEngineId = EngineCatalog.CliProxyApi.Id;
        else if (value == SectionKey.ConfigPerplexity)
            FocusedConfigEngineId = EngineCatalog.PerplexityWebUiScraper.Id;
    }

    partial void OnProvidersEngineIdChanged(string value)
    {
        OnPropertyChanged(nameof(IsCliProxyEngineSelected));
        OnPropertyChanged(nameof(IsPerplexityEngineSelected));
        OnPropertyChanged(nameof(ProvidersTabIndex));
        OnPropertyChanged(nameof(FocusedModelGroups));
        OnPropertyChanged(nameof(TotalAvailableModelCount));
    }

    partial void OnFocusedConfigEngineIdChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        RefreshFocusedEngineState();
        _ = LoadEngineReleasesAsync();
        OnPropertyChanged(nameof(ActiveEngineName));
        OnPropertyChanged(nameof(ActiveEngineDescription));
        OnPropertyChanged(nameof(AuthFilesDescription));
        ResetEditablePort();
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

        var runtime = _settings.Current.GetOrAddEngine(FocusedConfigEngineId, FocusedConfigEngine.Definition.DefaultPort);
        var latestTag = FocusedConfigEngine.LatestVersion ?? EngineReleases.FirstOrDefault(r => !r.IsPrerelease)?.TagName;
        runtime.PreferredVersion = VersionsEqual(value.TagName, latestTag) ? string.Empty : value.TagName;
        _settings.Save();
    }

    private void ResetEditablePort()
    {
        _editablePort = Port;
        OnPropertyChanged(nameof(EditablePort));
        OnPropertyChanged(nameof(CanApplyPort));
    }

    private bool IsPortUsedByOtherEngine(int port, string currentEngineId) => _engineRegistry.Engines.Any(engine =>
        !string.Equals(engine.Definition.Id, currentEngineId, StringComparison.OrdinalIgnoreCase) &&
        _settings.Current.GetOrAddEngine(engine.Definition.Id, engine.Definition.DefaultPort).Port == port);

    private string GetOtherEngineDisplayName(string currentEngineId) => _engineRegistry.Engines
        .FirstOrDefault(engine => !string.Equals(engine.Definition.Id, currentEngineId, StringComparison.OrdinalIgnoreCase))
        ?.Definition.DisplayName ?? _localization.GetString("ConfigView_OtherEngine");

    private static string NormalizeThemeMode(string? value) => value?.ToLowerInvariant() switch
    {
        "light" => "light",
        "dark" => "dark",
        _ => "system"
    };

    private LanguageOption GetSelectedLanguageOption()
    {
        if (_settings.Current.Language is null)
            return LocalizationService.SupportedLanguages[0];

        return LocalizationService.SupportedLanguages.FirstOrDefault(l => l.Code == _settings.Current.Language)
            ?? LocalizationService.SupportedLanguages[0];
    }

    /// <summary>
    /// Resolves the system language to a supported culture, falling back to English.
    /// Tries an exact match first (e.g. es-ES), then a two-letter prefix match (es-* → es-ES).
    /// </summary>
    private string GetSystemLanguageOrEnglish()
    {
        var systemCulture = _systemCulture;
        var supported = LocalizationService.SupportedLanguages;

        var exactMatch = supported.FirstOrDefault(l =>
            l.Code.Equals(systemCulture.Name, StringComparison.OrdinalIgnoreCase));
        if (exactMatch != null)
            return exactMatch.Code;

        var languagePrefix = systemCulture.TwoLetterISOLanguageName;
        var prefixMatch = supported.FirstOrDefault(l =>
            !string.IsNullOrEmpty(l.Code) && l.Code.StartsWith(languagePrefix + "-", StringComparison.OrdinalIgnoreCase));
        if (prefixMatch != null)
            return prefixMatch.Code;

        // Fallback a inglés
        return "en-US";
    }

    public async Task InitializeAsync()
    {
        try
        {
            await Task.Run(_settings.LoadAsync);
            var languageCode = _settings.Current.Language ?? GetSystemLanguageOrEnglish();
            _localization.SetCulture(languageCode);
            _selectedLanguage = GetSelectedLanguageOption();
            OnPropertyChanged(nameof(SelectedLanguage));

            _ = ObserveStartupTaskAsync(Task.Run(ReconcileLaunchAtLoginAsync));
            NormalizeActiveEngineSetting();
            RefreshSettingsBindings();

            var legacyPerplexityAccounts = _settings.Current.PerplexityAccounts.ToList();
            var catalogTask = Task.Run(_catalog.InitializeAsync);
            var perplexityAccountsTask = Task.Run(() => _perplexityAccounts.InitializeAsync(legacyPerplexityAccounts));
            await Task.WhenAll(catalogTask, perplexityAccountsTask);

            RebindProviders();
            OnPropertyChanged(nameof(ConnectedProviderCount));
            RefreshQuotaNavigation();

            // Clear legacy Perplexity accounts from settings after migration so they don't persist there anymore.
            if (_settings.Current.PerplexityAccounts.Count > 0)
            {
                _settings.Current.PerplexityAccounts.Clear();
                await _settings.SaveImmediateAsync();
            }
            ReloadPerplexityAccounts();

            // Warm non-default sections early without constructing their views.
            // This keeps first navigation to Quota/Agents responsive while startup UI stays lazy.
            _ = ObserveStartupTaskAsync(ScanAndRefreshQuotaOnceAsync());
            _ = ObserveStartupTaskAsync(DetectAgentsAsync());
            _ = ObserveStartupTaskAsync(WarmModelPricingAsync());

            var engineInitTasks = _engineRegistry.Engines
                .Select(engine => ObserveStartupTaskAsync(Task.Run(engine.InitializeAsync)))
                .ToArray();
            await Task.WhenAll(engineInitTasks);

            // AutoStart: start engines configured to launch automatically.
            // Keep this sequential to avoid multiple process starts and config writes contending at startup.
            foreach (var engine in _engineRegistry.Engines)
            {
                var runtime = _settings.Current.GetOrAddEngine(engine.Definition.Id, engine.Definition.DefaultPort);
                if (!runtime.AutoStart || engine.State == EngineState.Running) continue;
                try { await Task.Run(() => engine.StartAsync()); }
                catch (Exception ex) { TraceStartupWarning($"Engine autostart failed for {engine.Definition.Id}", ex); }
            }

            // Kick off model fetch for any engine already Running after init.
            foreach (var engine in _engineRegistry.Engines)
            {
                if (engine.State != EngineState.Running) continue;
                var isCliProxy = string.Equals(engine.Definition.Id, EngineCatalog.CliProxyApi.Id, StringComparison.OrdinalIgnoreCase);
                if (isCliProxy)
                {
                    _cliProxyModelFetchCts = new CancellationTokenSource();
                    _ = ObserveStartupTaskAsync(Task.Run(() =>
                        _modelFetch.FetchAndApplyAsync(CliProxyModelGroups, engine.Port, engine.Definition.Id, _cliProxyModelFetchCts.Token)));
                    // Engine already running at startup — configure management API and start log polling if needed.
                    ConfigureLogsService(engine.Port);
                    _logs.SetManagementApiAvailable(true);
                    _usage.SetManagementApiAvailable(true);
                    if (_logsInitialLoadPending && AreLogsActive)
                    {
                        _logsInitialLoadPending = false;
                        _logs.Start();
                        _usage.Start();
                    }
                    else
                    {
                        UpdateLogsPollingState();
                    }
                }
                else
                {
                    _perplexityModelFetchCts = new CancellationTokenSource();
                    _ = ObserveStartupTaskAsync(Task.Run(() =>
                        _modelFetch.FetchAndApplyAsync(PerplexityModelGroups, engine.Port, engine.Definition.Id, _perplexityModelFetchCts.Token)));
                }
            }

            RefreshFocusedEngineState();
            PropagateEmailMasking(MaskEmails);

            // Kick the initial log/usage load for the section we land on at startup
            // (Home by default). OnSelectedSectionChanged does not fire for the
            // initial value, so without this the dashboard stays empty until the
            // user navigates away and back.
            EnsureInitialLogsLoad();

            _ = ObserveStartupTaskAsync(LoadEngineReleasesAsync());
        }
        catch (Exception ex)
        {
            TraceStartupWarning("Startup initialization failed", ex);
        }
    }

    private void EnsureInitialLogsLoad()
    {
        if (!_logsInitialLoadPending || !AreLogsActive) return;
        _logsInitialLoadPending = false;
        ConfigureLogsService(CliProxyEngine.Port);
        _logs.Start();
        _usage.Start();
    }

    private static async Task ObserveStartupTaskAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception ex) { TraceStartupWarning("Background startup task failed", ex); }
    }

    private static void TraceStartupWarning(string message, Exception ex) =>
        Trace.TraceWarning($"{message}: {ex}");

    private void NormalizeActiveEngineSetting()
    {
        ProvidersEngineId = EngineCatalog.CliProxyApi.Id;
        FocusedConfigEngineId = EngineCatalog.CliProxyApi.Id;
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
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(ConnectedProviderCount));
            RefreshQuotaNavigation();
            PropagateEmailMasking(MaskEmails);
            MaybeCompletePendingOAuth();
        });

    private void OnProvidersRebuilt(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() =>
        {
            RebindProviders();
            OnPropertyChanged(nameof(ConnectedProviderCount));
            RefreshQuotaNavigation();
            PropagateEmailMasking(MaskEmails);
            MaybeCompletePendingOAuth();
        });

    /// <summary>
    /// Restart the running CLIProxyAPI so a custom-provider config change is fully applied.
    /// A rename re-keys the provider; CLIProxyAPI's hot-reload cannot re-bind the existing auth
    /// to the new name, so /v1/models only reflects it after a restart. The restart drives the
    /// engine through Stopped→Running, which deterministically re-fetches the model list.
    /// No-op when the engine is not running.
    /// </summary>
    private async Task RestartCliProxyIfRunningAsync()
    {
        var engine = CliProxyEngine;
        if (engine.State != EngineState.Running) return;
        try
        {
            _lastEngineErrorShown.Remove(engine.Definition.Id);
            await engine.StopAsync();
            await engine.StartAsync();
        }
        catch (Exception ex)
        {
            TraceStartupWarning("Failed to restart CLIProxyAPI after custom provider edit", ex);
        }
    }

    private async Task FetchPendingCliProxyModelsAsync(IManagedEngine engine, CancellationToken token, string expectedOwner, int expectedModelCount)
    {
        await _modelFetch.FetchAndApplyAsync(CliProxyModelGroups, engine.Port, engine.Definition.Id, token, expectedOwner, expectedModelCount);
        if (!token.IsCancellationRequested && string.Equals(_pendingCliProxyModelOwner, expectedOwner, StringComparison.Ordinal) && _pendingCliProxyModelCount == expectedModelCount)
        {
            _pendingCliProxyModelOwner = null;
            _pendingCliProxyModelCount = 0;
        }
    }

    private async Task RestartCliProxyIfRunningAndWaitForModelsAsync(string expectedOwner, int expectedModelCount)
    {
        _pendingCliProxyModelOwner = expectedOwner;
        _pendingCliProxyModelCount = expectedModelCount;
        await RestartCliProxyIfRunningAsync();
        if (CliProxyEngine.State == EngineState.Running)
        {
            _cliProxyModelFetchCts?.Cancel();
            _cliProxyModelFetchCts = new CancellationTokenSource();
            _ = FetchPendingCliProxyModelsAsync(CliProxyEngine, _cliProxyModelFetchCts.Token, expectedOwner, expectedModelCount);
        }
    }

    private void RebindProviders()
    {
        foreach (var existing in Providers)
            existing.AddAccountRequested -= OnAddAccountRequested;
        Providers.Clear();
        foreach (var vm in _catalog.Providers)
        {
            vm.AddAccountRequested += OnAddAccountRequested;
            Providers.Add(vm);
        }
    }

    private void OnProviderFirstConnected(object? sender, string providerId)
    {
        if (!QuotaSupportedProviderIds.Contains(providerId)) return;
        // Snapshot accounts on the UI thread before handing off to background
        var provider = Providers.FirstOrDefault(p => p.Id == providerId)
                    ?? StandaloneQuotaProviders.FirstOrDefault(p => p.Id == providerId);
        if (provider is null) return;
        var accounts = QuotaAccountsFor(provider).ToList();
        _ = Task.Run(async () =>
        {
            foreach (var account in accounts)
                await _quota.FetchAccountPublicAsync(providerId, account);
            await Dispatcher.UIThread.InvokeAsync(RefreshQuotaNavigation);
        });
    }

    private static bool IsQuotaSupportedProvider(ProviderViewModel provider) =>
        QuotaSupportedProviderIds.Contains(provider.Id);

    private void RefreshQuotaNavigation()
    {
        var supportedProviders = QuotaProviders.ToList();
        if (SelectedQuotaAccount is null && (SelectedQuotaProvider is null || !supportedProviders.Contains(SelectedQuotaProvider)))
            SelectedQuotaProvider = supportedProviders.FirstOrDefault();
        else
            UpdateQuotaSelectionFlags();

        OnPropertyChanged(nameof(QuotaProviders));
        OnPropertyChanged(nameof(QuotaProvidersForRail));
        OnPropertyChanged(nameof(QuotaProviderCount));
        OnPropertyChanged(nameof(SelectedQuotaAccounts));
        OnPropertyChanged(nameof(HasQuotaProviders));
        OnPropertyChanged(nameof(HasQuotaAccounts));
        OnPropertyChanged(nameof(HasAnyQuotaData));
        OnPropertyChanged(nameof(HasSelectedQuotaAccounts));
        OnPropertyChanged(nameof(ShowQuotaAccountEmptyState));
        OnPropertyChanged(nameof(QuotaEmptyStateText));
    }

    private void OnAnyEngineStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (sender is IManagedEngine engine)
            {
                var isCliProxy = string.Equals(engine.Definition.Id, EngineCatalog.CliProxyApi.Id, StringComparison.OrdinalIgnoreCase);
                var modelGroups = isCliProxy ? CliProxyModelGroups : PerplexityModelGroups;
                ref var cts = ref isCliProxy ? ref _cliProxyModelFetchCts : ref _perplexityModelFetchCts;

                if (engine.State == EngineState.Running)
                {
                    // A successful (re)start clears the last error so a future failure re-toasts.
                    _lastEngineErrorShown.Remove(engine.Definition.Id);
                    if (cts is null || cts.IsCancellationRequested)
                    {
                        cts = new CancellationTokenSource();
                        if (isCliProxy && _pendingCliProxyModelOwner is { } owner && _pendingCliProxyModelCount > 0)
                        {
                            _ = FetchPendingCliProxyModelsAsync(engine, cts.Token, owner, _pendingCliProxyModelCount);
                        }
                        else
                        {
                            _ = _modelFetch.FetchAndApplyAsync(modelGroups, engine.Port, engine.Definition.Id, cts.Token);
                        }
                    }
                    if (isCliProxy)
                    {
                        ConfigureLogsService(engine.Port);
                        _logs.SetManagementApiAvailable(true);
                        _usage.SetManagementApiAvailable(true);
                        if (_logsInitialLoadPending && AreLogsActive)
                        {
                            _logsInitialLoadPending = false;
                            _logs.Start();
                            _usage.Start();
                        }
                        else
                        {
                            UpdateLogsPollingState();
                        }
                    }
                }
                else if (engine.State == EngineState.Stopped || engine.State == EngineState.Error)
                {
                    cts?.Cancel();
                    cts = null;
                    modelGroups.Clear();
                    if (isCliProxy)
                    {
                        _logs.SetManagementApiAvailable(false);
                        _logs.Stop();
                        _usage.SetManagementApiAvailable(false);
                        _usage.Stop();
                        _logsInitialLoadPending = true;
                    }

                    // Surface engine failures (e.g. port already in use) as a localized error
                    // toast so the user understands why and can retry. Show once per Error transition.
                    if (engine.State == EngineState.Error)
                    {
                        var message = BuildEngineErrorMessage(engine);
                        if (!string.IsNullOrWhiteSpace(message)
                            && !string.Equals(_lastEngineErrorShown.GetValueOrDefault(engine.Definition.Id), message, StringComparison.Ordinal))
                        {
                            _lastEngineErrorShown[engine.Definition.Id] = message;
                            ConfigurationStatusIsError = true;
                            ConfigurationStatusMessage = message;
                            ShowConfigurationStatus = true;
                        }
                    }
                    else
                    {
                        _lastEngineErrorShown.Remove(engine.Definition.Id);
                    }
                }

                // Refresh focused state when the active config engine changes
                if (string.Equals(engine.Definition.Id, FocusedConfigEngineId, StringComparison.OrdinalIgnoreCase))
                    RefreshFocusedEngineState();

                // Show update toast for any engine as soon as an update is detected (once per session)
                if (engine.UpdateAvailable && !_engineUpdateToastShown.GetValueOrDefault(engine.Definition.Id))
                {
                    if (string.Equals(_suppressAutoUpdateForEngineId, engine.Definition.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _suppressAutoUpdateForEngineId = null;
                    }
                    else
                    {
                        _engineUpdateToastShown[engine.Definition.Id] = true;
                        if (AutoUpdate)
                            _ = _engineRegistry.Get(engine.Definition.Id).DownloadAndInstallAsync();
                        else
                        {
                            UpdateToastEngineId = engine.Definition.Id;
                            UpdateToastText = _localization.GetString(
                                "Toast_EngineUpdateAvailable_Body", engine.Definition.DisplayName, engine.LatestVersion ?? string.Empty);
                            ShowUpdateToast = true;
                            _ = Task.Delay(8000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowUpdateToast = false));
                        }
                    }
                }
            }

            RefreshEngineSectionProperties();
        });
    }

    private string? BuildEngineErrorMessage(IManagedEngine engine)
    {
        return engine.LastErrorKind switch
        {
            EngineErrorKind.PortInUse    => _localization.GetString("Toast_EngineError_PortInUse", engine.Port, engine.Definition.DisplayName),
            EngineErrorKind.Timeout      => _localization.GetString("Toast_EngineError_Timeout", engine.Definition.DisplayName),
            EngineErrorKind.LaunchFailed => _localization.GetString("Toast_EngineError_LaunchFailed", engine.Definition.DisplayName),
            EngineErrorKind.Crashed      => _localization.GetString("Toast_EngineError_Crashed", engine.Definition.DisplayName),
            // None: fall back to the raw engine message (e.g. integrity errors).
            _                            => engine.LastError
        };
    }

    private void RefreshFocusedEngineState()
    {
        EngineState = FocusedConfigEngine.State;
        InstalledVersion = FocusedConfigEngine.InstalledVersion;
        LatestVersion = FocusedConfigEngine.LatestVersion;
        DownloadProgress = FocusedConfigEngine.DownloadProgress;
        UpdateAvailable = FocusedConfigEngine.UpdateAvailable;
        EngineStatusText = BuildEngineStatusText(FocusedConfigEngine);
        UpdateBadgeState();
        RefreshEngineSectionProperties();
        OnPropertyChanged(nameof(ServerState));
        OnPropertyChanged(nameof(FocusedEngineErrorTooltip));
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
        OnPropertyChanged(nameof(FallbackProxyServerState));
        OnPropertyChanged(nameof(IsFallbackBridgeActive));
        OnPropertyChanged(nameof(CliProxyInternalPort));
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
        OnPropertyChanged(nameof(CanApplyPort));
        OnPropertyChanged(nameof(EngineAutoStart));
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

    private void UpdateBadgeState()
    {
        var anyEngineUpdateAvailable = _engineRegistry.Engines.Any(engine => engine.UpdateAvailable);
        ConfigHasBadge = (anyEngineUpdateAvailable && !AutoUpdate) || AppUpdateAvailable;
    }

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
        OnPropertyChanged(nameof(EngineAutoStart));
        OnPropertyChanged(nameof(RoutingStrategy));
        OnPropertyChanged(nameof(MaskEmails));
        UpdateBadgeState();
    }

    private async Task PrepareSelectedEngineReleaseAsync(string version)
    {
        try { await FocusedConfigEngine.PrepareVersionAsync(version); }
        catch { }
    }

    private async Task LoadEngineReleasesAsync()
    {
        IsLoadingEngineReleases = true;
        try
        {
            var releases = await FocusedConfigEngine.ListReleasesAsync();
            EngineReleases.Clear();
            foreach (var release in releases)
                EngineReleases.Add(new EngineReleaseViewModel(release));

            var runtime = _settings.Current.GetOrAddEngine(FocusedConfigEngineId, FocusedConfigEngine.Definition.DefaultPort);
            var preferred = runtime.PreferredVersion;
            var selected = EngineReleases.FirstOrDefault(r => VersionsEqual(r.TagName, preferred))
                ?? EngineReleases.FirstOrDefault(r => VersionsEqual(r.TagName, FocusedConfigEngine.LatestVersion))
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
        var engine = FocusedConfigEngine;
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

    /// <summary>
    /// Runs the OAuth login flow for a provider and surfaces the result in the shared status toast.
    /// Used by both the provider card button and the "add account" dialog so every path shows feedback.
    /// </summary>
    public async Task ConnectOAuthAsync(string providerId)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == providerId);
        _pendingOAuthBaselineAccounts = provider?.Accounts.Count(a => !a.IsCustomKey) ?? 0;
        _pendingOAuthBaselineWriteUtc = _catalog.LatestOAuthTokenWriteUtc(providerId);
        if (provider is not null) provider.IsConnecting = true;
        try
        {
            var result = await _catalog.ConnectOAuthAsync(providerId);

            OAuthStatusUrl = result.Status == OAuthConnectStatus.BrowserOpenedWithUrl ? result.Detail : "";
            ShowOAuthStatus = false;
            OAuthStatusIsError = !result.Success;
            OAuthStatusMessage = LocalizeOAuthResult(result);
            ShowOAuthStatus = true;

            // Watch the auth dir: once a new token lands for this provider we
            // auto-dismiss the toast (see MaybeCompletePendingOAuth). Check once now
            // in case a fast re-auth already rewrote the token during startup.
            _pendingOAuthProviderId = result.Success ? providerId : null;
            if (result.Success) MaybeCompletePendingOAuth();
        }
        finally
        {
            if (provider is not null) provider.IsConnecting = false;
        }
    }

    /// <summary>Closes the OAuth toast once the awaited provider gains a new account (login completed).</summary>
    private void MaybeCompletePendingOAuth()
    {
        if (_pendingOAuthProviderId is null) return;
        var provider = Providers.FirstOrDefault(p => p.Id == _pendingOAuthProviderId);
        if (provider is null) return;

        // New account added, or an existing account's token was rewritten (re-auth).
        var gainedAccount = provider.Accounts.Count(a => !a.IsCustomKey) > _pendingOAuthBaselineAccounts;
        var latestWrite   = _catalog.LatestOAuthTokenWriteUtc(_pendingOAuthProviderId);
        var tokenRewritten = latestWrite is { } w &&
            (_pendingOAuthBaselineWriteUtc is not { } b || w > b);
        if (!gainedAccount && !tokenRewritten) return;

        _pendingOAuthProviderId = null;
        ShowOAuthStatus = false;
    }

    private string LocalizeOAuthResult(OAuthConnectResult result) => result.Status switch
    {
        OAuthConnectStatus.BrowserOpened        => Localization.GetString("OAuth_Status_BrowserOpened"),
        OAuthConnectStatus.BrowserOpenedWithUrl => Localization.GetString("OAuth_Status_BrowserOpenedWithUrl"),
        OAuthConnectStatus.NotSupported         => Localization.GetString("OAuth_Status_NotSupported", result.Detail),
        OAuthConnectStatus.BinaryMissing        => Localization.GetString("OAuth_Status_BinaryMissing"),
        OAuthConnectStatus.StartFailed          => Localization.GetString("OAuth_Status_StartFailed", result.Detail),
        OAuthConnectStatus.Failed               => Localization.GetString("OAuth_Status_Failed", result.Detail),
        OAuthConnectStatus.FailedUnexpected     => Localization.GetString("OAuth_Status_FailedUnexpected"),
        _                                       => result.Detail,
    };

    public void DisconnectOAuth(string providerId) => _catalog.DisconnectOAuth(providerId);

    public Task RefreshQuotaAsync(ProviderAccountViewModel account)
    {
        var provider = Providers.FirstOrDefault(p => p.Accounts.Contains(account))
                    ?? StandaloneQuotaProviders.FirstOrDefault(p => p.Accounts.Contains(account));
        return provider is not null ? _quota.FetchAccountPublicAsync(provider.Id, account) : Task.CompletedTask;
    }

    public async Task RefreshAllQuotaProvidersAsync()
    {
        if (IsRefreshingAllQuotaProviders) return;

        IsRefreshingAllQuotaProviders = true;
        try
        {
            foreach (var account in QuotaProviders.SelectMany(QuotaAccountsFor).ToList())
                await RefreshQuotaAsync(account);
        }
        finally
        {
            IsRefreshingAllQuotaProviders = false;
            RefreshQuotaNavigation();
        }
    }

    private void OnAddAccountRequested(object? sender, EventArgs e)
    {
        if (sender is ProviderViewModel vm)
        {
            AddAccountTarget = vm;
            AddAccountApiKeyDraft = "";
            AddAccountBaseUrlDraft = vm.ApiKeyBaseUrl;
            EditApiAccountTarget = null;
            ShowAddAccountApiKey = false;
            AddAccountUseApiKey = vm.SupportsApiKey && !vm.SupportsOAuth;
            ShowAddAccountModeDialog = vm.HasMultipleAddModes;
            ShowAddAccountDialog = !vm.HasMultipleAddModes;
        }
    }

    public async Task ConfirmAddAccountAsync(string providerId, string baseUrl, string apiKey)
    {
        ShowAddAccountDialog = false;
        AddAccountApiKeyDraft = "";
        AddAccountBaseUrlDraft = "";
        ShowAddAccountApiKey = false;
        var kind = AddAccountUseApiKey ? ProviderCatalogService.GetDefaultKind(providerId) : ProviderKind.OpenAICompatibility;
        var edited = EditApiAccountTarget;
        if (edited is not null && edited.ApiKey != apiKey)
        {
            await _catalog.RemoveAccountAsync(edited.ProviderId, edited.ApiKey);
        }
        var created = await _catalog.AddAccountAsync(providerId, baseUrl, apiKey, null, kind);
        if (edited is not null && edited.ApiKey == apiKey)
        {
            edited.ProviderBaseUrl = baseUrl;
            created = true;
        }
        EditApiAccountTarget = null;
        if (!created)
        {
            OAuthStatusIsError = true;
            OAuthStatusMessage = Localization.GetString("Dialog_AddAccount_DuplicateApiKey");
            ShowOAuthStatus = true;
        }
        OnPropertyChanged(nameof(ConnectedProviderCount));
        PropagateEmailMasking(MaskEmails);
    }

    public async Task ConfirmAddPerplexityAccountAsync(string? label, string sessionToken)
    {
        ShowPerplexityAccountDialog = false;
        ShowPerplexitySessionToken = false;
        PerplexitySessionTokenDraft = "";
        await _perplexityAccounts.AddAsync(label, sessionToken);
    }

    [RelayCommand]
    private void ShowAddPerplexityAccount()
    {
        ShowPerplexityAccountDialog = true;
        ShowPerplexitySessionToken = false;
        IsPerplexityTokenGenerationMode = false;
        IsPerplexityTokenFlowBusy = false;
        PerplexityTokenStage = TokenFlowStage.Email;
        PerplexityTokenStepLabel = "Step 1 of 3";
        PerplexityTokenInputWatermark = "Email, code, or magic link";
        PerplexityTokenContinueLabel = "Continue";
        PerplexityTokenPrompt = "";
        PerplexityTokenDetail = "";
        PerplexityTokenHasError = false;
        PerplexityGeneratedToken = "";
        PerplexitySessionTokenDraft = "";
    }

    [RelayCommand]
    public async Task DismissPerplexityAccountDialog()
    {
        ShowPerplexityAccountDialog = false;
        ShowPerplexitySessionToken = false;
        IsPerplexityTokenGenerationMode = false;
        IsPerplexityTokenFlowBusy = false;
        PerplexityTokenStage = TokenFlowStage.Email;
        PerplexityTokenStepLabel = "Step 1 of 3";
        PerplexityTokenInputWatermark = "Email, code, or magic link";
        PerplexityTokenContinueLabel = "Continue";
        PerplexityTokenPrompt = "";
        PerplexityTokenDetail = "";
        PerplexityTokenHasError = false;
        PerplexityGeneratedToken = "";
        PerplexitySessionTokenDraft = "";
        await _perplexityTokenGenerator.DisposeAsync();
    }

    public async Task StartPerplexityTokenFlowAsync()
    {
        await _perplexityTokenGenerator.DisposeAsync();
        PerplexityTokenStage = TokenFlowStage.Email;
        PerplexityTokenStepLabel = "Step 1 of 3";
        PerplexityTokenInputWatermark = "Enter your Perplexity email";
        PerplexityTokenContinueLabel = "Send code";
        PerplexityGeneratedToken = "";
        PerplexityTokenHasError = false;
        PerplexityTokenPrompt = "Starting token generator…";
        PerplexityTokenDetail = "This may take a few seconds.";
        IsPerplexityTokenFlowBusy = true;
        IsPerplexityTokenGenerationMode = true;

        var update = await _perplexityTokenGenerator.StartAsync();
        IsPerplexityTokenFlowBusy = false;

        if (update.Stage == TokenFlowStage.Failed)
        {
            PerplexityTokenStage = TokenFlowStage.Failed;
            PerplexityTokenStepLabel = "";
            PerplexityTokenContinueLabel = "Retry";
            PerplexityTokenHasError = true;
            PerplexityTokenPrompt = "Token generation failed";
            PerplexityTokenDetail = FormatPerplexityTokenError(update.Detail ?? update.Prompt);
            return;
        }

        PerplexityTokenStage = update.Stage;
        PerplexityTokenStepLabel = update.Stage == TokenFlowStage.Email ? "Step 1 of 3" : "Step 2 of 3";
        PerplexityTokenInputWatermark = update.Stage == TokenFlowStage.Email ? "Enter your Perplexity email" : "Enter code or paste magic link";
        PerplexityTokenContinueLabel = update.Stage == TokenFlowStage.Email ? "Send code" : "Verify";
        PerplexityTokenHasError = false;
        PerplexityTokenPrompt = update.Prompt;
        PerplexityTokenDetail = update.Detail ?? "";
    }

    public async Task SubmitPerplexityTokenFlowAsync(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return;

        var trimmedInput = input.Trim();
        var currentStage = PerplexityTokenStage;
        if (currentStage == TokenFlowStage.Email && !IsValidEmail(trimmedInput))
        {
            PerplexityTokenStage = TokenFlowStage.Email;
            PerplexityTokenStepLabel = "Step 1 of 3";
            PerplexityTokenInputWatermark = "Enter your Perplexity email";
            PerplexityTokenContinueLabel = "Send code";
            PerplexityTokenHasError = true;
            PerplexityTokenPrompt = "Invalid email address";
            PerplexityTokenDetail = "Enter a valid Perplexity account email before requesting a verification code.";
            return;
        }

        if (currentStage == TokenFlowStage.Totp && !IsValidTotp(trimmedInput))
        {
            PerplexityTokenStage = TokenFlowStage.Totp;
            PerplexityTokenStepLabel = "Step 3 of 3";
            PerplexityTokenInputWatermark = "Enter 6-digit authenticator code";
            PerplexityTokenContinueLabel = "Verify";
            PerplexityTokenHasError = true;
            PerplexityTokenPrompt = "Invalid two-factor code";
            PerplexityTokenDetail = "Enter the 6-digit code from your authenticator app.";
            return;
        }

        if (currentStage == TokenFlowStage.Email)
        {
            PerplexityTokenStage = TokenFlowStage.Verification;
            PerplexityTokenStepLabel = "Step 2 of 3";
            PerplexityTokenInputWatermark = "Enter code or paste magic link";
            PerplexityTokenContinueLabel = "Verify";
            PerplexityTokenPrompt = "Enter 6-digit code or paste magic link";
            PerplexityTokenDetail = "Check your email for Perplexity verification message.";
            PerplexityTokenHasError = false;
        }

        IsPerplexityTokenFlowBusy = true;
        var update = await _perplexityTokenGenerator.SubmitAsync(trimmedInput, currentStage);
        IsPerplexityTokenFlowBusy = false;

        if (update.Stage == TokenFlowStage.Success && !string.IsNullOrWhiteSpace(update.Token))
        {
            PerplexityTokenStage = TokenFlowStage.Success;
            PerplexityTokenStepLabel = "";
            PerplexityTokenInputWatermark = "Enter code or paste magic link";
            PerplexityTokenContinueLabel = "Verify";
            PerplexityGeneratedToken = update.Token;
            PerplexitySessionTokenDraft = update.Token;
            PerplexityTokenHasError = false;
            PerplexityTokenPrompt = "Token generated successfully";
            PerplexityTokenDetail = "Review label in previous dialog and save account.";
            return;
        }

        if (update.Stage == TokenFlowStage.Failed)
        {
            PerplexityTokenStage = TokenFlowStage.Failed;
            PerplexityTokenContinueLabel = currentStage == TokenFlowStage.Email ? "Send code" : "Verify";
            PerplexityTokenHasError = true;
            PerplexityTokenPrompt = "Token generation failed";
            PerplexityTokenDetail = FormatPerplexityTokenError(update.Detail ?? update.Prompt);
            return;
        }

        if (update.Stage == TokenFlowStage.Totp)
        {
            PerplexityTokenStage = TokenFlowStage.Totp;
            PerplexityTokenStepLabel = "Step 3 of 3";
            PerplexityTokenInputWatermark = "Enter 6-digit authenticator code";
            PerplexityTokenContinueLabel = "Verify";
            PerplexityTokenHasError = false;
            PerplexityTokenPrompt = update.Prompt;
            PerplexityTokenDetail = update.Detail ?? "Use the 6-digit code from your authenticator app.";
            return;
        }

        // Never regress: once user sent email, stay in Verification even if parser echoes Email prompt
        if (update.Stage == TokenFlowStage.Email && currentStage != TokenFlowStage.Email)
        {
            PerplexityTokenHasError = false;
            PerplexityTokenDetail = update.Prompt;
            return;
        }

        // Don't re-prompt Verification: if scraper echoes the same prompt, keep user's input intact
        if (update.Stage == TokenFlowStage.Verification && currentStage == TokenFlowStage.Verification)
        {
            PerplexityTokenHasError = false;
            PerplexityTokenDetail = update.Detail ?? update.Prompt;
            return;
        }

        PerplexityTokenStage = update.Stage;
        PerplexityTokenStepLabel = update.Stage == TokenFlowStage.Email ? "Step 1 of 3" : "Step 2 of 3";
        PerplexityTokenInputWatermark = update.Stage == TokenFlowStage.Email ? "Enter your Perplexity email" : "Enter code or paste magic link";
        PerplexityTokenContinueLabel = update.Stage == TokenFlowStage.Email ? "Send code" : "Verify";
        PerplexityTokenHasError = false;
        PerplexityTokenPrompt = update.Prompt;
        PerplexityTokenDetail = update.Detail ?? "";
    }

    private static bool IsValidTotp(string value) =>
        value.Length == 6 && value.All(char.IsDigit);

    private static bool IsValidEmail(string value)
    {
        try
        {
            var address = new MailAddress(value);
            return string.Equals(address.Address, value, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string FormatPerplexityTokenError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown token generation error.";

        if (raw.Contains("Invalid email address", StringComparison.OrdinalIgnoreCase))
            return "Enter a valid Perplexity account email before requesting a verification code.";

        if (raw.Contains("Authentication successful, but token not found", StringComparison.OrdinalIgnoreCase))
        {
            return "Perplexity accepted the code, but did not return the session cookie. If your engine build is older, update it so the token generator can ask for the TOTP code. You can also paste the browser cookie manually: __Secure-next-auth.session-token.";
        }

        return raw
            .Replace("⛔ Error:", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Press ENTER to exit...", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    [RelayCommand]
    public async Task CancelPerplexityTokenFlowAsync()
    {
        IsPerplexityTokenGenerationMode = false;
        PerplexityTokenStage = TokenFlowStage.Email;
        PerplexityTokenStepLabel = "Step 1 of 3";
        PerplexityTokenInputWatermark = "Email, code, or magic link";
        PerplexityTokenContinueLabel = "Continue";
        PerplexityTokenPrompt = "";
        PerplexityTokenDetail = "";
        PerplexityTokenHasError = false;
        PerplexityGeneratedToken = "";
        await _perplexityTokenGenerator.DisposeAsync();
    }
    [RelayCommand]
    private void DismissAddAccountDialog()
    {
        ShowAddAccountDialog = false;
        ShowAddAccountModeDialog = false;
        AddAccountApiKeyDraft = "";
        AddAccountBaseUrlDraft = "";
        EditApiAccountTarget = null;
        ShowAddAccountApiKey = false;
    }

    [RelayCommand]
    private void OpenAddCustomProvider()
    {
        CustomProviderNameDraft = "";
        CustomProviderBaseUrlDraft = "";
        CustomProviderApiKeyDraft = "";
        ShowCustomProviderApiKey = false;
        ShowAddCustomProviderDialog = true;
    }

    [RelayCommand]
    private void DismissAddCustomProvider()
    {
        ShowAddCustomProviderDialog = false;
        CustomProviderNameDraft = "";
        CustomProviderBaseUrlDraft = "";
        CustomProviderApiKeyDraft = "";
        ShowCustomProviderApiKey = false;
    }

    [RelayCommand] private void ToggleCustomProviderApiKeyVisibility() => ShowCustomProviderApiKey = !ShowCustomProviderApiKey;

    [RelayCommand]
    private void OpenEditCustomProvider(ProviderViewModel provider)
    {
        if (provider is null || !provider.IsCustomProvider) return;
        _editingCustomProviderId = provider.Id;
        CustomProviderNameDraft = provider.Name;
        CustomProviderBaseUrlDraft = provider.ApiKeyBaseUrl;
        CustomProviderApiKeyDraft = provider.Accounts.FirstOrDefault(a => a.IsCustomKey)?.ApiKey ?? "";
        ShowCustomProviderApiKey = false;
        ShowEditCustomProviderDialog = true;
    }

    [RelayCommand]
    private void DismissEditCustomProvider()
    {
        ShowEditCustomProviderDialog = false;
        _editingCustomProviderId = null;
        ResetCustomProviderDrafts();
    }

    [RelayCommand]
    private async Task ConfirmEditCustomProvider()
    {
        if (IsFetchingCustomProviderModels) return;
        if (_editingCustomProviderId is not { } editId) return;

        var name = CustomProviderNameDraft.Trim();
        var baseUrl = CustomProviderBaseUrlDraft.Trim();
        var apiKey = CustomProviderApiKeyDraft.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey)) return;

        // Probe the upstream /models endpoint, mirroring the add flow. A non-200 (or
        // unreachable) URL aborts the save and surfaces an error toast.
        _customProviderModelFetchCts?.Cancel();
        _customProviderModelFetchCts = new CancellationTokenSource();
        IsFetchingCustomProviderModels = true;
        ShowOAuthStatus = false;
        var result = await _upstreamModelFetch.FetchAsync(baseUrl, apiKey, _customProviderModelFetchCts.Token);
        IsFetchingCustomProviderModels = false;

        if (!result.Success)
        {
            // Close the modal so the top-right status toast (rendered under the overlay) is visible.
            ShowEditCustomProviderDialog = false;
            OAuthStatusIsError = true;
            OAuthStatusMessage = _localization.GetString("Dialog_CustomProviderModels_FetchError");
            ShowOAuthStatus = true;
            return;
        }

        ShowEditCustomProviderDialog = false;
        var expectedModelCount = Providers.FirstOrDefault(p => p.Id == editId)?.Models.Count ?? 0;
        var updatedId = await _catalog.UpdateCustomProviderAsync(editId, name, baseUrl, apiKey);
        _editingCustomProviderId = null;
        ResetCustomProviderDrafts();

        // A rename re-keys the provider in the config; CLIProxyAPI's hot-reload can't re-bind the
        // auth to the new name, so the Available Models list (from /v1/models) only updates after
        // a restart. URL/key edits are picked up by hot-reload and don't change the model listing.
        if (updatedId != editId)
            await RestartCliProxyIfRunningAndWaitForModelsAsync(updatedId, expectedModelCount);
    }

    [RelayCommand]
    private async Task ConfirmAddCustomProvider()
    {
        if (IsFetchingCustomProviderModels) return;

        var name = CustomProviderNameDraft.Trim();
        var baseUrl = CustomProviderBaseUrlDraft.Trim();
        var apiKey = CustomProviderApiKeyDraft.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey)) return;

        // Probe the upstream /models endpoint. A non-200 (or unreachable) URL aborts the add
        // and surfaces an error toast — the /v1/models listing is an OpenAI-compatible standard.
        _customProviderModelFetchCts?.Cancel();
        _customProviderModelFetchCts = new CancellationTokenSource();
        IsFetchingCustomProviderModels = true;
        ShowOAuthStatus = false;
        var result = await _upstreamModelFetch.FetchAsync(baseUrl, apiKey, _customProviderModelFetchCts.Token);
        IsFetchingCustomProviderModels = false;

        if (!result.Success)
        {
            // Close the modal so the top-right status toast (rendered under the overlay) is visible.
            ShowAddCustomProviderDialog = false;
            OAuthStatusIsError = true;
            OAuthStatusMessage = result.Error ?? _localization.GetString("Dialog_CustomProviderModels_FetchError");
            ShowOAuthStatus = true;
            return;
        }

        SetEditingProviderModelsId(null);
        PopulateCustomProviderModels(result.Models, name, []);

        ShowAddCustomProviderDialog = false;
        ShowCustomProviderModelsDialog = true;
        RaiseCustomProviderModelState();
    }

    [RelayCommand]
    private async Task EditProviderModels(ProviderViewModel provider)
    {
        if (provider is null || IsFetchingCustomProviderModels) return;

        var baseUrl = provider.ApiKeyBaseUrl;
        var apiKey = provider.Accounts.FirstOrDefault(a => a.IsCustomKey)?.ApiKey ?? "";
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            OAuthStatusIsError = true;
            OAuthStatusMessage = _localization.GetString("Dialog_CustomProviderModels_FetchError");
            ShowOAuthStatus = true;
            return;
        }

        _customProviderModelFetchCts?.Cancel();
        _customProviderModelFetchCts = new CancellationTokenSource();
        IsFetchingCustomProviderModels = true;
        provider.IsFetchingModels = true;
        ShowOAuthStatus = false;
        UpstreamModelsResult result;
        try
        {
            result = await _upstreamModelFetch.FetchAsync(baseUrl, apiKey, _customProviderModelFetchCts.Token);
        }
        finally
        {
            IsFetchingCustomProviderModels = false;
            provider.IsFetchingModels = false;
        }

        if (!result.Success)
        {
            OAuthStatusIsError = true;
            OAuthStatusMessage = result.Error ?? _localization.GetString("Dialog_CustomProviderModels_FetchError");
            ShowOAuthStatus = true;
            return;
        }

        SetEditingProviderModelsId(provider.Id);
        PopulateCustomProviderModels(result.Models, provider.Name, provider.Models);

        ShowCustomProviderModelsDialog = true;
        RaiseCustomProviderModelState();
    }

    private void PopulateCustomProviderModels(IReadOnlyList<string> models, string providerName, IReadOnlyList<string> preselected)
    {
        var selectedSet = new HashSet<string>(preselected, StringComparer.Ordinal);
        ClearCustomProviderModels();
        foreach (var model in models)
        {
            var vm = new SelectableModelViewModel(model, providerName) { IsSelected = selectedSet.Contains(model) };
            vm.PropertyChanged += OnCustomProviderModelPropertyChanged;
            CustomProviderModels.Add(vm);
        }
        CustomProviderModelSearch = "";
        ApplyCustomProviderModelFilter();
    }

    [RelayCommand]
    private async Task ConfirmCustomProviderModels()
    {
        var selected = CustomProviderModels.Where(m => m.IsSelected).Select(m => m.Name).ToList();
        if (selected.Count == 0) return;

        if (_editingProviderModelsId is { } editId)
        {
            ShowCustomProviderModelsDialog = false;
            await _catalog.UpdateCustomProviderModelsAsync(editId, selected);
            SetEditingProviderModelsId(null);
            ClearCustomProviderModels();
            // Restart so the changed model list is reflected in /v1/models (Available Models),
            // then wait until this provider's models have re-registered before applying.
            await RestartCliProxyIfRunningAndWaitForModelsAsync(editId, selected.Count);
            return;
        }

        var name = CustomProviderNameDraft.Trim();
        var baseUrl = CustomProviderBaseUrlDraft.Trim();
        var apiKey = CustomProviderApiKeyDraft.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(baseUrl) || string.IsNullOrEmpty(apiKey)) return;

        ShowCustomProviderModelsDialog = false;
        await _catalog.AddCustomProviderAsync(name, baseUrl, apiKey, selected);
        ResetCustomProviderDrafts();
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    [RelayCommand]
    private void DismissCustomProviderModels()
    {
        ShowCustomProviderModelsDialog = false;
        ClearCustomProviderModels();
        if (_editingProviderModelsId is null) ResetCustomProviderDrafts();
        SetEditingProviderModelsId(null);
    }

    public IEnumerable<SelectableModelViewModel> VisibleCustomProviderModels =>
        CustomProviderModels.Where(m => m.IsVisible);
    public int CustomProviderModelCount => CustomProviderModels.Count;
    public int SelectedCustomProviderModelCount => CustomProviderModels.Count(m => m.IsSelected);
    public bool IsEditingCustomProviderModels => _editingProviderModelsId is not null;
    public bool HasCustomProviderModels => CustomProviderModels.Count > 0;
    public bool HasVisibleCustomProviderModels => CustomProviderModels.Any(m => m.IsVisible);
    public bool ShowNoCustomProviderModelResults => HasCustomProviderModels && !HasVisibleCustomProviderModels;
    public bool CanConfirmCustomProviderModels => SelectedCustomProviderModelCount > 0;
    public string CustomProviderModelsSelectedLabel => _localization.GetString(
        "Dialog_CustomProviderModels_SelectedLabel", SelectedCustomProviderModelCount, CustomProviderModelCount);
    public bool? AllVisibleCustomProviderModelsSelected
    {
        get
        {
            var visible = CustomProviderModels.Where(m => m.IsVisible).ToList();
            if (visible.Count == 0) return false;
            var selected = visible.Count(m => m.IsSelected);
            if (selected == 0) return false;
            return selected == visible.Count ? true : null;
        }
        set
        {
            var select = value != false;
            _suppressCustomProviderModelState = true;
            try
            {
                foreach (var model in CustomProviderModels.Where(m => m.IsVisible))
                    model.IsSelected = select;
            }
            finally
            {
                _suppressCustomProviderModelState = false;
            }
            RaiseCustomProviderModelState();
        }
    }

    private void ApplyCustomProviderModelFilter()
    {
        foreach (var model in CustomProviderModels)
            model.IsVisible = model.Matches(CustomProviderModelSearch);
        RaiseCustomProviderModelState();
    }

    private void SetEditingProviderModelsId(string? providerId)
    {
        _editingProviderModelsId = providerId;
        OnPropertyChanged(nameof(IsEditingCustomProviderModels));
    }

    private void RaiseCustomProviderModelState()
    {
        OnPropertyChanged(nameof(VisibleCustomProviderModels));
        OnPropertyChanged(nameof(CustomProviderModelCount));
        OnPropertyChanged(nameof(SelectedCustomProviderModelCount));
        OnPropertyChanged(nameof(HasCustomProviderModels));
        OnPropertyChanged(nameof(HasVisibleCustomProviderModels));
        OnPropertyChanged(nameof(ShowNoCustomProviderModelResults));
        OnPropertyChanged(nameof(CanConfirmCustomProviderModels));
        OnPropertyChanged(nameof(CustomProviderModelsSelectedLabel));
        OnPropertyChanged(nameof(AllVisibleCustomProviderModelsSelected));
    }

    private void OnCustomProviderModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableModelViewModel.IsSelected)) return;
        if (_suppressCustomProviderModelState) return;
        RaiseCustomProviderModelState();
    }

    private void ClearCustomProviderModels()
    {
        foreach (var vm in CustomProviderModels)
            vm.PropertyChanged -= OnCustomProviderModelPropertyChanged;
        CustomProviderModels.Clear();
        RaiseCustomProviderModelState();
    }

    private void ResetCustomProviderDrafts()
    {
        CustomProviderNameDraft = "";
        CustomProviderBaseUrlDraft = "";
        CustomProviderApiKeyDraft = "";
        CustomProviderModelSearch = "";
        ShowCustomProviderApiKey = false;
    }

    [RelayCommand]
    private async Task RemoveCustomProvider(ProviderViewModel provider)
    {
        if (provider is null) return;
        await _catalog.RemoveCustomProviderAsync(provider.Id);
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    [RelayCommand]
    private void EditApiAccount(ProviderAccountViewModel account)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == account.ProviderId)
            ?? StandaloneQuotaProviders.FirstOrDefault(p => p.Id == account.ProviderId);
        AddAccountTarget = provider;
        EditApiAccountTarget = account;
        AddAccountUseApiKey = true;
        AddAccountApiKeyDraft = account.ApiKey;
        AddAccountBaseUrlDraft = provider?.ApiKeyBaseUrl ?? "";
        ShowAddAccountApiKey = false;
        ShowAddAccountModeDialog = false;
        ShowAddAccountDialog = true;
    }

    [RelayCommand]
    private void AddAccountWithApiKey()
    {
        AddAccountUseApiKey = true;
        ShowAddAccountModeDialog = false;
        ShowAddAccountDialog = true;
    }

    [RelayCommand]
    private async Task AddAccountWithOAuth()
    {
        if (AddAccountTarget is null) return;
        ShowAddAccountModeDialog = false;
        await ConnectOAuthAsync(AddAccountTarget.Id);
    }

    [RelayCommand] private void ToggleApiKeyDraftVisibility() => ShowApiKeyDraft = !ShowApiKeyDraft;
    [RelayCommand] private void ToggleAddAccountApiKeyVisibility() => ShowAddAccountApiKey = !ShowAddAccountApiKey;
    [RelayCommand] private void TogglePerplexitySessionTokenVisibility() => ShowPerplexitySessionToken = !ShowPerplexitySessionToken;
    [RelayCommand] private void ToggleAmpUpstreamApiKeyVisibility() => ShowAmpUpstreamApiKey = !ShowAmpUpstreamApiKey;

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

    [RelayCommand]
    public void EditPerplexityAccountLabel(PerplexityAccountViewModel account)
    {
        EditPerplexityLabelTarget = account;
        EditPerplexityLabelDraft = account.Label;
        ShowEditPerplexityLabelDialog = true;
    }

    [RelayCommand]
    public void DismissEditPerplexityLabelDialog()
    {
        ShowEditPerplexityLabelDialog = false;
        EditPerplexityLabelTarget = null;
        EditPerplexityLabelDraft = "";
    }

    [RelayCommand]
    public async Task ConfirmEditPerplexityLabelAsync()
    {
        if (EditPerplexityLabelTarget is null) return;

        await _perplexityAccounts.UpdateLabelAsync(EditPerplexityLabelTarget.Id, EditPerplexityLabelDraft);
        DismissEditPerplexityLabelDialog();
    }

    [RelayCommand]
    private void ResetPort()
    {
        Port = FocusedConfigEngine.Definition.DefaultPort;
        ResetEditablePort();
    }

    [RelayCommand]
    private void FocusCliProxy()
    {
        ProvidersEngineId = EngineCatalog.CliProxyApi.Id;
        FocusedConfigEngineId = EngineCatalog.CliProxyApi.Id;
    }

    [RelayCommand]
    private void FocusPerplexity()
    {
        ProvidersEngineId = EngineCatalog.PerplexityWebUiScraper.Id;
        FocusedConfigEngineId = EngineCatalog.PerplexityWebUiScraper.Id;
    }



    private async Task ScanAndRefreshQuotaAsync()
    {
        await ScanQuotaProvidersAsync();
        await RefreshAllQuotaProvidersAsync();
    }

    // Warm model pricing from disk (instant, offline) then refresh from OpenRouter when
    // stale, refreshing dashboard cost figures after each step.
    private async Task WarmModelPricingAsync()
    {
        void Refresh() => Dispatcher.UIThread.Post(Dashboard.OnPricingUpdated);
        await TunnelAgent.Services.OpenRouterContextService.Instance.WarmAsync(Refresh);
    }

    private async Task ScanAndRefreshQuotaOnceAsync()
    {
        if (_quotaScannedOnce || _quotaScanInProgress) return;
        _quotaScanInProgress = true;
        try
        {
            await ScanAndRefreshQuotaAsync();
            _quotaScannedOnce = true;
        }
        catch { }
        finally
        {
            _quotaScanInProgress = false;
        }
    }

    [RelayCommand]
    private Task ScanQuotaProviders() => ScanQuotaProvidersAsync();

    public async Task ScanQuotaProvidersAsync()
    {
        foreach (var vm in QuotaAccounts) vm.IsScanning = true;
        try
        {
            var result = await _quotaProviders.ScanAsync();
            await Dispatcher.UIThread.InvokeAsync(() => ApplyQuotaScanResult(result));
        }
        finally
        {
            foreach (var vm in QuotaAccounts) vm.IsScanning = false;
        }
    }

    private void ApplyQuotaScanResult(QuotaScanResult result)
    {
        ApplySingleQuotaProvider("cursor", "Cursor", result.Cursor);
        ApplySingleQuotaProvider("kiro", "Kiro", result.Kiro);
        ApplySingleQuotaProvider("trae", "Trae", result.Trae);
        RefreshQuotaNavigation();
        PropagateEmailMasking(MaskEmails);
    }

    private void ApplySingleQuotaProvider(string id, string name, QuotaProviderInfo info)
    {
        var icon      = ProviderIconRegistry.Get(id);
        var accountVm = QuotaAccounts.FirstOrDefault(v => v.Id == id);
        if (accountVm is null)
        {
            accountVm = new QuotaProviderViewModel(id, name, icon.IconKind, icon.LogoColor, $"{name} standalone quota.", icon.CustomIconData);
            QuotaAccounts.Add(accountVm);
        }
        accountVm.IsDetected = info.IsDetected;
        accountVm.Email      = info.Email;
        accountVm.PlanType   = info.PlanType;

        if (info.IsDetected)
        {
            var existing = StandaloneQuotaProviders.FirstOrDefault(p => p.Id == id);
            if (existing is null)
            {
                existing = new ProviderViewModel(id, name, icon.IconKind, icon.LogoColor,
                    $"{name} standalone quota.", isOAuth: false, customIconData: icon.CustomIconData);
                StandaloneQuotaProviders.Add(existing);
            }

            var label = string.IsNullOrEmpty(info.Email) ? name : info.Email;
            var existingAcct = existing.Accounts.FirstOrDefault();
            if (existingAcct is null || existingAcct.Email != info.Email)
            {
                existing.Accounts.Clear();
                var acct = new ProviderAccountViewModel(id, "", label, isDisabled: false)
                {
                    Email = info.Email,
                };
                existing.Accounts.Add(acct);
                existing.RefreshAccountCount();
            }
        }
        else
        {
            var existing = StandaloneQuotaProviders.FirstOrDefault(p => p.Id == id);
            if (existing is not null)
                StandaloneQuotaProviders.Remove(existing);
        }
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
    public async Task ConfirmResetPerplexityAccountsAsync()
    {
        ShowResetPerplexityDialog = false;
        await _perplexityAccounts.RemoveAllAsync();
        ConfigurationStatusIsError = false;
        ConfigurationStatusMessage = "Perplexity session accounts removed.";
        ShowConfigurationStatus = true;
    }

    [RelayCommand] private void ResetAllCredentials() => ShowResetCredentialsDialog = true;
    [RelayCommand] private void DismissResetCredentialsDialog() => ShowResetCredentialsDialog = false;

    [RelayCommand]
    private async Task ConfirmResetCredentialsAsync()
    {
        ShowResetCredentialsDialog = false;
        await _catalog.ResetAllCredentialsAsync();
        ConfigurationStatusIsError = false;
        ConfigurationStatusMessage = "Tunnel Agent-managed credentials were backed up and removed.";
        ShowConfigurationStatus = true;
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    [RelayCommand] private void DismissConfigurationStatus() => ShowConfigurationStatus = false;

    [RelayCommand]
    private void SelectHome()
    {
        SelectedSection = SectionKey.Home;
        if (_logsInitialLoadPending && AreLogsActive)
        {
            _logsInitialLoadPending = false;
            ConfigureLogsService(CliProxyEngine.Port);
            _logs.Start();
            _usage.Start();
        }
        else
        {
            UpdateLogsPollingState();
        }
    }

    [RelayCommand]
    private void SelectProviders()
    {
        SelectedSection = SectionKey.Providers;
        FocusedConfigEngineId = ProvidersEngineId;
    }

    [RelayCommand]
    private void SelectCliProxyProviders()
    {
        FocusCliProxy();
        SelectedSection = SectionKey.Providers;
    }

    [RelayCommand]
    private void SelectPerplexityProviders()
    {
        FocusPerplexity();
        SelectedSection = SectionKey.Providers;
    }

    [RelayCommand]
    private void SelectQuota()
    {
        SelectedSection = SectionKey.Quota;
        FocusedConfigEngineId = EngineCatalog.CliProxyApi.Id;
    }

    /// <summary>True when the tray usage popup is showing the Home view (engines + aggregated usage) instead of a single provider's quota.</summary>
    [ObservableProperty] private bool _trayHomeSelected = true;

    [RelayCommand]
    private void SelectQuotaProvider(ProviderViewModel provider)
    {
        if (!IsQuotaSupportedProvider(provider) && !StandaloneQuotaProviders.Contains(provider)) return;
        TrayHomeSelected = false;
        SelectedQuotaProvider = provider;
    }

    [RelayCommand]
    private void SelectQuotaProviderById(string providerId)
    {
        var provider = Providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase))
                    ?? StandaloneQuotaProviders.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is not null) { SelectQuotaProvider(provider); return; }

        // Cursor/Kiro/Trae not yet detected — add a placeholder so navigation works
        var quotaAccount = QuotaAccounts.FirstOrDefault(q => string.Equals(q.Id, providerId, StringComparison.OrdinalIgnoreCase));
        if (quotaAccount is null) return;
        var icon = ProviderIconRegistry.Get(providerId);
        var placeholder = new ProviderViewModel(quotaAccount.Id, quotaAccount.Name, icon.IconKind, icon.LogoColor, quotaAccount.Description, isOAuth: false, icon.CustomIconData);
        StandaloneQuotaProviders.Add(placeholder);
        SelectQuotaProvider(placeholder);
    }

    [RelayCommand] private Task RefreshAllQuotaProviders() => RefreshAllQuotaProvidersAsync();

    [RelayCommand]
    private void SelectAgents() => SelectedSection = SectionKey.Agents;

    [RelayCommand]
    private void SelectFallback() => SelectedSection = SectionKey.Fallback;

    private void RefreshFallbackModelOptions()
    {
        var options = CliProxyModelGroups
            .SelectMany(g => g.Models.Select(m =>
                new FallbackModelOption(g.ProviderId, g.ProviderName, m.Name)));
        Fallback.SetAvailableModels(options);
    }

    /// <summary>
    /// Restarts CLIProxyAPI when fallback activation changes so the bridge can take over
    /// (or release) the public port. No-op when the engine is not running.
    /// </summary>
    private async Task ApplyFallbackChangeAsync()
    {
        var engine = CliProxyEngine;
        if (engine.State is not (EngineState.Running or EngineState.Starting)) return;

        await engine.StopAsync();
        await engine.StartAsync();
        ConfigureLogsService(engine.Port);
        OnPropertyChanged(nameof(FallbackProxyServerState));
        OnPropertyChanged(nameof(IsFallbackBridgeActive));
    }

    [RelayCommand]
    private void SelectLogs()
    {
        SelectedSection = SectionKey.Logs;
        if (_logsInitialLoadPending && AreLogsActive)
        {
            _logsInitialLoadPending = false;
            ConfigureLogsService(CliProxyEngine.Port);
            _logs.Start();
            _usage.Start();
        }
        else
        {
            UpdateLogsPollingState();
        }
    }

    [RelayCommand] private void ClearLogs()  => _logs.Clear();
    [RelayCommand] private Task RefreshLogsAsync() => Logs.RefreshWithSpinAsync(() => _logs.TriggerManualRefresh());
    [RelayCommand] private void ShowDeleteLogsConfirm()    => Logs.ShowClearConfirm = true;
    [RelayCommand] private void DismissDeleteLogsConfirm() => Logs.ShowClearConfirm = false;
    [RelayCommand] private void ShowClearUsageConfirm()    => Logs.ShowClearUsageConfirm = true;
    [RelayCommand] private void DismissClearUsageConfirm() => Logs.ShowClearUsageConfirm = false;
    [RelayCommand]
    private void ConfirmClearUsage()
    {
        Logs.ShowClearUsageConfirm = false;
        _usageStore.Clear();
        Dashboard.OnCleared();
        Logs.OnUsageCleared();
    }
    [RelayCommand]
    private async Task ConfirmDeleteLogsAsync()
    {
        Logs.ShowClearConfirm = false;
        var deleted = await _logs.DeleteLogFileAsync();
        if (deleted) _logs.ResetAndClear();
    }

    [RelayCommand]
    private async Task OpenAgentConfigAsync(AgentViewModel vm)
    {
        foreach (var a in Agents) a.IsSelectedForConfig = false;
        IsAgentConfigBulkMode   = false;
        AgentConfigTarget       = vm;
        AgentConfigResult       = null;
        AmpUpstreamApiKeyDraft  = vm.Id == "amp" ? await _configService.GetAmpUpstreamApiKeyAsync() : "";
        ShowAmpUpstreamApiKey   = false;
        AgentConfigMultiResults = Array.Empty<AgentConfigItemResult>();
        IsAgentConfigManualMode = false;
        IsAgentConfigDefaultMode = false;
        IsApplyingAgentConfig   = false;
        AgentConfigPreviews     = Array.Empty<RawConfigPreview>();
        IsModelsExpanded        = false;
        ModelSearchText         = "";
        PopulateSelectableModels();
        ShowAgentConfigDialog   = true;
        OnPropertyChanged(nameof(ShowAgentConfigAgentPicker));
        OnPropertyChanged(nameof(ShowSingleAgentSummary));
        OnPropertyChanged(nameof(AgentConfigSupportsModelSelection));
        OnPropertyChanged(nameof(AgentConfigSelectedCount));
        OnPropertyChanged(nameof(AgentConfigApplyLabel));
        OnPropertyChanged(nameof(AgentConfigDialogTitle));
        OnPropertyChanged(nameof(AgentConfigDialogDescription));
        OnPropertyChanged(nameof(ShowAmpUpstreamApiKeyField));
    }

    [RelayCommand]
    private void OpenBulkAgentConfig()
    {
        foreach (var a in Agents) a.IsSelectedForConfig = false;
        IsAgentConfigBulkMode   = true;
        AgentConfigTarget       = null;
        AgentConfigResult       = null;
        AgentConfigMultiResults = Array.Empty<AgentConfigItemResult>();
        IsAgentConfigManualMode = false;
        IsAgentConfigDefaultMode = false;
        IsApplyingAgentConfig   = false;
        AgentConfigPreviews     = Array.Empty<RawConfigPreview>();
        IsModelsExpanded        = false;
        ModelSearchText         = "";
        PopulateSelectableModels();
        ShowAgentConfigDialog   = true;
        OnPropertyChanged(nameof(ShowAgentConfigAgentPicker));
        OnPropertyChanged(nameof(ShowSingleAgentSummary));
        OnPropertyChanged(nameof(AgentConfigSupportsModelSelection));
        OnPropertyChanged(nameof(AgentConfigSelectedCount));
        OnPropertyChanged(nameof(AgentConfigApplyLabel));
        OnPropertyChanged(nameof(AgentConfigDialogTitle));
        OnPropertyChanged(nameof(AgentConfigDialogDescription));
    }

    [RelayCommand]
    private void DismissAgentConfig()
    {
        ShowAgentConfigDialog = false;
        IsAgentConfigBulkMode = false;
        AgentConfigTarget = null;
        AgentConfigResult = null;
        AgentConfigMultiResults = Array.Empty<AgentConfigItemResult>();
        AgentConfigPreviews = Array.Empty<RawConfigPreview>();
        foreach (var a in Agents) a.IsSelectedForConfig = false;
        ModelSearchText = "";
        ClearSelectableModels();
        OnPropertyChanged(nameof(ShowAgentConfigAgentPicker));
        OnPropertyChanged(nameof(ShowSingleAgentSummary));
        OnPropertyChanged(nameof(AgentConfigSelectedCount));
        OnPropertyChanged(nameof(AgentConfigApplyLabel));
        OnPropertyChanged(nameof(AgentConfigDialogTitle));
        OnPropertyChanged(nameof(AgentConfigDialogDescription));
    }

    [RelayCommand] private void ToggleModels() => IsModelsExpanded = !IsModelsExpanded;

    partial void OnModelSearchTextChanged(string value) => ApplyModelFilter();

    private void ApplyModelFilter()
    {
        foreach (var model in SelectableModels)
            model.IsVisible = model.Matches(ModelSearchText);
        OnPropertyChanged(nameof(VisibleSelectableModelCount));
        OnPropertyChanged(nameof(HasVisibleSelectableModels));
        OnPropertyChanged(nameof(ShowNoModelSearchResults));
        OnPropertyChanged(nameof(AllVisibleModelsSelected));
    }

    [RelayCommand] private void SetAgentAutoMode() => IsAgentConfigManualMode = false;
    [RelayCommand]
    private void SetAgentManualMode()
    {
        IsAgentConfigManualMode = true;
        _ = RefreshManualPreviewAsync();
    }
    [RelayCommand] private void SetAgentProxyMode()   => IsAgentConfigDefaultMode = false;
    [RelayCommand] private void SetAgentDefaultMode() => IsAgentConfigDefaultMode = true;

    [RelayCommand(CanExecute = nameof(CanApplyAgentConfig))]
    private async Task ApplyAgentConfigAsync()
    {
        if (IsAgentConfigManualMode) return;
        var targets = GetAgentConfigTargets();
        if (targets.Count == 0) return;

        IsApplyingAgentConfig = true;
        AgentConfigResult = null;

        // Write Amp upstream API key directly to proxy-config.yaml
        if (AgentConfigTarget?.Id == "amp" || targets.Any(t => t.Id == "amp"))
            await _configService.SetAmpUpstreamApiKeyAsync(AmpUpstreamApiKeyDraft.Trim()).ConfigureAwait(false);

        var models       = GetSelectedModels();
        var modelEntries = await GetSelectedModelEntriesAsync().ConfigureAwait(false);
        var itemResults  = new List<AgentConfigItemResult>();
        try
        {
            foreach (var target in targets)
            {
                var def = FindDef(target.Id);
                var r = IsAgentConfigDefaultMode
                    ? await Task.Run(() => _agentConfiguration.Revert(def)).ConfigureAwait(false)
                    : await _agentConfiguration.ApplyAsync(def, AgentProxyBaseUrl, CurrentAgentApiKey, models, modelEntries).ConfigureAwait(false);
                var displayPath = r.ConfigPath;
                if (r.RawPreviews.Count > 0)
                {
                    var extraPaths = r.RawPreviews.Select(p => p.TargetPath).Where(p => p != r.ConfigPath && !string.IsNullOrEmpty(p));
                    var joined = string.Join(Environment.NewLine, new[] { r.ConfigPath }.Concat(extraPaths).Where(p => !string.IsNullOrEmpty(p)));
                    if (!string.IsNullOrEmpty(joined)) displayPath = joined;
                }
                itemResults.Add(new AgentConfigItemResult(target.Name, r.Success, r.Error, displayPath));
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AgentConfigMultiResults = itemResults;
                var ok  = itemResults.Count(r => r.Success);
                var msg = ok == itemResults.Count
                    ? $"All {ok} agent(s) configured successfully."
                    : $"{ok} of {itemResults.Count} configured. Check errors below.";
                AgentConfigResult = AgentConfigApplyResult.Ok(msg);
            });

            _ = DetectAgentsAsync();
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsApplyingAgentConfig = false;
                ApplyAgentConfigCommand.NotifyCanExecuteChanged();
            });
        }
    }
    public bool CanApplyAgentConfig() => !IsApplyingAgentConfig;

    private List<AgentViewModel> GetAgentConfigTargets() => IsAgentConfigBulkMode
        ? Agents.Where(a => a.IsSelectedForConfig && a.Installed).ToList()
        : AgentConfigTarget is { Installed: true } target ? new List<AgentViewModel> { target } : new List<AgentViewModel>();

    private static AgentDefinition FindDef(string id) =>
        AgentCatalog.All.First(d => d.Id == id);

    private List<string> GetSelectedModels() =>
        SelectableModels.Where(m => m.IsSelected).Select(m => m.Name).ToList();

    private async Task<List<TunnelAgent.Services.ModelEntry>> GetSelectedModelEntriesAsync()
    {
        var selected = SelectableModels.Where(m => m.IsSelected).ToList();
        var entries = new List<TunnelAgent.Services.ModelEntry>(selected.Count);
        foreach (var m in selected)
        {
            var isPerplexity = string.Equals(m.EngineId, EngineCatalog.PerplexityWebUiScraper.Id, StringComparison.OrdinalIgnoreCase);
            var engineBaseUrl = isPerplexity ? PerplexityEndpointUrl + "/v1" : CliProxyEndpointUrl + "/v1";
            var apiKey = isPerplexity
                ? TunnelAgent.Services.PerplexityAccountCatalogService.EnvVarName
                : "TUNNEL_AGENT_CLIPROXY_API_KEY";
            var displayName = await ResolveDisplayNameAsync(m.Name, isPerplexity);
            entries.Add(new TunnelAgent.Services.ModelEntry(m.Name, m.Provider, engineBaseUrl, apiKey, displayName));
        }
        return entries;
    }

    private static async Task<string> ResolveDisplayNameAsync(string modelId, bool isPerplexity)
    {
        var info = await TunnelAgent.Services.OpenRouterContextService.Instance
            .GetModelInfoAsync(modelId).ConfigureAwait(false);
        var name = info?.Name is string n ? StripProviderPrefix(n) : FormatModelId(modelId);
        return isPerplexity ? $"{name} (Tunnel Agent - Perplexity)" : $"{name} (Tunnel Agent)";
    }

    private static string StripProviderPrefix(string name)
    {
        var colon = name.IndexOf(':');
        return colon >= 0 ? name[(colon + 1)..].TrimStart() : name;
    }

    private static readonly HashSet<string> _uppercaseWords =
        new(StringComparer.OrdinalIgnoreCase) { "gpt", "ai" };

    private static string FormatModelId(string modelId)
    {
        // Strip provider prefix (e.g. "anthropic/claude-opus-4.7" -> "claude-opus-4.7")
        var bare = modelId.Contains('/') ? modelId[(modelId.LastIndexOf('/') + 1)..] : modelId;
        // Strip 8-digit date suffix (e.g. "claude-3-5-haiku-20241022" -> "claude-3-5-haiku")
        var idx = bare.Length - 8;
        if (idx > 1 && bare[idx - 1] == '-' && bare[idx..].All(char.IsDigit))
            bare = bare[..(idx - 1)];
        // Preserve dots as version separators, split only on dashes, capitalize each word
        var words = bare.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => _uppercaseWords.Contains(w) ? w.ToUpperInvariant() : char.ToUpperInvariant(w[0]) + w[1..]);
        return string.Join(' ', words);
    }

    public IEnumerable<SelectableModelViewModel> CliProxySelectableModels  => SelectableModels.Where(m => m.EngineId == EngineCatalog.CliProxyApi.Id);
    public IEnumerable<SelectableModelViewModel> PerplexitySelectableModels => SelectableModels.Where(m => m.EngineId == EngineCatalog.PerplexityWebUiScraper.Id);
    public bool HasCliProxySelectableModels  => CliProxySelectableModels.Any();
    public bool HasPerplexitySelectableModels => PerplexitySelectableModels.Any();

    private void PopulateSelectableModels()
    {
        ClearSelectableModels();
        foreach (var group in CliProxyModelGroups)
            foreach (var model in group.Models)
            {
                var vm = new SelectableModelViewModel(model.Name, group.ProviderName, EngineCatalog.CliProxyApi.Id);
                vm.PropertyChanged += OnSelectableModelPropertyChanged;
                SelectableModels.Add(vm);
            }
        foreach (var group in PerplexityModelGroups)
            foreach (var model in group.Models)
            {
                var vm = new SelectableModelViewModel(model.Name, group.ProviderName, EngineCatalog.PerplexityWebUiScraper.Id);
                vm.PropertyChanged += OnSelectableModelPropertyChanged;
                SelectableModels.Add(vm);
            }
        ApplyModelFilter();
        OnPropertyChanged(nameof(HasSelectableModels));
        OnPropertyChanged(nameof(HasCliProxySelectableModels));
        OnPropertyChanged(nameof(HasPerplexitySelectableModels));
        OnPropertyChanged(nameof(CliProxySelectableModels));
        OnPropertyChanged(nameof(PerplexitySelectableModels));
        OnPropertyChanged(nameof(ModelsExpanderLabel));
        OnPropertyChanged(nameof(AllVisibleModelsSelected));
        if (IsAgentConfigManualMode && ShowAgentConfigDialog && !AgentConfigHasResult)
            _ = RefreshManualPreviewAsync();
    }

    private void ClearSelectableModels()
    {
        foreach (var vm in SelectableModels)
            vm.PropertyChanged -= OnSelectableModelPropertyChanged;
        SelectableModels.Clear();
        OnPropertyChanged(nameof(HasSelectableModels));
        OnPropertyChanged(nameof(VisibleSelectableModelCount));
        OnPropertyChanged(nameof(HasVisibleSelectableModels));
        OnPropertyChanged(nameof(ShowNoModelSearchResults));
        OnPropertyChanged(nameof(AllVisibleModelsSelected));
        OnPropertyChanged(nameof(ModelsExpanderLabel));
    }

    private void OnSelectableModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableModelViewModel.IsSelected)) return;
        if (_suppressSelectableModelState) return;
        OnPropertyChanged(nameof(ModelsExpanderLabel));
        OnPropertyChanged(nameof(AllVisibleModelsSelected));
        if (IsAgentConfigManualMode && ShowAgentConfigDialog && !AgentConfigHasResult)
            _ = RefreshManualPreviewAsync();
    }

    private async Task RefreshManualPreviewAsync()
    {
        var targets      = GetAgentConfigTargets();
        var models       = GetSelectedModels();
        var modelEntries = await GetSelectedModelEntriesAsync().ConfigureAwait(false);
        var previews     = new List<RawConfigPreview>();
        foreach (var a in targets)
            previews.AddRange(await _agentConfiguration.PreviewAsync(FindDef(a.Id), AgentProxyBaseUrl, CurrentAgentApiKey, models, modelEntries).ConfigureAwait(false));
        AgentConfigPreviews = previews;
    }

    private void InitApiKeysFromSettings()
    {
        // One-time migration: move CliProxyApiKeys/DefaultCliProxyApiKey from legacy settings.json
        _ = _configService.MigrateApiKeysFromSettingsAsync(_settings.SettingsPath)
            .ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshApiKeyItems));
        RefreshApiKeyItems();
    }

    private void RefreshApiKeyItems() => _ = RefreshApiKeyItemsAsync();

    private async Task RefreshApiKeyItemsAsync()
    {
        var keys   = await _configService.ReadApiKeysFromConfigAsync();
        var defKey = TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY") ?? "";

        // If env var has a key not in yaml, add it so both stay in sync.
        if (!string.IsNullOrWhiteSpace(defKey) && !keys.Contains(defKey, StringComparer.Ordinal))
        {
            keys.Add(defKey);
            await _configService.WriteApiKeysToConfigAsync(keys);
        }
        // If env var is empty but yaml has keys, adopt the first as default.
        // The yaml is the source of truth for accepted keys, so without this the
        // agent configs would fall back to "no-key" while the proxy still requires one.
        else if (string.IsNullOrWhiteSpace(defKey) && keys.Count > 0)
        {
            defKey = keys[0];
            await SyncCliProxyEnvVarAsync(defKey);
        }

        CliProxyApiKeys.Clear();
        foreach (var key in keys)
            CliProxyApiKeys.Add(new CliProxyApiKeyViewModel(key,
                string.Equals(key, defKey, StringComparison.Ordinal)));
        OnPropertyChanged(nameof(CurrentAgentApiKey));
    }

    [RelayCommand]
    private void OpenApiKeys()
    {
        RefreshApiKeyItems();
        ApiKeyDraft = "";
        ShowApiKeyDraft = false;
        ShowApiKeysDialog = true;
    }

    [RelayCommand]
    private void DismissApiKeys()
    {
        ApiKeyDraft = "";
        ShowApiKeyDraft = false;
        ShowApiKeysDialog = false;
    }

    [RelayCommand]
    private async Task AddApiKeyAsync()
    {
        var key = ApiKeyDraft.Trim();
        if (string.IsNullOrWhiteSpace(key)) return;
        ApiKeyDraft = "";
        ShowApiKeyDraft = false;
        var isFirst = !CliProxyApiKeys.Any() || string.IsNullOrWhiteSpace(TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY"));
        await PersistApiKeysAsync(key, setDefault: isFirst);
    }

    [RelayCommand]
    private async Task RemoveApiKeyAsync(CliProxyApiKeyViewModel? key)
    {
        if (key is null) return;
        var keys = await _configService.ReadApiKeysFromConfigAsync();
        keys.RemoveAll(k => string.Equals(k, key.Value, StringComparison.Ordinal));
        await _configService.WriteApiKeysToConfigAsync(keys);
        // Update default env var if removed key was the default
        var defKey = TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY") ?? "";
        if (string.Equals(defKey, key.Value, StringComparison.Ordinal))
            await SyncCliProxyEnvVarAsync(keys.FirstOrDefault() ?? "");
        await RefreshApiKeyItemsAsync();
    }

    [RelayCommand]
    private async Task SetDefaultApiKeyAsync(CliProxyApiKeyViewModel? key)
    {
        if (key is null) return;
        await SyncCliProxyEnvVarAsync(key.Value);
        await RefreshApiKeyItemsAsync();
    }

    private async Task PersistApiKeysAsync(string newKey, bool setDefault)
    {
        var keys = await _configService.ReadApiKeysFromConfigAsync();
        if (!keys.Contains(newKey, StringComparer.Ordinal))
            keys.Add(newKey);
        await _configService.WriteApiKeysToConfigAsync(keys);
        if (setDefault) await SyncCliProxyEnvVarAsync(newKey);
        await RefreshApiKeyItemsAsync();
        await CliProxyEngine.WriteConfigAsync();
    }

    private static Task SyncCliProxyEnvVarAsync(string key) => Task.Run(() =>
    {
        if (!string.IsNullOrWhiteSpace(key))
            TunnelAgent.Infrastructure.Services.UserEnvironmentService.Set("TUNNEL_AGENT_CLIPROXY_API_KEY", key);
        else
            TunnelAgent.Infrastructure.Services.UserEnvironmentService.Remove("TUNNEL_AGENT_CLIPROXY_API_KEY");
    });

    public int LocalProxyScrollRequestId { get; private set; }

    [RelayCommand] private void SelectConfiguration() => SelectedSection = SectionKey.ConfigGeneral;
    [RelayCommand] private void SelectConfigGeneral() => SelectedSection = SectionKey.ConfigGeneral;
    [RelayCommand] private void SelectConfigCliProxy() => SelectedSection = SectionKey.ConfigCliProxy;
    [RelayCommand]
    private void SelectConfigLocalProxy()
    {
        SelectedSection = SectionKey.ConfigCliProxy;
        LocalProxyScrollRequestId++;
        OnPropertyChanged(nameof(LocalProxyScrollRequestId));
    }
    [RelayCommand] private void SelectConfigPerplexity() => SelectedSection = SectionKey.ConfigPerplexity;

    public bool IsConfigSection => SelectedSection is SectionKey.ConfigGeneral or SectionKey.ConfigCliProxy or SectionKey.ConfigPerplexity;

    partial void OnSelectedQuotaProviderChanged(ProviderViewModel? value)
    {
        UpdateQuotaSelectionFlags();
        OnPropertyChanged(nameof(QuotaTabIndex));
        OnPropertyChanged(nameof(SelectedQuotaAccounts));
        OnPropertyChanged(nameof(HasQuotaAccounts));
        OnPropertyChanged(nameof(HasAnyQuotaData));
        OnPropertyChanged(nameof(HasSelectedQuotaAccounts));
        OnPropertyChanged(nameof(ShowQuotaAccountEmptyState));
        OnPropertyChanged(nameof(QuotaEmptyStateText));
    }

    private void UpdateQuotaSelectionFlags()
    {
        foreach (var provider in Providers)
            provider.IsQuotaSelected = ReferenceEquals(provider, SelectedQuotaProvider);
        foreach (var provider in StandaloneQuotaProviders)
            provider.IsQuotaSelected = ReferenceEquals(provider, SelectedQuotaProvider);
    }
    [RelayCommand] private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        SidebarWidth = value ? 56.0 : 176.0;
        SidebarContentOpacity = value ? 0.0 : 1.0;
        SidebarToggleIconScaleX = value ? -1.0 : 1.0;
    }

    [ObservableProperty] private double _sidebarWidth = 176.0;
    [ObservableProperty] private double _sidebarContentOpacity = 1.0;
    [ObservableProperty] private double _sidebarToggleIconScaleX = 1.0;
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
            await FocusedConfigEngine.CheckForUpdateAsync();
            if (!FocusedConfigEngine.UpdateAvailable)
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
        if (!FocusedConfigEngine.UpdateAvailable) return;
        await InstallEngineVersionAsync(null, FocusedConfigEngineId);
    }

    [RelayCommand]
    private async Task UpdateToastEngine()
    {
        var engineId = string.IsNullOrWhiteSpace(UpdateToastEngineId) ? FocusedConfigEngineId : UpdateToastEngineId;
        var engine = _engineRegistry.Get(engineId);
        if (!engine.UpdateAvailable) return;
        await InstallEngineVersionAsync(null, engineId);
    }

    [RelayCommand]
    private async Task InstallSelectedEngine()
    {
        if (SelectedEngineRelease is null || !CanInstallSelectedEngine) return;
        await InstallEngineVersionAsync(SelectedEngineRelease.TagName, FocusedConfigEngineId);
    }

    private async Task InstallEngineVersionAsync(string? version, string engineId)
    {
        var engine = _engineRegistry.Get(engineId);

        // Stay on correct config section for the engine being updated.
        SelectedSection = string.Equals(engineId, EngineCatalog.PerplexityWebUiScraper.Id, StringComparison.OrdinalIgnoreCase)
            ? SectionKey.ConfigPerplexity
            : SectionKey.ConfigCliProxy;
        ShowUpdateToast = false;
        var requestedVersion = string.IsNullOrWhiteSpace(version) ? engine.LatestVersion : version;
        if (!string.IsNullOrWhiteSpace(requestedVersion) && !VersionsEqual(requestedVersion, engine.LatestVersion))
            _suppressAutoUpdateForEngineId = engineId;
        try { await engine.DownloadAndInstallAsync(requestedVersion); }
        catch { return; }
        ConfigHasBadge = false;
        _engineUpdateToastShown[engineId] = false;
        ShowUpdateSuccess = true;
        var isPerplexity = string.Equals(engineId, EngineCatalog.PerplexityWebUiScraper.Id, StringComparison.OrdinalIgnoreCase);
        if (isPerplexity) ShowPerplexityUpdateSuccess = true;
        else ShowCliProxyUpdateSuccess = true;
        _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            ShowUpdateSuccess = false;
            ShowCliProxyUpdateSuccess = false;
            ShowPerplexityUpdateSuccess = false;
        }));
    }

    [RelayCommand]
    public async Task RestartEngineAsync()
    {
        _lastEngineErrorShown.Remove(FocusedConfigEngineId);
        await FocusedConfigEngine.StopAsync();
        await FocusedConfigEngine.StartAsync();
    }

    [RelayCommand]
    public async Task StartServerAsync()
    {
        // Allow (re)starting from any non-active state. Crucially this includes
        // EngineState.Error, so a failed start (e.g. the port was momentarily in
        // use by another process) can be retried without restarting the whole app.
        if (FocusedConfigEngine.State is EngineState.Running or EngineState.Starting)
            return;

        // Reset the dedupe so an explicit user-initiated start always re-toasts on failure,
        // even when the error message is identical to the previous attempt (e.g. the other
        // instance is still holding the port).
        _lastEngineErrorShown.Remove(FocusedConfigEngineId);

        // A failed start (state -> Error) is surfaced as an error toast centrally
        // in OnAnyEngineStateChanged, which also covers autostart failures.
        try { await FocusedConfigEngine.StartAsync(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartServer] {ex.Message}");
        }
    }

    [RelayCommand] public async Task StopServerAsync() => await FocusedConfigEngine.StopAsync();

    [RelayCommand]
    private void ToggleAgent(AgentViewModel a)
    {
        a.Enabled = !a.Enabled;
        OnPropertyChanged(nameof(EnabledAgentCount));
    }

    [RelayCommand]
    private async Task DetectAgentsAsync()
    {
        if (IsDetectingAgents) return;
        IsDetectingAgents = true;
        _agentsDetectedOnce = true;
        try
        {
            var results = await _agentDetection.DetectAllAsync().ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var vm in Agents)
                {
                    var r = results.FirstOrDefault(x => x.AgentId == vm.Id)
                            ?? AgentDetectionResult.NotFound(vm.Id);
                    vm.ApplyDetection(r);
                }
                OnPropertyChanged(nameof(EnabledAgentCount));
                OnPropertyChanged(nameof(InstalledAgentCount));
                OnPropertyChanged(nameof(ConfiguredAgentCount));
                OnPropertyChanged(nameof(NotInstalledAgentCount));
                OnPropertyChanged(nameof(InstalledAgents));
                OnPropertyChanged(nameof(NotInstalledAgents));
            });
        }
        finally
        {
            IsDetectingAgents = false;
        }
    }

    private void InitAgentsFromCatalog()
    {
        foreach (var def in AgentCatalog.All)
        {
            var vm = new AgentViewModel(def);
            vm.PropertyChanged += OnAgentPropertyChanged;
            Agents.Add(vm);
        }
    }

    private void OnAgentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentViewModel.IsSelectedForConfig))
        {
            OnPropertyChanged(nameof(AgentConfigSelectedCount));
            OnPropertyChanged(nameof(AgentConfigApplyLabel));
            if (IsAgentConfigManualMode && IsAgentConfigBulkMode && ShowAgentConfigDialog && !AgentConfigHasResult)
                _ = RefreshManualPreviewAsync();
        }
        else if (e.PropertyName == nameof(AgentViewModel.Configured))
        {
            OnPropertyChanged(nameof(ConfiguredAgentCount));
        }
    }

    // ── App self-update (Velopack) ────────────────────────────────────────────

    [ObservableProperty] private bool _appUpdateDismissed;
    public bool AppUpdateAvailable    => AppUpdateState == AppUpdateState.UpdateAvailable && !AppUpdateDismissed;
    public bool AppUpdateDownloading  => AppUpdateState == AppUpdateState.Downloading;
    public bool AppUpdateReady        => AppUpdateState == AppUpdateState.ReadyToInstall;
    [ObservableProperty] private int _appUpdateDownloadProgress;
    public bool AppUpdateSupported    => _appUpdate.IsInstalled;

    public void InitAppUpdater()
    {
        _appUpdate.StateChanged += () => Dispatcher.UIThread.Post(() =>
        {
            AppUpdateDismissed = false;
            AppUpdateState      = _appUpdate.State;
            AppUpdateNewVersion = _appUpdate.NewVersion;
            OnPropertyChanged(nameof(AppUpdateAvailable));
            OnPropertyChanged(nameof(AppUpdateDownloading));
            OnPropertyChanged(nameof(AppUpdateReady));
            UpdateBadgeState();
        });
        _appUpdate.DownloadProgressChanged += p => Dispatcher.UIThread.Post(() =>
            AppUpdateDownloadProgress = p);

        if (_settings.Current.AutoCheckForAppUpdates)
            _ = _appUpdate.CheckAsync();
    }

    [RelayCommand]
    private async Task CheckForAppUpdate()
    {
        if (!_appUpdate.IsInstalled)
        {
            ShowAppNoUpdateToast = true;
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowAppNoUpdateToast = false));
            return;
        }
        var hasUpdate = await _appUpdate.CheckAsync();
        if (!hasUpdate)
        {
            ShowAppNoUpdateToast = true;
            _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowAppNoUpdateToast = false));
        }
    }

    partial void OnAppUpdateDismissedChanged(bool value) => OnPropertyChanged(nameof(AppUpdateAvailable));

    [RelayCommand]
    private void DismissAppUpdate() => AppUpdateDismissed = true;

    [RelayCommand]
    private async Task DownloadAppUpdate()
    {
        SelectedSection = SectionKey.ConfigGeneral;
        await _appUpdate.DownloadAsync();
        _appUpdate.ApplyAndRestart();
    }

    [RelayCommand]
    private void InstallAppUpdateAndRestart()
    {
        _appUpdate.ApplyAndRestart();
    }

    private void OnManagementKeyRejected()
    {
        if (_managementKeyRepairAttempted) return;
        _managementKeyRepairAttempted = true;
        _ = RepairManagementKeyAsync();
    }

    private async Task RepairManagementKeyAsync()
    {
        await _configService.WriteConfigAsync(forceManagementKey: true);
        if (CliProxyEngine.State is EngineState.Running or EngineState.Starting)
        {
            await CliProxyEngine.StopAsync();
            await CliProxyEngine.StartAsync();
            ConfigureLogsService(CliProxyEngine.Port);
            _logs.SetManagementApiAvailable(true);
            _usage.SetManagementApiAvailable(true);
        }

        ShowManagementKeyRepairedToast = true;
        _ = Task.Delay(4000).ContinueWith(_ => Dispatcher.UIThread.Post(() => ShowManagementKeyRepairedToast = false));
    }

    [RelayCommand]
    private void DismissManagementKeyRepairedToast() => ShowManagementKeyRepairedToast = false;

    [RelayCommand]
    private void DismissNoUpdateToast() => ShowNoUpdateToast = false;

    [RelayCommand]
    private void DismissAppNoUpdateToast() => ShowAppNoUpdateToast = false;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _cliProxyModelFetchCts?.Cancel();
        _cliProxyModelFetchCts?.Dispose();
        _cliProxyModelFetchCts = null;

        _customProviderModelFetchCts?.Cancel();
        _customProviderModelFetchCts?.Dispose();
        _customProviderModelFetchCts = null;

        _oauthStatusDismissCts?.Cancel();
        _oauthStatusDismissCts?.Dispose();
        _oauthStatusDismissCts = null;

        _configurationStatusDismissCts?.Cancel();
        _configurationStatusDismissCts?.Dispose();
        _configurationStatusDismissCts = null;

        _perplexityModelFetchCts?.Cancel();
        _perplexityModelFetchCts?.Dispose();
        _perplexityModelFetchCts = null;

        foreach (var engine in _engineRegistry.Engines)
            engine.StateChanged -= OnAnyEngineStateChanged;

        _catalog.ProvidersRefreshed    -= OnProvidersRefreshed;
        _catalog.ProvidersRebuilt      -= OnProvidersRebuilt;
        _catalog.ProviderFirstConnected -= OnProviderFirstConnected;
        _perplexityAccounts.AccountsChanged -= OnPerplexityAccountsChanged;
        _logs.EntriesLoaded -= OnLogEntriesLoaded;
        _logs.RawLinesLoaded -= OnRawLogLinesLoaded;
        _logs.Cleared -= OnLogsCleared;
        _logs.ManagementKeyRejected -= OnManagementKeyRejected;
        _usage.EventsLoaded -= OnUsageEventsLoaded;

        _logs.Dispose();
        _usage.Dispose();
        _usageStore.Dispose();
        _catalog.Dispose();
        await _perplexityTokenGenerator.DisposeAsync();
    }
}
