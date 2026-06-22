using System;
using System.Collections.Generic;
using System.Linq;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>USD price per 1M tokens for a model.</summary>
public readonly record struct ModelPrice(double PromptPer1M, double CompletionPer1M, double CachePer1M);

/// <summary>
/// Estimates request cost from token usage, mirroring quotio-desktop's formula:
/// <c>max(input-cached,0)*prompt/1e6 + output*completion/1e6 + cached*cache/1e6</c>.
/// Prices are matched by longest model-id prefix; unknown models cost 0 (like the
/// proxy's LEFT JOIN with COALESCE), so the cost figure is a best-effort estimate.
/// </summary>
public static class ModelPricing
{
    // Public list prices (USD per 1M tokens). Matched by prefix, longest match wins.
    private static readonly Dictionary<string, ModelPrice> Prices = new(StringComparer.OrdinalIgnoreCase)
    {
        // OpenAI
        ["gpt-4o-mini"]       = new(0.15, 0.60, 0.075),
        ["gpt-4o"]            = new(2.50, 10.00, 1.25),
        ["gpt-4.1-mini"]      = new(0.40, 1.60, 0.10),
        ["gpt-4.1"]           = new(2.00, 8.00, 0.50),
        ["o3-mini"]           = new(1.10, 4.40, 0.55),
        ["o3"]                = new(2.00, 8.00, 0.50),
        ["gpt-5-mini"]        = new(0.25, 2.00, 0.025),
        ["gpt-5"]             = new(1.25, 10.00, 0.125),
        // Anthropic Claude
        ["claude-3-5-haiku"]  = new(0.80, 4.00, 0.08),
        ["claude-3-5-sonnet"] = new(3.00, 15.00, 0.30),
        ["claude-3-7-sonnet"] = new(3.00, 15.00, 0.30),
        ["claude-haiku-4"]    = new(1.00, 5.00, 0.10),
        ["claude-sonnet-4"]   = new(3.00, 15.00, 0.30),
        ["claude-opus-4"]     = new(15.00, 75.00, 1.50),
        ["claude-3-opus"]     = new(15.00, 75.00, 1.50),
        // Google Gemini
        ["gemini-1.5-flash"]  = new(0.075, 0.30, 0.01875),
        ["gemini-1.5-pro"]    = new(1.25, 5.00, 0.3125),
        ["gemini-2.0-flash"]  = new(0.10, 0.40, 0.025),
        ["gemini-2.5-flash"]  = new(0.30, 2.50, 0.075),
        ["gemini-2.5-pro"]    = new(1.25, 10.00, 0.3125),
        ["gemini-3-flash"]    = new(0.30, 2.50, 0.075),
        ["gemini-3-pro"]      = new(2.00, 12.00, 0.50),
    };

    public static double CostFor(UsageEvent e)
    {
        if (!TryGetPrice(e.Model, out var p)) return 0;
        var billedInput = Math.Max(e.InputTokens - e.CachedTokens, 0);
        return billedInput * p.PromptPer1M / 1_000_000.0
             + e.OutputTokens * p.CompletionPer1M / 1_000_000.0
             + e.CachedTokens * p.CachePer1M / 1_000_000.0;
    }

    /// <summary>True when at least one event maps to a known price (drives whether cost is shown).</summary>
    public static bool HasKnownPrice(IEnumerable<UsageEvent> events) =>
        events.Any(e => TryGetPrice(e.Model, out _));

    private static bool TryGetPrice(string model, out ModelPrice price)
    {
        price = default;
        if (string.IsNullOrWhiteSpace(model)) return false;
        if (Prices.TryGetValue(model, out price)) return true;

        string? bestKey = null;
        foreach (var key in Prices.Keys)
        {
            if (model.StartsWith(key, StringComparison.OrdinalIgnoreCase) &&
                (bestKey is null || key.Length > bestKey.Length))
                bestKey = key;
        }
        if (bestKey is not null) { price = Prices[bestKey]; return true; }
        return false;
    }
}
