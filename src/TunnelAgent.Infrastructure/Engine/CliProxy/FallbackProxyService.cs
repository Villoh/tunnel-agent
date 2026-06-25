using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>
/// In-process reverse proxy that sits between coding agents and CLIProxyAPI to add
/// experimental model fallback.
///
/// <para>
/// Flow: agent → FallbackProxyService (public port) → CLIProxyAPI (internal port).
/// When a request targets a configured <see cref="VirtualModel"/>, the proxy rewrites
/// the request body's <c>model</c> field to each entry in priority order, retrying on
/// quota exhaustion / retryable upstream errors until one succeeds or the chain ends.
/// All other traffic is forwarded transparently.
/// </para>
/// </summary>
public sealed class FallbackProxyService
{
    private static readonly HttpClient Upstream = new(new SocketsHttpHandler
    {
        // Forward bytes verbatim; we never want the proxy to transparently decompress
        // or buffer responses, so streaming endpoints keep working.
        AutomaticDecompression = DecompressionMethods.None,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    /// <summary>How many bytes of a response are inspected to decide whether to fall back.</summary>
    private const int InspectionBytes = 4096;

    private static readonly string[] RetryableBodyPatterns =
    [
        "quota exceeded", "rate limit", "limit reached", "no available account",
        "insufficient_quota", "resource_exhausted", "overloaded", "capacity",
        "too many requests", "throttl", "model not found", "model unavailable",
        "does not exist"
    ];

    private static readonly int[] RetryableStatusCodes = [429, 500, 503];

    private static readonly ConcurrentDictionary<string, FallbackRouteState> RouteStates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised whenever a virtual model starts using a provider/model entry.</summary>
    public static event Action<FallbackRouteState>? RouteStateChanged;

    /// <summary>Raised when the bridge stops and runtime route states are cleared.</summary>
    public static event Action? RouteStatesCleared;

    /// <summary>Returns the latest known route states, keyed by virtual model name.</summary>
    public static IReadOnlyDictionary<string, FallbackRouteState> SnapshotRouteStates() =>
        new Dictionary<string, FallbackRouteState>(RouteStates, StringComparer.OrdinalIgnoreCase);

    private readonly Func<FallbackConfiguration> _configProvider;

    private readonly Action<string>? _log;

    // virtual model name -> (entry id, cached at). Reused while route caching is on.
    private readonly ConcurrentDictionary<string, (string EntryId, DateTime CachedAt)> _routeCache = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private string _targetBaseUrl = "";

    public bool IsRunning { get; private set; }
    public int Port { get; private set; }

    /// <summary>
    /// Derives the internal port CLIProxyAPI binds to when the bridge owns the public port.
    /// Offsets by 1000 to stay clear of neighbouring engine ports, wrapping if out of range.
    /// </summary>
    public static int InternalPortFor(int publicPort)
    {
        var candidate = publicPort + 1000;
        if (candidate > 65535) candidate = publicPort - 1000;
        return candidate < 1 ? publicPort + 1 : candidate;
    }

    public FallbackProxyService(
        Func<FallbackConfiguration> configProvider,
        Action<string>? log = null)
    {
        _configProvider = configProvider;
        _log = log;
    }

    /// <summary>
    /// Starts the bridge listening on <paramref name="publicPort"/> and forwarding to
    /// CLIProxyAPI on <paramref name="internalPort"/>. Safe to call when already running
    /// (it restarts on the new ports).
    /// </summary>
    public void Start(int publicPort, int internalPort)
    {
        Stop();

        _targetBaseUrl = $"http://127.0.0.1:{internalPort}";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{publicPort}/");
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();
        Port = publicPort;
        IsRunning = true;

        _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
        _log?.Invoke($"Fallback bridge listening on :{publicPort} → :{internalPort}");
    }

    public void Stop()
    {
        if (!IsRunning && _listener is null) return;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _listener?.Stop(); } catch { /* ignore */ }
        try { _listener?.Close(); } catch { /* ignore */ }

        _listener = null;
        _cts = null;
        IsRunning = false;
        _routeCache.Clear();
        RouteStates.Clear();
        RouteStatesCleared?.Invoke();
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                return; // listener stopped
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Fallback bridge accept error: {ex.Message}");
                return;
            }

            _ = Task.Run(() => HandleAsync(context, ct), ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            if (IsModelsListRequest(context.Request))
            {
                await HandleModelsListAsync(context, ct);
                return;
            }

            var requestBody = await ReadRequestBodyAsync(context.Request);
            var virtualModel = TryResolveVirtualModel(context.Request, requestBody, out var requestJson);

            if (virtualModel is null || requestJson is null)
            {
                await ForwardTransparentAsync(context, requestBody, ct);
                return;
            }

            await ForwardWithFallbackAsync(context, virtualModel, requestJson, ct);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Fallback bridge request error: {ex.Message}");
            TryWriteError(context, 502, "Fallback bridge error.");
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private static bool IsModelsListRequest(HttpListenerRequest request) =>
        string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
        && string.Equals(request.Url?.AbsolutePath.TrimEnd('/'), "/v1/models", StringComparison.OrdinalIgnoreCase);

    private async Task HandleModelsListAsync(HttpListenerContext context, CancellationToken ct)
    {
        using var upstreamRequest = BuildUpstreamRequest(context.Request, []);
        using var upstreamResponse = await Upstream.SendAsync(upstreamRequest, HttpCompletionOption.ResponseContentRead, ct);
        var body = await upstreamResponse.Content.ReadAsByteArrayAsync(ct);

        if ((int)upstreamResponse.StatusCode is not (>= 200 and < 300))
        {
            await using var ms = new MemoryStream(body);
            await WriteResponseAsync(context.Response, upstreamResponse, (int)upstreamResponse.StatusCode, ReadOnlyMemory<byte>.Empty, ms, ct);
            return;
        }

        var merged = TryAppendVirtualModels(body);
        if (merged is null)
        {
            await using var ms = new MemoryStream(body);
            await WriteResponseAsync(context.Response, upstreamResponse, (int)upstreamResponse.StatusCode, ReadOnlyMemory<byte>.Empty, ms, ct);
            return;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = "application/json";
        context.Response.SendChunked = true;
        await context.Response.OutputStream.WriteAsync(merged, ct);
        await context.Response.OutputStream.FlushAsync(ct);
    }

    private byte[]? TryAppendVirtualModels(byte[] upstreamBody)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(upstreamBody); }
        catch { return null; }

        if (root is not JsonObject obj) return null;
        if (obj["data"] is not JsonArray data) return null;

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.OfType<JsonObject>())
        {
            if (item["id"]?.GetValue<string>() is { Length: > 0 } id)
                existing.Add(id);
        }

        foreach (var model in _configProvider().VirtualModels.Where(m => m.Enabled && m.Entries.Count > 0))
        {
            if (string.IsNullOrWhiteSpace(model.Name) || !existing.Add(model.Name)) continue;
            data.Add(new JsonObject
            {
                ["id"] = model.Name,
                ["object"] = "model",
                ["created"] = 0,
                ["owned_by"] = "tunnel-agent-fallback"
            });
        }

        return Encoding.UTF8.GetBytes(obj.ToJsonString());
    }

