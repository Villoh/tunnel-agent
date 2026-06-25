using System;
using System.Collections.Generic;
using System.Linq;

namespace TunnelAgent.Services;

/// <summary>
/// A single entry in a fallback chain: a provider/model combination that a virtual
/// model can resolve to. Entries are tried in <see cref="Priority"/> order.
/// </summary>
public sealed class FallbackEntry
{
    /// <summary>Stable identifier used by the UI for reordering/removal.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Provider id matching CLIProxyAPI/Tunnel Agent provider keys (e.g. "antigravity").</summary>
    public string ProviderId { get; set; } = "";

    /// <summary>Friendly provider name for display (empty = fall back to <see cref="ProviderId"/>).</summary>
    public string ProviderDisplayName { get; set; } = "";

    /// <summary>Real upstream model id this entry resolves to.</summary>
    public string ModelId { get; set; } = "";

    /// <summary>1-based position in the chain; lower runs first.</summary>
    public int Priority { get; set; }
}

/// <summary>
/// A virtual model name that maps to an ordered list of real provider/model entries.
/// When a request targets this name, the proxy tries entries in order, advancing to the
/// next on quota exhaustion or other retryable errors.
/// </summary>
public sealed class VirtualModel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The virtual model name agents request (e.g. "gemini-claude-opus-4-5-thinking").</summary>
    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public List<FallbackEntry> Entries { get; set; } = [];

    /// <summary>Entries ordered by ascending priority.</summary>
    public IEnumerable<FallbackEntry> SortedEntries => Entries.OrderBy(e => e.Priority);
}

/// <summary>Runtime state for the route currently serving a virtual model.</summary>
public sealed class FallbackRouteState
{
    public string VirtualModelName { get; set; } = "";
    public int CurrentEntryIndex { get; set; }
    public int TotalEntries { get; set; }
    public string ProviderId { get; set; } = "";
    public string ProviderDisplayName { get; set; } = "";
    public string ModelId { get; set; } = "";
    public DateTime LastUpdatedUtc { get; set; } = DateTime.UtcNow;

    public string ProviderLabel =>
        string.IsNullOrWhiteSpace(ProviderDisplayName) ? ProviderId : ProviderDisplayName;

    public string DisplayString => $"{ProviderLabel} → {ModelId}";

    public string ProgressString => $"{CurrentEntryIndex + 1}/{TotalEntries}";
}

/// <summary>Top-level, persisted model-fallback configuration.</summary>
public sealed class FallbackConfiguration
{
    /// <summary>Master switch. When false, the proxy bridge is bypassed entirely.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When true, a virtual model that successfully resolved to an entry keeps using that
    /// entry for subsequent requests until the cached route expires.
    /// </summary>
    public bool RouteCachingEnabled { get; set; } = true;

    /// <summary>Route cache lifetime in minutes.</summary>
    public int RouteCacheMinutes { get; set; } = 60;

    public List<VirtualModel> VirtualModels { get; set; } = [];

    public VirtualModel? FindVirtualModel(string name) =>
        VirtualModels.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the bridge should run: enabled and at least one usable virtual model exists.</summary>
    public bool HasActiveRoutes =>
        Enabled && VirtualModels.Any(m => m.Enabled && m.Entries.Count > 0);
}
