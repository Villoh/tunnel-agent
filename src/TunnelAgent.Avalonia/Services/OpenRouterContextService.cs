using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Fetches model metadata from OpenRouter's public /v1/models endpoint.
/// Results are cached in-memory for the lifetime of the app.
/// </summary>
public sealed class OpenRouterContextService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string Url = "https://openrouter.ai/api/v1/models";

    public sealed record ModelInfo(int ContextLength, bool SupportsImage);

    private Dictionary<string, ModelInfo>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static readonly OpenRouterContextService Instance = new();

    private OpenRouterContextService() { }

    /// <summary>
    /// Returns model info for a model id, or null if not found.
    /// Normalizes dots to dashes before matching (e.g. "claude-sonnet-4.6" vs "claude-sonnet-4-6").
    /// Falls back to stripping trailing date suffixes (e.g. "claude-3-5-haiku-20241022" -> "claude-3-5-haiku").
    /// </summary>
    public async Task<ModelInfo?> GetModelInfoAsync(string modelId, CancellationToken ct = default)
    {
        var map = await GetOrFetchAsync(ct).ConfigureAwait(false);
        if (map is null) return null;

        foreach (var needle in new[] { Normalize(modelId), Normalize(StripDateSuffix(modelId)) })
        {
            foreach (var (key, info) in map)
            {
                var bare = key.Contains('/') ? key[(key.IndexOf('/') + 1)..] : key;
                if (bare.StartsWith('~')) continue;
                if (Normalize(bare).Equals(needle, StringComparison.OrdinalIgnoreCase))
                    return info;
            }
        }

        return null;
    }

    private static string Normalize(string id) => id.Replace('.', '-');

    private static string StripDateSuffix(string id)
    {
        var idx = id.Length - 8;
        if (idx > 1 && id[idx - 1] == '-' && id[idx..].All(char.IsDigit))
            return id[..(idx - 1)];
        return id;
    }

    private async Task<Dictionary<string, ModelInfo>?> GetOrFetchAsync(CancellationToken ct)
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

            var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data)
            {
                var id  = item?["id"]?.GetValue<string>();
                var ctx = item?["context_length"]?.GetValue<int>() ?? 0;
                if (id is null) continue;
                var modalities  = item?["architecture"]?["input_modalities"]?.AsArray();
                var supportsImg = modalities?.Any(m => m?.GetValue<string>() == "image") ?? false;
                map[id] = new ModelInfo(ctx, supportsImg);
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
