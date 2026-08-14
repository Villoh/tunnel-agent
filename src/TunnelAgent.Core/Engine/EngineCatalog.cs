using System.Collections.Generic;

namespace TunnelAgent.Core.Engine;

/// <summary>Canonical engine definitions known by TunnelAgent.</summary>
public static class EngineCatalog
{
    public static readonly EngineDefinition CliProxyApi = new(
        "cliproxyapi",
        "CLIProxyAPI",
        "Unified proxy for OAuth and OpenAI-compatible upstream providers.",
        "router-for-me",
        "CLIProxyAPI",
        8317);

    public static readonly EngineDefinition PerplexityWebUiScraper = new(
        "perplexity-webui-scraper",
        "Perplexity WebUI Scraper",
        "OpenAI-compatible local API backed by Perplexity WebUI sessions.",
        "Villoh",
        "perplexity-webui-scraper",
        8327);

    public static readonly EngineDefinition NineRouter = new(
        "9router",
        "9Router",
        "OpenAI-compatible local router for 40+ providers with auto-fallback.",
        "decolua",
        "9router",
        20128);

    public static IReadOnlyList<EngineDefinition> All { get; } =
        [CliProxyApi, PerplexityWebUiScraper, NineRouter];
}
