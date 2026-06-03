using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Fetches the model list from the running proxy's /v1/models endpoint
/// and populates AvailableModelGroups on the ViewModel.
/// </summary>
public sealed class ModelFetchService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly SettingsService _settings;

    public ModelFetchService(SettingsService settings) => _settings = settings;

    /// <summary>
    /// Fetch /v1/models from the running proxy and update AvailableModelGroups.
    /// Call when engine transitions to Running. Pass the active engine's port.
    /// </summary>
    public async Task FetchAndApplyAsync(
        System.Collections.ObjectModel.ObservableCollection<AvailableModelGroupViewModel> groups,
        int port,
        string? engineId = null,
        CancellationToken ct = default)
    {
        var url  = $"http://127.0.0.1:{port}/v1/models";

        // Poll until models are available (CLIProxy loads auth/models after health check).
        // Max 30 seconds: 1 immediate attempt + 14 retries every 2s.
        const int pollIntervalMs = 2000;
        const int maxAttempts    = 15;
        JsonArray? data          = null;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (ct.IsCancellationRequested) return;
            if (attempt > 0) await Task.Delay(pollIntervalMs, ct);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                var apiKey = TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY") ?? "";
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                using var resp = await Http.SendAsync(request, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                var candidate = body?["data"]?.AsArray();
                if (candidate is { Count: > 0 }) { data = candidate; break; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch { /* server not ready — retry */ }
        }

        if (data is null) return;

        try
        {

            // Group by owned_by
            var byOwner = new Dictionary<string, List<(string id, string ownedBy)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data)
            {
                var id      = item?["id"]?.GetValue<string>()       ?? "";
                var ownedBy = item?["owned_by"]?.GetValue<string>() ?? "unknown";
                if (string.IsNullOrEmpty(id)) continue;

                if (!byOwner.TryGetValue(ownedBy, out var list))
                    byOwner[ownedBy] = list = new List<(string, string)>();
                list.Add((id, ownedBy));
            }

            Dispatcher.UIThread.Post(() =>
            {
                groups.Clear();

                foreach (var (owner, models) in byOwner.OrderBy(k => k.Key))
                {
                    var effectiveOwner = engineId == TunnelAgent.Core.Engine.EngineCatalog.PerplexityWebUiScraper.Id &&
                                         string.Equals(owner, "openai", StringComparison.OrdinalIgnoreCase)
                        ? "perplexity"
                        : owner;
                    var displayName = OwnerDisplayName(effectiveOwner);
                    var icon        = ProviderIconRegistry.Get(effectiveOwner);
                    var group       = new AvailableModelGroupViewModel(displayName, effectiveOwner, icon.IconKind, icon.LogoColor, icon.CustomIconData);

                    foreach (var (id, _) in models.OrderBy(m => m.id))
                    {
                        var authKind = AuthKindFromOwner(effectiveOwner);
                        group.Models.Add(new AvailableModelViewModel(id, authKind, context: "", displayName));
                    }

                    if (group.Models.Count > 0)
                        groups.Add(group);
                }
            });
        }
        catch (OperationCanceledException) { }
        catch { /* server not reachable — leave groups as-is */ }
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string OwnerDisplayName(string ownedBy) => ownedBy.ToLowerInvariant() switch
    {
        "anthropic"      => "Anthropic",
        "openai"         => "OpenAI",
        "google"         => "Google",
        "github-copilot" => "GitHub Copilot",
        "moonshot"       => "Kimi",
        "alibaba"        => "Qwen",
        _                => Titlecase(ownedBy),
    };

    private static string AuthKindFromOwner(string ownedBy) => ownedBy.ToLowerInvariant() switch
    {
        "anthropic"      => "OAuth",
        "openai"         => "OAuth",
        "google"         => "OAuth",
        "github-copilot" => "OAuth",
        _                => "API Key",
    };

    private static string Titlecase(string s) =>
        string.IsNullOrEmpty(s) ? s :
        char.ToUpperInvariant(s[0]) + s[1..].Replace("-", " ");
}
