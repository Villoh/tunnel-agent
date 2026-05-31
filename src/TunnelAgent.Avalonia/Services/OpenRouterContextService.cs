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
    /// Model id can be bare (e.g. "claude-sonnet-4-6") or prefixed (e.g. "anthropic/claude-sonnet-4-6").
    /// </summary>
    public async Task<int?> GetContextLengthAsync(string modelId, CancellationToken ct = default)
    {
        var map = await GetOrFetchAsync(ct).ConfigureAwait(false);
        if (map is null) return null;

        // Exact match first
        if (map.TryGetValue(modelId, out var exact)) return exact;

        // Try stripping provider prefix from OpenRouter id (e.g. "anthropic/claude-sonnet-4-6" -> "claude-sonnet-4-6")
        foreach (var (key, value) in map)
        {
            var slash = key.IndexOf('/');
            if (slash >= 0 && key[(slash + 1)..].Equals(modelId, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

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
