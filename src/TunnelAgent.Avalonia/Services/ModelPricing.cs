using System;
using System.Collections.Generic;
using System.Linq;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// USD price per 1M tokens for a model. <see cref="CachePer1M"/> is the cache-read
/// rate; <see cref="CacheWritePer1M"/> is the (more expensive) cache-creation rate,
/// which defaults to 0 — callers treat 0 as "unknown" and fall back to 1.25× prompt.
/// </summary>
public readonly record struct ModelPrice(
    double PromptPer1M,
    double CompletionPer1M,
    double CachePer1M,
    double CacheWritePer1M = 0);

/// <summary>
/// Estimates request cost from token usage. Two billing shapes are supported:
/// <list type="bullet">
/// <item>Anthropic-style (events carry cache creation/read token splits):
/// <c>input*prompt + creation*cacheWrite + read*cacheRead + output*completion</c>,
/// where <c>input</c> is already cache-free.</item>
/// <item>OpenAI-style (only an aggregate cached count, input includes it):
/// <c>max(input-cached,0)*prompt + cached*cacheRead + output*completion</c>.</item>
/// </list>
/// Prices come from models.dev's live catalog (cached on disk via
/// <see cref="ModelsDevService"/>) when available, falling back to the
/// built-in table below; unknown models cost 0 (like the proxy's LEFT JOIN with
/// COALESCE), so the cost figure is a best-effort estimate.
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
        if (!TryResolvePrice(e.Provider, e.Model, out var p)) return 0;

        var cost = e.OutputTokens * p.CompletionPer1M / 1_000_000.0;

        if (e.CacheCreationTokens > 0 || e.CacheReadTokens > 0)
        {
            // Anthropic-style: input_tokens is already cache-free; creation and read
            // tokens are billed separately (creation is the pricey write rate).
            var writeRate = p.CacheWritePer1M > 0 ? p.CacheWritePer1M : p.PromptPer1M * 1.25;
            cost += e.InputTokens * p.PromptPer1M / 1_000_000.0
                  + e.CacheCreationTokens * writeRate / 1_000_000.0
                  + e.CacheReadTokens * p.CachePer1M / 1_000_000.0;
        }
        else
        {
            // OpenAI-style: input_tokens includes the cached (read) portion.
            var billedInput = Math.Max(e.InputTokens - e.CachedTokens, 0);
            cost += billedInput * p.PromptPer1M / 1_000_000.0
                  + e.CachedTokens * p.CachePer1M / 1_000_000.0;
        }

        return cost;
    }

    /// <summary>True when at least one event maps to a known price (drives whether cost is shown).</summary>
    public static bool HasKnownPrice(IEnumerable<UsageEvent> events) =>
        events.Any(e => TryResolvePrice(e.Provider, e.Model, out _));

    /// <summary>models.dev live/cached pricing first, then the built-in table.</summary>
    private static bool TryResolvePrice(string? provider, string model, out ModelPrice price) =>
        ModelsDevService.Instance.TryGetPrice(provider, model, out price) || TryGetPrice(model, out price);

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
