using System;
using System.Collections.ObjectModel;
using System.Linq;
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
    [ObservableProperty] private bool _showUpdateSuccess;
    [ObservableProperty] private string _engineStatusText = "Stopped";

    // Add-account dialog state
    [ObservableProperty] private bool _showAddAccountDialog;
    [ObservableProperty] private ProviderViewModel? _addAccountTarget;

    // OAuth status feedback
    [ObservableProperty] private bool _showOAuthStatus;
    [ObservableProperty] private bool _oAuthStatusIsError;
    [ObservableProperty] private string _oAuthStatusMessage = "";

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
            OnPropertyChanged(nameof(EndpointUrl));
            _ = ApplyPortChangeAsync();
        }
    }
    public bool LaunchAtLogin
    {
        get => _settings.Current.LaunchAtLogin;
        set { _settings.Current.LaunchAtLogin = value; _settings.Save(); OnPropertyChanged(); }
    }
    public string LogLevel
    {
        get => _settings.Current.LogLevel;
        set { _settings.Current.LogLevel = value; _settings.Save(); OnPropertyChanged(); }
    }
    public bool AutoCheckForUpdates
    {
        get => _settings.Current.AutoCheckForUpdates;
        set { _settings.Current.AutoCheckForUpdates = value; _settings.Save(); OnPropertyChanged(); }
    }
    public bool AutoUpdate
    {
        get => _settings.Current.AutoUpdate;
        set { _settings.Current.AutoUpdate = value; _settings.Save(); OnPropertyChanged(); UpdateBadgeState(); }
    }

    public ServerState ServerState => EngineState switch
    {
        EngineState.Running  => ServerState.Running,
        EngineState.Starting => ServerState.Starting,
        EngineState.Error    => ServerState.Error,
        _                    => ServerState.Stopped
    };

    public string AppVersion { get; } = TunnelAgent.AppVersion.Current;
    public string[] LogLevels { get; } = { "error", "warn", "info", "debug" };

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();
    public ObservableCollection<AgentViewModel> Agents { get; } = new();
    public ObservableCollection<AvailableModelGroupViewModel> AvailableModelGroups { get; } = new();
    public ObservableCollection<ActivityLogViewModel> ActivityLogs { get; } = new();

    public string EndpointUrl => $"http://127.0.0.1:{Port}";

    public int ConnectedProviderCount   => Providers.Count(p => p.Connected || p.ActiveAccountCount > 0);
    public int EnabledAgentCount        => Agents.Count(a => a.Installed && a.Enabled);
    public int ActivityLogCount         => ActivityLogs.Count;
    public int TotalAvailableModelCount => AvailableModelGroups.Sum(g => g.ModelCount);

    // Design-time constructor
    public MainWindowViewModel() : this(new SettingsService(), null!, null!) { }

    public MainWindowViewModel(SettingsService settings, EngineService engine, ProviderCatalogService catalog)
    {
        _settings = settings;
        _engine   = engine  ?? new EngineService(settings);
        var engineConfig = new EngineConfigService(settings);
        _catalog  = catalog ?? new ProviderCatalogService(settings, engineConfig);

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

            EngineState      = _engine.State;
            InstalledVersion = _engine.InstalledVersion;
            LatestVersion    = _engine.LatestVersion;
            DownloadProgress = _engine.DownloadProgress;
            UpdateAvailable  = _engine.UpdateAvailable;
            EngineStatusText = BuildEngineStatusText();
            UpdateBadgeState();
            OnPropertyChanged(nameof(ServerState));

            if (UpdateAvailable && !wasAvailable && !_updateToastShown)
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

    private async Task ApplyPortChangeAsync()
    {
        var wasRunning = _engine.IsRunning;
        if (wasRunning) await _engine.StopAsync();
        await _engine.WriteConfigAsync();
        if (wasRunning) await _engine.StartAsync();
    }

    public async Task InitializeAsync()
    {
        await _settings.LoadAsync();
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
        await _catalog.RemoveAccountAsync(account.ProviderId, account.ApiKey);
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    // ── Engine commands ───────────────────────────────────────────────────────

    [RelayCommand] private void SelectProviders()     => SelectedSection = SectionKey.Providers;
    [RelayCommand] private void SelectAgents()        => SelectedSection = SectionKey.Agents;
    [RelayCommand] private void SelectActivity()      => SelectedSection = SectionKey.Activity;
    [RelayCommand] private void SelectConfiguration() => SelectedSection = SectionKey.Configuration;
    [RelayCommand] private void ToggleSidebar()       => IsSidebarCollapsed = !IsSidebarCollapsed;
    [RelayCommand] private void ToggleTheme()         => IsDark = !IsDark;
    [RelayCommand] private void DismissToast()        => ShowUpdateToast = false;

    [RelayCommand]
    private async Task UpdateEngine()
    {
        if (!_engine.UpdateAvailable) return;

        SelectedSection = SectionKey.Configuration;
        ShowUpdateToast = false;

        try { await _engine.DownloadAndInstallAsync(); }
        catch { return; }

        ConfigHasBadge    = false;
        _updateToastShown = false;

        ShowUpdateSuccess = true;
        _ = Task.Delay(4000).ContinueWith(_ =>
            Dispatcher.UIThread.Post(() => ShowUpdateSuccess = false));
    }

    [RelayCommand]
    private async Task RestartEngine()
    {
        await _engine.StopAsync();
        await _engine.StartAsync();
    }

    [RelayCommand]
    private async Task StartServer()
    {
        if (_engine.State == EngineState.Stopped)
            await _engine.StartAsync();
    }

    [RelayCommand] private void StopServer() => _ = _engine.StopAsync();

    [RelayCommand]
    private void ToggleAgent(AgentViewModel a)
    {
        a.Enabled = !a.Enabled;
        OnPropertyChanged(nameof(EnabledAgentCount));
    }

    // ── Demo data (agents + activity only — providers come from catalog) ──────

    private void SeedDemoAgents()
    {
        Agents.Add(new AgentViewModel("claude-code", "Claude Code", "claude",       "Terminal", true)  { Enabled = true,  RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("codex",       "Codex CLI",   "codex",        "Code",     true)  { Enabled = true,  RouteProviderId = "codex"  });
        Agents.Add(new AgentViewModel("cursor",      "Cursor Agent","cursor-agent", "Sparkles", true)  { Enabled = false, RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("aider",       "Aider",       "aider",        "Terminal", false,
            "Install via pip to route through Tunnel."));

        var anthropicModels = new AvailableModelGroupViewModel("Anthropic", "claude", true);
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-opus-4-1-20250805",  "OAuth", "200K context", "Claude Code"));
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-opus-4-5-20251101",  "OAuth", "200K context", "Claude Code"));
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-sonnet-4-5",         "OAuth", "200K context", "Claude Code"));
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-haiku-4-5",          "OAuth", "200K context", "Claude Code"));

        var openAiModels = new AvailableModelGroupViewModel("OpenAI", "codex");
        openAiModels.Models.Add(new AvailableModelViewModel("gpt-5-codex", "ChatGPT", "272K context", "OpenAI Codex"));
        openAiModels.Models.Add(new AvailableModelViewModel("gpt-5",       "ChatGPT", "400K context", "OpenAI Codex"));
        openAiModels.Models.Add(new AvailableModelViewModel("gpt-4.1",     "ChatGPT", "1M context",   "OpenAI Codex"));

        AvailableModelGroups.Add(anthropicModels);
        AvailableModelGroups.Add(openAiModels);

        ActivityLogs.Add(new ActivityLogViewModel("POST", "/v1/messages",  "Claude Code", "Claude", "claude-sonnet-4.5", "200", "1.2s",  "12s ago"));
        ActivityLogs.Add(new ActivityLogViewModel("POST", "/v1/responses", "Codex CLI",   "OpenAI", "gpt-5-codex",       "200", "842ms", "48s ago"));
        ActivityLogs.Add(new ActivityLogViewModel("GET",  "/v1/models",    "Cursor Agent","Claude", "-",                 "200", "31ms",  "2m ago"));
    }
}
