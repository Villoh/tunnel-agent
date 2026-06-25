using System.Collections.Generic;
using System.Linq;

namespace TunnelAgent.Services;

/// <summary>Persisted state for one account slot of a custom (OpenAI-compat) provider.</summary>
public sealed class ProviderAccountSettings
{
    public string ApiKey   { get; set; } = "";
    public bool   Disabled { get; set; } = false;
}

public enum ProviderKind
{
    OpenAICompatibility,
    ClaudeApiKey,
    GeminiApiKey,
    CodexApiKey
}

/// <summary>Persisted state for one provider entry (OAuth or custom).</summary>
public sealed class ProviderSettings
{
    /// <summary>Stable identifier matching CLIProxyAPI keys (e.g. "claude", "codex").</summary>
    public string Id { get; set; } = "";

    /// <summary>False = excluded from config.yaml (oauth-excluded-models / omitted from openai-compatibility).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL for API-key providers. Empty uses CLIProxyAPI defaults when supported.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>YAML block used when this provider has upstream API-key accounts.</summary>
    public ProviderKind Kind { get; set; } = ProviderKind.OpenAICompatibility;

    /// <summary>Friendly display name override (empty = use catalog default).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>API-key accounts for custom providers. Empty for OAuth providers.</summary>
    public List<ProviderAccountSettings> Accounts { get; set; } = [];

    /// <summary>Upstream model names exposed for OpenAI-compatible providers (written under <c>models:</c>).</summary>
    public List<string> Models { get; set; } = [];
}

public sealed class EngineRuntimeSettings
{
    public string EngineId { get; set; } = "";
    public int Port { get; set; }
    public bool AutoStart { get; set; }
    public string PreferredVersion { get; set; } = "";
}

public sealed class PerplexityAccountSettings
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string SessionToken { get; set; } = "";
    public bool Disabled { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class AppSettings
{
    public int Port { get; set; }
    public bool LaunchAtLogin { get; set; } = true;
    public string ThemeMode { get; set; } = "system";
    public string? Language { get; set; } = null; // null = usar idioma del sistema
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoCheckForAppUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = false;
    public bool MaskEmails { get; set; } = false;
    public string PreferredEngineVersion { get; set; } = "";

    /// <summary>Auto-generated key for the CLIProxyAPI management API (enables /v0/management/* and logging).</summary>
    public string ManagementKey { get; set; } = "";

    /// <summary>Whether the raw proxy logs view polls automatically for new entries.</summary>
    public bool LogsAutoRefresh { get; set; } = false;

    /// <summary>Polling interval in seconds for the raw proxy logs view (2, 5, 10, 30).</summary>
    public int LogsRefreshIntervalSeconds { get; set; } = 5;
    public List<EngineRuntimeSettings> Engines { get; set; } = [];
    public List<PerplexityAccountSettings> PerplexityAccounts { get; set; } = [];


    /// <summary>
    /// How requests are distributed across multiple API keys for a provider.
    /// RoundRobin = even distribution (default); FillFirst = use first account until limit.
    /// </summary>
    public ViewModels.RoutingStrategy RoutingStrategy { get; set; } = ViewModels.RoutingStrategy.RoundRobin;

    /// <summary>
    /// Persisted provider list. OAuth providers appear here once first seen/enabled;
    /// custom providers are added when the user adds an account.
    /// </summary>
    public List<ProviderSettings> Providers { get; set; } = [];

    /// <summary>
    /// Experimental model-fallback configuration. Virtual models map to ordered
    /// provider/model entries with automatic retry when quota is exhausted.
    /// </summary>
    public FallbackConfiguration Fallback { get; set; } = new();

    public EngineRuntimeSettings GetOrAddEngine(string engineId, int defaultPort)
    {
        var engine = Engines.FirstOrDefault(e => e.EngineId == engineId);
        if (engine is not null)
            return engine;

        engine = new EngineRuntimeSettings
        {
            EngineId = engineId,
            Port = defaultPort,
            PreferredVersion = engineId == "cliproxyapi" ? PreferredEngineVersion : ""
        };

        Engines.Add(engine);
        return engine;
    }
}

