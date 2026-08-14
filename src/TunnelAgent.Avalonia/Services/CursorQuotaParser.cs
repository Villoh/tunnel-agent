using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TunnelAgent.Services;

internal readonly record struct CursorQuotaBarSpec(
    string Title,
    double UsedFraction,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Parses Cursor dashboard usage payloads into quota bars.
/// Pro/Ultra use Auto + Composer and API percents; Team/Enterprise fall back
/// to request counts or usage-summary meters.
/// </summary>
internal static class CursorQuotaParser
{
    internal const string AutoTitle = "Auto + Composer";
    internal const string ApiTitle  = "API";

    internal static string? ParsePlanName(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var name = JsonNode.Parse(json)?["planInfo"]?["planName"]?.GetValue<string>();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    internal static IReadOnlyList<CursorQuotaBarSpec> ParsePeriodUsage(string json)
    {
        var doc = ParseObject(json);
        if (doc is null) return [];

        var reset = ReadTimestamp(doc["billingCycleEnd"]);
        var bars  = new List<CursorQuotaBarSpec>();
        var plan  = doc["planUsage"] as JsonObject;

        var autoPct = ReadNumber(plan?["autoPercentUsed"]);
        var apiPct  = ReadNumber(plan?["apiPercentUsed"]);
        if (autoPct is not null || apiPct is not null)
        {
            if (autoPct is not null)
                bars.Add(PercentBar(AutoTitle, autoPct.Value, reset));
            if (apiPct is not null)
                bars.Add(PercentBar(ApiTitle, apiPct.Value, reset));
        }
        else
        {
            var used  = ReadNumber(plan?["includedSpend"]) ?? 0;
            var limit = ReadNumber(plan?["limit"]) ?? 0;
            if (limit > 0)
            {
                bars.Add(new CursorQuotaBarSpec(
                    $"Included (${FormatCents(used)}/${FormatCents(limit)})",
                    Clamp01(used / limit),
                    reset));
            }
            else if (ReadNumber(plan?["totalPercentUsed"]) is double pct)
            {
                bars.Add(PercentBar("Plan usage", pct, reset));
            }
        }

        var spend = doc["spendLimitUsage"] as JsonObject;
        AddOnDemandBar(bars, spend, reset);
        return bars;
    }

    internal static IReadOnlyList<CursorQuotaBarSpec> ParseAuthUsage(string json)
    {
        var doc = ParseObject(json);
        if (doc is null) return [];

        DateTimeOffset? reset = null;
        if (ReadTimestamp(doc["startOfMonth"]) is DateTimeOffset start)
            reset = start.AddMonths(1);

        var bucket = doc["gpt-4"] as JsonObject
                  ?? FindRequestBucket(doc);
        if (bucket is null) return [];

        var used  = ReadNumber(bucket["numRequests"]) ?? 0;
        var limit = ReadNumber(bucket["maxRequestUsage"]) ?? 0;
        if (limit <= 0) return [];

        return
        [
            new CursorQuotaBarSpec(
                $"Included requests ({FormatCount(used)}/{FormatCount(limit)})",
                Clamp01(used / limit),
                reset)
        ];
    }

    internal static IReadOnlyList<CursorQuotaBarSpec> ParseUsageSummary(string json)
    {
        var doc = ParseObject(json);
        if (doc is null) return [];

        var reset = ReadTimestamp(doc["billingCycleEnd"]);
        var bars  = new List<CursorQuotaBarSpec>();
        AddSummaryMeter(bars, "Included", doc["individualUsage"]?["overall"], reset);
        AddSummaryMeter(bars, "Team", doc["teamUsage"]?["pooled"], reset);
        AddSummaryMeter(bars, "On-demand", doc["teamUsage"]?["onDemand"], reset);
        return bars;
    }

    private static void AddOnDemandBar(List<CursorQuotaBarSpec> bars, JsonObject? spend, DateTimeOffset? reset)
    {
        if (spend is null) return;

        var indLimit = ReadNumber(spend["individualLimit"]) ?? 0;
        var indUsed  = ReadNumber(spend["individualUsed"]) ?? 0;
        if (indLimit > 0)
        {
            bars.Add(new CursorQuotaBarSpec(
                $"On-demand (${FormatCents(indUsed)}/${FormatCents(indLimit)})",
                Clamp01(indUsed / indLimit),
                null));
            return;
        }

        var pooledLimit = ReadNumber(spend["pooledLimit"]) ?? 0;
        var pooledUsed  = ReadNumber(spend["pooledUsed"]) ?? 0;
        if (pooledLimit > 0)
        {
            bars.Add(new CursorQuotaBarSpec(
                $"On-demand (${FormatCents(pooledUsed)}/${FormatCents(pooledLimit)})",
                Clamp01(pooledUsed / pooledLimit),
                reset));
        }
    }

    private static void AddSummaryMeter(
        List<CursorQuotaBarSpec> bars, string label, JsonNode? meter, DateTimeOffset? reset)
    {
        if (meter is not JsonObject obj) return;
        if (obj["enabled"] is JsonValue enabled && enabled.TryGetValue<bool>(out var on) && !on)
            return;

        var used  = ReadNumber(obj["used"]) ?? 0;
        var limit = ReadNumber(obj["limit"]) ?? 0;
        if (limit <= 0) return;

        bars.Add(new CursorQuotaBarSpec(
            $"{label} ({FormatCount(used)}/{FormatCount(limit)})",
            Clamp01(used / limit),
            reset));
    }

    private static JsonObject? FindRequestBucket(JsonObject doc)
    {
        foreach (var kv in doc)
        {
            if (kv.Key is "startOfMonth" or "gpt-4") continue;
            if (kv.Value is JsonObject obj && (ReadNumber(obj["maxRequestUsage"]) ?? 0) > 0)
                return obj;
        }
        return null;
    }

    private static CursorQuotaBarSpec PercentBar(string title, double percent, DateTimeOffset? reset) =>
        new(title, Clamp01(percent / 100.0), reset);

    private static JsonObject? ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch { return null; }
    }

    internal static double? ReadNumber(JsonNode? node)
    {
        if (node is not JsonValue value) return null;

        if (value.TryGetValue<JsonElement>(out var el))
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var n) && double.IsFinite(n))
                return n;
            if (el.ValueKind == JsonValueKind.String)
                return ParseNumericString(el.GetString());
            return null;
        }

        if (value.TryGetValue<double>(out var d) && double.IsFinite(d)) return d;
        if (value.TryGetValue<long>(out var l)) return l;
        if (value.TryGetValue<int>(out var i)) return i;
        if (value.TryGetValue<decimal>(out var m)) return (double)m;
        if (value.TryGetValue<string>(out var s)) return ParseNumericString(s);
        return null;
    }

    internal static DateTimeOffset? ReadTimestamp(JsonNode? node)
    {
        if (node is null) return null;

        if (ReadNumber(node) is double numeric)
        {
            var unix = (long)numeric;
            return unix > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                : DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var s) &&
            DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            return dt;

        return null;
    }

    private static double? ParseNumericString(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
            ? n : null;
    }

    private static string FormatCents(double cents) =>
        (cents / 100.0).ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatCount(double value) =>
        value.ToString("0", CultureInfo.InvariantCulture);

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);
}