    private static async Task<byte[]> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody) return [];
        using var ms = new MemoryStream();
        await request.InputStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Returns the matching enabled virtual model when the request is a JSON body whose
    /// <c>model</c> equals a configured virtual model name; otherwise null.
    /// </summary>
    private VirtualModel? TryResolveVirtualModel(HttpListenerRequest request, byte[] body, out JsonObject? json)
    {
        json = null;
        if (body.Length == 0) return null;
        if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)) return null;

        var config = _configProvider();
        if (!config.HasActiveRoutes) return null;

        JsonNode? node;
        try { node = JsonNode.Parse(body); }
        catch { return null; }

        if (node is not JsonObject obj) return null;
        if (obj["model"]?.GetValue<string>() is not { Length: > 0 } modelName) return null;

        var model = config.FindVirtualModel(modelName);
        if (model is null || !model.Enabled || model.Entries.Count == 0) return null;

        json = obj;
        return model;
    }

    private static void UpdateRouteState(string virtualModelName, FallbackEntry entry, int entryIndex, int totalEntries)
    {
        var state = new FallbackRouteState
        {
            VirtualModelName = virtualModelName,
            CurrentEntryIndex = entryIndex,
            TotalEntries = totalEntries,
            ProviderId = entry.ProviderId,
            ProviderDisplayName = entry.ProviderDisplayName,
            ModelId = entry.ModelId,
            LastUpdatedUtc = DateTime.UtcNow
        };

        RouteStates[virtualModelName] = state;
        RouteStateChanged?.Invoke(state);
    }

    private async Task ForwardWithFallbackAsync(
        HttpListenerContext context, VirtualModel model, JsonObject requestJson, CancellationToken ct)
    {
        var entries = model.SortedEntries.ToList();
        var ordered = ApplyRouteCache(model.Name, entries);

        for (var i = 0; i < ordered.Count; i++)
        {
            var entry = ordered[i];
            var isLast = i == ordered.Count - 1;
            var chainIndex = Math.Max(0, entries.FindIndex(e => e.Id == entry.Id));

            UpdateRouteState(model.Name, entry, chainIndex, entries.Count);
            requestJson["model"] = entry.ModelId;
            var body = Encoding.UTF8.GetBytes(requestJson.ToJsonString());

            using var upstreamRequest = BuildUpstreamRequest(context.Request, body);
            HttpResponseMessage upstreamResponse;
            try
            {
                upstreamResponse = await Upstream.SendAsync(
                    upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Fallback entry '{entry.ModelId}' transport error: {ex.Message}");
                if (isLast) { TryWriteError(context, 502, "All fallback entries failed."); return; }
                continue;
            }

            using (upstreamResponse)
            {
                var status = (int)upstreamResponse.StatusCode;
                await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);

                // Inspect the response prefix to decide whether to retry the next entry.
                var prefix = await ReadPrefixAsync(upstreamStream, InspectionBytes, ct);

                if (!isLast && ShouldRetry(status, upstreamResponse, prefix))
                {
                    _log?.Invoke($"Fallback '{model.Name}': entry '{entry.ModelId}' failed (HTTP {status}); trying next.");
                    continue;
                }

                if (status is >= 200 and < 300)
                    CacheRoute(model.Name, entry.Id);

                await WriteResponseAsync(context.Response, upstreamResponse, status, prefix, upstreamStream, ct);
                return;
            }
        }
    }

    private async Task ForwardTransparentAsync(HttpListenerContext context, byte[] requestBody, CancellationToken ct)
    {
        using var upstreamRequest = BuildUpstreamRequest(context.Request, requestBody);
        using var upstreamResponse = await Upstream.SendAsync(
            upstreamRequest, HttpCompletionOption.ResponseHeadersRead, ct);

        var status = (int)upstreamResponse.StatusCode;
        await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(ct);
        await WriteResponseAsync(context.Response, upstreamResponse, status, ReadOnlyMemory<byte>.Empty, upstreamStream, ct);
    }

    private HttpRequestMessage BuildUpstreamRequest(HttpListenerRequest source, byte[] body)
    {
        var target = _targetBaseUrl + source.RawUrl;
        var request = new HttpRequestMessage(new HttpMethod(source.HttpMethod), target);

        var hasBody = body.Length > 0
            || string.Equals(source.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.HttpMethod, "PUT", StringComparison.OrdinalIgnoreCase)
            || string.Equals(source.HttpMethod, "PATCH", StringComparison.OrdinalIgnoreCase);

        if (hasBody)
            request.Content = new ByteArrayContent(body);

        foreach (string? key in source.Headers.AllKeys)
        {
            if (key is null) continue;
            if (IsRestrictedRequestHeader(key)) continue;

            var values = source.Headers.GetValues(key);
            if (values is null) continue;

            if (!request.Headers.TryAddWithoutValidation(key, values) && request.Content is not null)
                request.Content.Headers.TryAddWithoutValidation(key, values);
        }

        return request;
    }

    private static async Task<ReadOnlyMemory<byte>> ReadPrefixAsync(Stream stream, int max, CancellationToken ct)
    {
        var buffer = new byte[max];
        var total = 0;
        while (total < max)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, max - total), ct);
            if (read == 0) break;
            total += read;
        }
        return new ReadOnlyMemory<byte>(buffer, 0, total);
    }

    private async Task WriteResponseAsync(
        HttpListenerResponse response,
        HttpResponseMessage upstream,
        int status,
        ReadOnlyMemory<byte> prefix,
        Stream remainder,
        CancellationToken ct)
    {
        response.StatusCode = status;
        try { response.StatusDescription = upstream.ReasonPhrase ?? ""; } catch { /* ignore */ }

        CopyResponseHeaders(upstream, response);
        response.SendChunked = true; // framing is managed by the bridge for streaming safety

        if (!prefix.IsEmpty)
            await response.OutputStream.WriteAsync(prefix, ct);

        await remainder.CopyToAsync(response.OutputStream, ct);
        await response.OutputStream.FlushAsync(ct);
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstream, HttpListenerResponse response)
    {
        void Copy(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
        {
            foreach (var header in headers)
            {
                if (IsRestrictedResponseHeader(header.Key)) continue;
                try { response.Headers[header.Key] = string.Join(",", header.Value); }
                catch { /* skip headers HttpListener manages itself */ }
            }
        }

        Copy(upstream.Headers);
        if (upstream.Content is not null)
            Copy(upstream.Content.Headers);
    }

    private bool ShouldRetry(int status, HttpResponseMessage response, ReadOnlyMemory<byte> prefix)
    {
        if (RetryableStatusCodes.Contains(status)) return true;
        if (status is 401 or 403) return true;

        // Only scan the body for patterns when it is plain text (not compressed/binary).
        var encoding = response.Content?.Headers.ContentEncoding;
        if (encoding is { Count: > 0 }) return false;
        if (prefix.IsEmpty) return false;

        var text = Encoding.UTF8.GetString(prefix.Span).ToLowerInvariant();
        return RetryableBodyPatterns.Any(text.Contains);
    }

    // ── Route cache ──────────────────────────────────────────────────────────

    private List<FallbackEntry> ApplyRouteCache(string modelName, List<FallbackEntry> entries)
    {
        if (!_configProvider().RouteCachingEnabled) return entries;
        if (!_routeCache.TryGetValue(modelName, out var cached)) return entries;

        if (DateTime.UtcNow - cached.CachedAt > TimeSpan.FromMinutes(Math.Clamp(_configProvider().RouteCacheMinutes, 1, 24 * 60)))
        {
            _routeCache.TryRemove(modelName, out _);
            return entries;
        }

        var index = entries.FindIndex(e => e.Id == cached.EntryId);
        if (index <= 0) return entries;

        // Try the cached (last working) entry first, keeping the rest as further fallbacks.
        var reordered = new List<FallbackEntry>(entries.Count) { entries[index] };
        reordered.AddRange(entries.Where((_, i) => i != index));
        return reordered;
    }

    private void CacheRoute(string modelName, string entryId)
    {
        if (!_configProvider().RouteCachingEnabled) return;
        _routeCache[modelName] = (entryId, DateTime.UtcNow);
    }

    public void ClearRouteCache() => _routeCache.Clear();

    // ── Header filters ─────────────────────────────────────────────────────────

    private static bool IsRestrictedRequestHeader(string key) =>
        key.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);

    private static bool IsRestrictedResponseHeader(string key) =>
        key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || key.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase);

    private void TryWriteError(HttpListenerContext context, int status, string message)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var payload = Encoding.UTF8.GetBytes($"{{\"error\":{{\"message\":\"{message}\"}}}}");
            context.Response.OutputStream.Write(payload, 0, payload.Length);
        }
        catch { /* response may already be committed */ }
    }
}
