using System.Collections.Generic;
using System.Linq;

namespace TunnelAgent.Services;

/// <summary>Persisted state for one account slot of a custom (OpenAI-compat) provider.</summary>
public sealed class ProviderAccountSettings
{
    public string ApiKey   { get; set; } = "";
    public string Label    { get; set; } = "";
    public bool   Disabled { get; set; } = false;
}

/// <summary>Persisted state for one provider entry (OAuth or custom).</summary>
public sealed class ProviderSettings
{
    /// <summary>Stable identifier matching CLIProxyAPI keys (e.g. "claude", "gemini-cli").</summary>
    public string Id { get; set; } = "";

    /// <summary>False = excluded from config.yaml (oauth-excluded-models / omitted from openai-compatibility).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Base URL for custom OpenAI-compat providers. Empty for OAuth providers.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Friendly display name override (empty = use catalog default).</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>API-key accounts for custom providers. Empty for OAuth providers.</summary>
    public List<ProviderAccountSettings> Accounts { get; set; } = [];
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
    public string ActiveEngineId { get; set; } = "cliproxyapi";
    public int Port { get; set; } = 8317;
    public bool LaunchAtLogin { get; set; } = true;
    public string ThemeMode { get; set; } = "system";
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoCheckForAppUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = false;
    public bool MaskEmails { get; set; } = false;
    public string PreferredEngineVersion { get; set; } = "";
    public List<EngineRuntimeSettings> Engines { get; set; } = [];
    public List<PerplexityAccountSettings> PerplexityAccounts { get; set; } = [];
    public string DefaultCliProxyApiKey { get; set; } = "";
    public List<string> CliProxyApiKeys { get; set; } = [];

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

