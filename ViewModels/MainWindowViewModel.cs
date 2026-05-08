using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    [ObservableProperty] private SectionKey _selectedSection = SectionKey.Providers;
    [ObservableProperty] private ServerState _serverState = ServerState.Running;
    [ObservableProperty] private int _port = 7890;
    [ObservableProperty] private bool _launchAtLogin = true;
    [ObservableProperty] private bool _telemetry;
    [ObservableProperty] private bool _useKeychain = true;
    [ObservableProperty] private string _logLevel = "info";
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _isSidebarCollapsed;

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();
    public ObservableCollection<AgentViewModel> Agents { get; } = new();
    public ObservableCollection<AvailableModelGroupViewModel> AvailableModelGroups { get; } = new();
    public ObservableCollection<ActivityLogViewModel> ActivityLogs { get; } = new();

    public string[] LogLevels { get; } = { "error", "warn", "info", "debug" };
    public string[] BindAddresses { get; } = { "127.0.0.1", "0.0.0.0" };

    public MainWindowViewModel()
    {
        var claude = new ProviderViewModel("claude", "Claude Code", PackIconSimpleIconsKind.Claude, "#D97757", "Anthropic models via OAuth.")
        { Connected = true, Account = "alex@studio.dev", Model = "sonnet-4.5" };
        foreach (var v in new double[] { .4,.6,.3,.8,.5,.9,.7,.6,.85,.5,.7 }) claude.Spark.Add(v);

        var codex = new ProviderViewModel("codex", "OpenAI Codex", PackIconSimpleIconsKind.OpenAi, "#23262E", "OpenAI Codex via ChatGPT plan.")
        { Connected = true, Account = "alex.openai", Model = "gpt-5-codex" };
        foreach (var v in new double[] { .2,.5,.4,.3,.6,.4,.7,.5,.4,.65,.5 }) codex.Spark.Add(v);

        Providers.Add(claude); Providers.Add(codex);

        Agents.Add(new AgentViewModel("claude-code", "Claude Code", "claude",      "Terminal", true) { Enabled = true,  RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("codex",       "Codex CLI",   "codex",       "Code",     true) { Enabled = true,  RouteProviderId = "codex"  });
        Agents.Add(new AgentViewModel("cursor",      "Cursor Agent","cursor-agent","Sparkles", true) { Enabled = false, RouteProviderId = "claude" });
        Agents.Add(new AgentViewModel("aider",       "Aider",       "aider",       "Terminal", false, "Install via pip to route through Tunnel."));

        var anthropicModels = new AvailableModelGroupViewModel("Anthropic", "claude", true);
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-opus-4-1-20250805", "OAuth", "200K context", "Claude Code"));
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-opus-4-5-20251101", "OAuth", "200K context", "Claude Code"));
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-sonnet-4-5", "OAuth", "200K context", "Claude Code"));
        anthropicModels.Models.Add(new AvailableModelViewModel("claude-haiku-4-5", "OAuth", "200K context", "Claude Code"));

        var openAiModels = new AvailableModelGroupViewModel("OpenAI", "codex");
        openAiModels.Models.Add(new AvailableModelViewModel("gpt-5-codex", "ChatGPT", "272K context", "OpenAI Codex"));
        openAiModels.Models.Add(new AvailableModelViewModel("gpt-5", "ChatGPT", "400K context", "OpenAI Codex"));
        openAiModels.Models.Add(new AvailableModelViewModel("gpt-4.1", "ChatGPT", "1M context", "OpenAI Codex"));

        AvailableModelGroups.Add(anthropicModels);
        AvailableModelGroups.Add(openAiModels);

        ActivityLogs.Add(new ActivityLogViewModel("POST", "/v1/messages", "Claude Code", "Claude", "claude-sonnet-4.5", "200", "1.2s", "12s ago"));
        ActivityLogs.Add(new ActivityLogViewModel("POST", "/v1/responses", "Codex CLI", "OpenAI", "gpt-5-codex", "200", "842ms", "48s ago"));
        ActivityLogs.Add(new ActivityLogViewModel("GET", "/v1/models", "Cursor Agent", "Claude", "-", "200", "31ms", "2m ago"));
    }

    [RelayCommand] private void SelectProviders()     => SelectedSection = SectionKey.Providers;
    [RelayCommand] private void SelectAgents()        => SelectedSection = SectionKey.Agents;
    [RelayCommand] private void SelectActivity()      => SelectedSection = SectionKey.Activity;
    [RelayCommand] private void SelectConfiguration() => SelectedSection = SectionKey.Configuration;
    [RelayCommand] private void ToggleSidebar()       => IsSidebarCollapsed = !IsSidebarCollapsed;
    [RelayCommand] private void ToggleTheme()         => IsDark = !IsDark;

    [RelayCommand] private async Task StartServer()
    {
        ServerState = ServerState.Starting;
        await Task.Delay(700);
        ServerState = ServerState.Running;
    }

    [RelayCommand] private void StopServer() => ServerState = ServerState.Stopped;

    [RelayCommand]
    private void ToggleProvider(ProviderViewModel p)
    {
        p.Connected = !p.Connected;
        OnPropertyChanged(nameof(ConnectedProviderCount));
    }

    [RelayCommand]
    private void ToggleAgent(AgentViewModel a)
    {
        a.Enabled = !a.Enabled;
        OnPropertyChanged(nameof(EnabledAgentCount));
    }

    public string EndpointUrl => $"http://{BindAddress}:{Port}";
    partial void OnPortChanged(int value)        => OnPropertyChanged(nameof(EndpointUrl));
    partial void OnBindAddressChanged(string value) => OnPropertyChanged(nameof(EndpointUrl));

    public int ConnectedProviderCount  => Providers.Count(p => p.Connected);
    public int EnabledAgentCount       => Agents.Count(a => a.Installed && a.Enabled);
    public int ActivityLogCount        => ActivityLogs.Count;
    public int TotalAvailableModelCount => AvailableModelGroups.Sum(g => g.ModelCount);
}
