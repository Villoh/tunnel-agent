using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace TunnelAgent.ViewModels;

/// <summary>
/// A single per-request usage record drained from the CLIProxyAPI management
/// <c>/usage-queue</c> endpoint. Carries the full token breakdown, identity and
/// status used by the dashboard aggregates. Mirrors quotio-desktop's UsageEvent.
/// The queue is a destructive ~60s buffer, so events are deduped by
/// <see cref="EventHash"/> as they accumulate.
/// </summary>
public sealed class UsageEvent
{
    public string   EventHash { get; init; } = "";
    public DateTime Timestamp { get; init; }
    public string   RequestId { get; init; } = "";
    public string?  Provider  { get; init; }
    public string   Model     { get; init; } = "";
    public string?  Source    { get; init; }
    public string?  Path      { get; init; }

    public long InputTokens         { get; init; }
    public long OutputTokens        { get; init; }
    public long ReasoningTokens     { get; init; }
    public long CachedTokens        { get; init; }
    public long CacheCreationTokens { get; init; }
    public long CacheReadTokens     { get; init; }
    public long TotalTokens         { get; init; }

    public long LatencyMs  { get; init; }
    public bool Failed     { get; init; }
    public int? StatusCode { get; init; }

    public bool IsSuccess => !Failed;

    /// <summary>Parse a single <c>/usage-queue</c> JSON record. Returns null when the node is unusable.</summary>
    public static UsageEvent? FromJson(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;

        var tokens = obj["tokens"] as JsonObject;
        var usage  = obj["usage"] as JsonObject;
        var input  = ReadLong(tokens, "input", "input_tokens");
        var output = ReadLong(tokens, "output", "output_tokens");
        var reasoning = ReadLong(tokens, "reasoning", "reasoning_tokens");
        if (reasoning == 0) reasoning = ReadNestedLong(tokens, "output_token_details", "reasoning_tokens");
        if (reasoning == 0) reasoning = ReadNestedLong(tokens, "completion_tokens_details", "reasoning_tokens");
        if (reasoning == 0) reasoning = ReadNestedLong(usage, "output_tokens_details", "reasoning_tokens");
        if (reasoning == 0) reasoning = ReadNestedLong(usage, "completion_tokens_details", "reasoning_tokens");
        var cached = ReadLong(tokens, "cached", "cached_tokens");
        var cacheCreation = ReadLong(tokens, "cache_creation", "cache_creation_tokens");
        var cacheRead = ReadLong(tokens, "cache_read", "cache_read_tokens");
        var total = ReadLong(tokens, "total", "total_tokens");
        if (total == 0) total = input + output;

        var failed = ReadBool(obj, "failed");
        var endpoint = ReadString(obj, "endpoint");
        var (_, path) = SplitEndpoint(endpoint);
        var model = ReadString(obj, "model") ?? ReadString(obj, "alias") ?? "";
        var provider = ReadString(obj, "provider");
        var source = ReadString(obj, "source") ?? ReadString(obj, "account") ?? ReadString(obj, "email");
        var latency = ReadLong(obj, "latency_ms");
        var requestId = ReadString(obj, "request_id") ?? "";
        var timestamp = ParseTimestamp(obj["timestamp"]);

        int? status = null;
        if (obj["fail"] is JsonObject fail && ReadLong(fail, "status_code") is var sc && sc > 0)
            status = (int)sc;
        status ??= failed ? 500 : 200;

        var hashKey = string.Join('|', requestId, timestamp.Ticks, model, endpoint ?? "", latency, source ?? "", total);

        return new UsageEvent
        {
            EventHash = Sha256(hashKey),
            Timestamp = timestamp,
            RequestId = requestId,
            Provider  = provider,
            Model     = model,
            Source    = string.IsNullOrWhiteSpace(source) ? null : source,
            Path      = path,
            InputTokens = input,
            OutputTokens = output,
            ReasoningTokens = reasoning,
            CachedTokens = cached,
            CacheCreationTokens = cacheCreation,
            CacheReadTokens = cacheRead,
            TotalTokens = total,
            LatencyMs = latency,
            Failed = failed,
            StatusCode = status,
        };
    }

    private static long ReadLong(JsonObject? obj, params string[] keys)
    {
        if (obj is null) return 0;
        foreach (var key in keys)
        {
            if (obj.TryGetPropertyValue(key, out var n) && n is not null)
            {
                try { return n.GetValue<long>(); }
                catch
                {
                    try { return (long)n.GetValue<double>(); } catch { /* ignore */ }
                }
            }
        }
        return 0;
    }

    private static long ReadNestedLong(JsonObject? obj, string parentKey, params string[] keys)
    {
        if (obj is null) return 0;
        return obj.TryGetPropertyValue(parentKey, out var node) && node is JsonObject nested
            ? ReadLong(nested, keys)
            : 0;
    }

    private static string? ReadString(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var n) && n is not null)
        {
            try { return n.GetValue<string>(); } catch { return null; }
        }
        return null;
    }

    private static bool ReadBool(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var n) && n is not null)
        {
            try { return n.GetValue<bool>(); } catch { return false; }
        }
        return false;
    }

    private static (string? method, string? path) SplitEndpoint(string? endpoint)
    {
        var value = endpoint?.Trim();
        if (string.IsNullOrEmpty(value)) return (null, null);
        var space = value.IndexOf(' ');
        if (space > 0) return (value[..space], value[(space + 1)..].Trim());
        return value.StartsWith('/') ? (null, value) : (value, null);
    }

    private static DateTime ParseTimestamp(JsonNode? node)
    {
        if (node is null) return DateTime.Now;
        try
        {
            if (node.GetValueKind() == System.Text.Json.JsonValueKind.String)
            {
                var text = node.GetValue<string>();
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
                    return dto.LocalDateTime;
                if (long.TryParse(text, out var unix)) return FromUnix(unix);
            }
            else
            {
                return FromUnix((long)node.GetValue<double>());
            }
        }
        catch { /* fall through */ }
        return DateTime.Now;
    }

    private static DateTime FromUnix(long value)
    {
        // Coerce seconds or milliseconds to a local DateTime.
        var ms = value >= 1_000_000_000_000 ? value : value * 1000;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
    }

    private static string Sha256(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
