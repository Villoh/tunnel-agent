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

    public sealed record ModelInfo(int ContextLength, bool SupportsImage, bool SupportsReasoning, string? Name = null);

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

        var bare = modelId.Contains('/') ? modelId[(modelId.LastIndexOf('/') + 1)..] : modelId;

        // Candidates: full id and bare name, each with and without date suffix, normalized dots->dashes
        var candidates = new[]
        {
            Normalize(modelId),
            Normalize(StripDateSuffix(modelId)),
            Normalize(bare),
            Normalize(StripDateSuffix(bare)),
        };

        foreach (var (key, info) in map)
        {
            if (key.StartsWith('~')) continue;
            var keyNorm     = Normalize(key);
            var keyBareNorm = key.Contains('/') ? Normalize(key[(key.LastIndexOf('/') + 1)..]) : keyNorm;
            if (candidates.Any(c => c.Equals(keyNorm, StringComparison.OrdinalIgnoreCase) ||
                                    c.Equals(keyBareNorm, StringComparison.OrdinalIgnoreCase)))
                return info;
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
                // Reasoning support: OpenRouter exposes a `reasoning` object only for models
                // that support it, and lists "reasoning" in `supported_parameters`.
                var supportedParams = item?["supported_parameters"]?.AsArray();
                var supportsReasoning = item?["reasoning"] is not null
                    || (supportedParams?.Any(p => p?.GetValue<string>() == "reasoning") ?? false);
                var name = item?["name"]?.GetValue<string>();
                map[id] = new ModelInfo(ctx, supportsImg, supportsReasoning, name);
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
