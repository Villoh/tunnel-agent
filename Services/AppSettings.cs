using System.Collections.Generic;

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

public sealed class AppSettings
{
    public int Port { get; set; } = 8317;
    public bool LaunchAtLogin { get; set; } = true;
    public bool IsDark { get; set; }
    public string LogLevel { get; set; } = "info";
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = false;
    public string PreferredEngineVersion { get; set; } = "";

    /// <summary>
    /// Persisted provider list. OAuth providers appear here once first seen/enabled;
    /// custom providers are added when the user adds an account.
    /// </summary>
    public List<ProviderSettings> Providers { get; set; } = [];
}
