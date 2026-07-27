using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Fetches model metadata (context length, modalities, reasoning support and
/// list pricing) from the public models.dev catalog.
///
/// The fetched map is cached in memory for the lifetime of the app and also
/// persisted to a JSON file under <see cref="IPlatformInfo.LocalDataDirectory"/>.
/// On startup the disk copy seeds the in-memory cache instantly (works offline);
/// a background refresh re-fetches from the network only when the disk copy is
/// missing or older than <see cref="Ttl"/>, so startup never blocks on models.dev.
/// </summary>
public sealed class ModelsDevService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private const string Url = "https://models.dev/api.json";
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    public sealed record ModelInfo(
        int ContextLength,
        bool SupportsImage,
        bool SupportsReasoning,
        string? Name = null,
        ModelPrice? Pricing = null);

    private Dictionary<string, ModelInfo>? _cache;
    private DateTime _fetchedAtUtc = DateTime.MinValue;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Memoizes price lookups by raw model id so CostFor stays cheap inside Sum loops.
    private readonly ConcurrentDictionary<string, ModelPrice?> _priceMemo = new(StringComparer.OrdinalIgnoreCase);

    private static string CacheFilePath =>
        Path.Combine(IPlatformInfo.Current.LocalDataDirectory, "models-dev.json");

    private static string LegacyCacheFilePath =>
        Path.Combine(IPlatformInfo.Current.LocalDataDirectory, "openrouter-models.json");

    public static readonly ModelsDevService Instance = new();

    private ModelsDevService() { }

    /// <summary>
    /// Returns model info for a model id, or null if not found.
    /// Normalizes dots to dashes before matching (e.g. "claude-sonnet-4.6" vs "claude-sonnet-4-6").
    /// Falls back to stripping trailing date suffixes (e.g. "claude-3-5-haiku-20241022" -> "claude-3-5-haiku").
    /// </summary>
    public async Task<ModelInfo?> GetModelInfoAsync(string modelId, CancellationToken ct = default)
    {
        var map = await GetOrFetchAsync(ct).ConfigureAwait(false);
        return Match(map, modelId);
    }

    /// <summary>
    /// Synchronous price lookup against the already-loaded cache. Returns false when the
    /// cache is empty (not warmed yet) or the model has no known models.dev pricing.
    /// Safe to call from hot aggregation loops; results are memoized per provider/model id.
    /// </summary>
    public bool TryGetPrice(string modelId, out ModelPrice price) =>
        TryGetPrice(null, modelId, out price);

    /// <summary>Looks up provider-specific pricing when a provider id is available.</summary>
    public bool TryGetPrice(string? providerId, string modelId, out ModelPrice price)
    {
        price = default;
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        var map = _cache;
        if (map is null) return false;

        var memoKey = $"{providerId}\n{modelId}";
        var resolved = _priceMemo.GetOrAdd(memoKey, _ => Match(map, modelId, providerId)?.Pricing);
        if (resolved is { } p) { price = p; return true; }
        return false;
    }

    /// <summary>
    /// Synchronously seeds the in-memory cache from the on-disk JSON when present and not
    /// already loaded. Call this before the first cost computation so dashboard figures use
    /// models.dev prices from the start instead of momentarily falling back to the built-in
    /// table. Returns true when the cache is populated.
    /// </summary>
    public bool SeedFromDisk()
    {
        if (_cache is not null) return true;
        _lock.Wait();
        try
        {
            if (_cache is not null) return true;
            return LoadFromDiskLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Warms the cache for startup: seeds from disk first (instant, offline-capable),
    /// invoking <paramref name="onUpdated"/>, then refreshes from the network in the
    /// background when the disk copy is stale, invoking <paramref name="onUpdated"/> again.
    /// </summary>
    public async Task WarmAsync(Action? onUpdated = null, CancellationToken ct = default)
    {
        var seeded = await TryLoadFromDiskAsync(ct).ConfigureAwait(false);
        if (seeded) onUpdated?.Invoke();

        if (!seeded || DateTime.UtcNow - _fetchedAtUtc > Ttl)
        {
            var fetched = await FetchAndPersistAsync(ct).ConfigureAwait(false);
            if (fetched) onUpdated?.Invoke();
        }
    }

    private ModelInfo? Match(Dictionary<string, ModelInfo>? map, string modelId, string? providerId = null)
    {
        if (map is null) return null;

        var bare = modelId.Contains('/') ? modelId[(modelId.LastIndexOf('/') + 1)..] : modelId;
        var candidates = new[]
        {
            Normalize(modelId),
            Normalize(StripDateSuffix(modelId)),
            Normalize(bare),
            Normalize(StripDateSuffix(bare)),
        };

        ModelInfo? Find(bool requireProvider)
        {
            foreach (var (key, info) in map)
            {
                var slash = key.IndexOf('/');
                var keyProvider = slash > 0 ? key[..slash] : string.Empty;
                if (requireProvider && !keyProvider.Equals(providerId, StringComparison.OrdinalIgnoreCase)) continue;

                var keyNorm = Normalize(key);
                var keyBareNorm = slash > 0 ? Normalize(key[(slash + 1)..]) : keyNorm;
                if (candidates.Any(c => c.Equals(keyNorm, StringComparison.OrdinalIgnoreCase) ||
                                        c.Equals(keyBareNorm, StringComparison.OrdinalIgnoreCase)))
                    return info;
            }

            return null;
        }

        return !string.IsNullOrWhiteSpace(providerId) ? Find(true) ?? Find(false) : Find(false);
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

            // Seed from disk first so we have something to work with offline; refresh
            // from the network only when the disk copy is missing or stale.
            LoadFromDiskLocked();
            if (_cache is null || DateTime.UtcNow - _fetchedAtUtc > Ttl)
                await FetchAndPersistLockedAsync(ct).ConfigureAwait(false);

            return _cache;
        }
        catch
        {
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> TryLoadFromDiskAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return true;
            return LoadFromDiskLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> FetchAndPersistAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await FetchAndPersistLockedAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    private bool LoadFromDiskLocked()
    {
        try
        {
            var path = CacheFilePath;
            if (!File.Exists(path)) return false;

            var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
            var models = root?["models"]?.AsObject();
            if (models is null) return false;

            var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, node) in models)
            {
                if (node is not JsonObject m) continue;
                map[id] = new ModelInfo(
                    m["ctx"]?.GetValue<int>() ?? 0,
                    m["img"]?.GetValue<bool>() ?? false,
                    m["reason"]?.GetValue<bool>() ?? false,
                    m["name"]?.GetValue<string>(),
                    ReadDiskPrice(m));
            }
            if (map.Count == 0) return false;

            _cache = map;
            _priceMemo.Clear();
            DeleteLegacyCache();
            var fetchedAt = root?["fetchedAtUtc"]?.GetValue<string>();
            _fetchedAtUtc = DateTime.TryParse(fetchedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                ? dt
                : DateTime.MinValue;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> FetchAndPersistLockedAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        request.Headers.UserAgent.ParseAdd("TunnelAgent/1.0");
        using var resp = await Http.SendAsync(request, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return false;

        var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var map = ParseCatalog(body);
        if (map.Count == 0) return false;

        _cache = map;
        _priceMemo.Clear();
        _fetchedAtUtc = DateTime.UtcNow;
        PersistToDisk(map, _fetchedAtUtc);
        return true;
    }

    internal static Dictionary<string, ModelInfo> ParseCatalog(JsonNode? body)
    {
        var map = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        if (body is not JsonObject providers) return map;

        foreach (var (providerId, providerNode) in providers)
        {
            if (providerNode?["models"] is not JsonObject models) continue;
            foreach (var (modelId, modelNode) in models)
            {
                var ctx = modelNode?["limit"]?["context"]?.GetValue<int>() ?? 0;
                var modalities = modelNode?["modalities"]?["input"]?.AsArray();
                var supportsImage = modalities?.Any(m => m?.GetValue<string>() == "image") ?? false;
                var supportsReasoning = modelNode?["reasoning"]?.GetValue<bool>() ?? false;
                var name = modelNode?["name"]?.GetValue<string>();
                map[$"{providerId}/{modelId}"] = new ModelInfo(
                    ctx,
                    supportsImage,
                    supportsReasoning,
                    name,
                    ReadApiPrice(modelNode?["cost"]));
            }
        }

        return map;
    }

    /// <summary>Parses models.dev pricing, already expressed in USD per one million tokens.</summary>
    private static ModelPrice? ReadApiPrice(JsonNode? pricing)
    {
        if (pricing is not JsonObject p) return null;
        var prompt = ReadNumber(p["input"]);
        var completion = ReadNumber(p["output"]);
        if (prompt is null && completion is null) return null;

        var promptPer1M = prompt ?? 0;
        var completionPer1M = completion ?? 0;
        var cachePer1M = ReadNumber(p["cache_read"]) ?? promptPer1M;
        var cacheWritePer1M = ReadNumber(p["cache_write"]) ?? 0;
        return new ModelPrice(promptPer1M, completionPer1M, cachePer1M, cacheWritePer1M);
    }

    private static double? ReadNumber(JsonNode? node)
    {
        if (node is null) return null;
        try
        {
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.Number)
                return node.GetValue<double>();
            var text = node.GetValue<string>();
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static ModelPrice? ReadDiskPrice(JsonObject m)
    {
        if (m["prompt"] is null && m["completion"] is null) return null;
        return new ModelPrice(
            m["prompt"]?.GetValue<double>() ?? 0,
            m["completion"]?.GetValue<double>() ?? 0,
            m["cache"]?.GetValue<double>() ?? 0,
            m["cacheWrite"]?.GetValue<double>() ?? 0);
    }

    private static void PersistToDisk(Dictionary<string, ModelInfo> map, DateTime fetchedAtUtc)
    {
        try
        {
            var models = new JsonObject();
            foreach (var (id, info) in map)
            {
                var node = new JsonObject
                {
                    ["ctx"] = info.ContextLength,
                    ["img"] = info.SupportsImage,
                    ["reason"] = info.SupportsReasoning,
                    ["name"] = info.Name,
                };
                if (info.Pricing is { } pr)
                {
                    node["prompt"] = pr.PromptPer1M;
                    node["completion"] = pr.CompletionPer1M;
                    node["cache"] = pr.CachePer1M;
                    node["cacheWrite"] = pr.CacheWritePer1M;
                }
                models[id] = node;
            }

            var root = new JsonObject
            {
                ["fetchedAtUtc"] = fetchedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                ["models"] = models,
            };

            var path = CacheFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString());
            DeleteLegacyCache();
        }
        catch
        {
            // Persistence is best-effort; a failed write just means we refetch next launch.
        }
    }

    private static void DeleteLegacyCache()
    {
        try
        {
            File.Delete(LegacyCacheFilePath);
        }
        catch
        {
            // Best-effort cleanup; retry next time the new cache is loaded or written.
        }
    }
}
