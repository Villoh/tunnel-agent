using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Fetches model context lengths from OpenRouter's public /v1/models endpoint.
/// Results are cached in-memory for the lifetime of the app.
/// </summary>
public sealed class OpenRouterContextService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string Url = "https://openrouter.ai/api/v1/models";

    private Dictionary<string, int>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static readonly OpenRouterContextService Instance = new();

    private OpenRouterContextService() { }

    /// <summary>
    /// Returns the context length for a model id, or null if not found.
    /// Normalizes dots to dashes before matching to bridge e.g. "claude-sonnet-4.6" (OpenRouter) vs "claude-sonnet-4-6" (proxy).
    /// </summary>
    public async Task<int?> GetContextLengthAsync(string modelId, CancellationToken ct = default)
    {
        var map = await GetOrFetchAsync(ct).ConfigureAwait(false);
        if (map is null) return null;

        var needle = Normalize(modelId);

        foreach (var (key, value) in map)
        {
            // Strip provider prefix (e.g. "anthropic/claude-sonnet-4.6" -> "claude-sonnet-4.6")
            var bare = key.Contains('/') ? key[(key.IndexOf('/') + 1)..] : key;
            // Skip deprecated/aliased entries prefixed with ~
            if (bare.StartsWith('~')) continue;
            if (Normalize(bare).Equals(needle, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    private static string Normalize(string id) => id.Replace('.', '-');

    private async Task<Dictionary<string, int>?> GetOrFetchAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;

            using var resp = await Http.GetAsync(Url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var data = body?["data"]?.AsArray();
            if (data is null) return null;

            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data)
            {
                var id = item?["id"]?.GetValue<string>();
                var ctx = item?["context_length"]?.GetValue<int>();
                if (id is not null && ctx is > 0)
                    map[id] = ctx.Value;
            }

            _cache = map;
            return _cache;
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }
}
