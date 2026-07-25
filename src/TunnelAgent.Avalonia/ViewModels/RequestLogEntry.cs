using System;
using System.Text.RegularExpressions;

namespace TunnelAgent.ViewModels;

/// <summary>
/// A single parsed request entry from the CLIProxyAPI log file.
/// Format: [2026-06-04 11:35:10] [5e01a728] [info ] [gin_logger.go:101] 200 |       14.832s |       127.0.0.1 | POST    "/v1/chat/completions"
/// AI API requests have a real request_id (8 hex chars); all others have "--------".
/// </summary>
public sealed partial class RequestLogEntry
{
    // Groups: 1=datetime  2=request_id  3=level  4=status  5=latency  6=method  7=path
    [GeneratedRegex(
        @"^\[(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] \[([^\]]+)\] \[([^\]]+)\] \[[^\]]+\] (\d{3}) \|\s+([\d.]+(?:ms|µs|s|m\d+s|\S+))\s+\|\s+[\d.:]+\s+\|\s+(\S+)\s+""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex LogLineRegex();

    public DateTime Timestamp  { get; }
    public string   RequestId  { get; }
    public int      StatusCode { get; }
    public string   LatencyRaw { get; }
    public TimeSpan Latency    { get; }
    public string   Method     { get; }
    public string   Path       { get; }
    public bool     IsSuccess  => StatusCode >= 200 && StatusCode < 300;
    public bool     IsError    => StatusCode >= 400;
    public string   Provider   { get; private set; }
    public string   Model      { get; }

    private RequestLogEntry(DateTime ts, string reqId, int status, string latRaw, TimeSpan latency, string method, string path, string? provider = null, string? model = null)
    {
        Timestamp  = ts;
        RequestId  = reqId;
        StatusCode = status;
        LatencyRaw = latRaw;
        Latency    = latency;
        Method     = method;
        Path       = path;
        Provider   = string.IsNullOrWhiteSpace(provider) ? InferProvider(path) : Titlecase(CleanProvider(provider));
        Model      = string.IsNullOrWhiteSpace(model) ? "—" : model.Trim();
    }

    public static RequestLogEntry FromUsageEvent(UsageEvent e)
    {
        var status = e.StatusCode ?? (e.Failed ? 500 : 200);
        var latency = TimeSpan.FromMilliseconds(Math.Max(0, e.LatencyMs));
        return new RequestLogEntry(
            e.Timestamp,
            e.RequestId,
            status,
            FormatLatency(latency),
            latency,
            string.IsNullOrWhiteSpace(e.Path) ? "" : "POST",
            string.IsNullOrWhiteSpace(e.Path) ? "—" : e.Path!,
            e.Provider,
            e.Model);
    }

    public void ApplyProviderOverride(string provider)
    {
        if (!string.IsNullOrWhiteSpace(provider)) Provider = Titlecase(provider.Trim());
    }

    public static RequestLogEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = LogLineRegex().Match(line);
        if (!m.Success) return null;

        if (!DateTime.TryParse(m.Groups[1].Value, out var ts)) return null;

        var reqId = m.Groups[2].Value.Trim();
        if (reqId == "--------") return null;

        if (!int.TryParse(m.Groups[4].Value, out var status)) return null;

        var latRaw  = m.Groups[5].Value.Trim();
        var latency = ParseLatency(latRaw);
        var method  = m.Groups[6].Value.Trim();
        var path    = m.Groups[7].Value.Trim();

        if (!IsAiPath(path)) return null;

        return new RequestLogEntry(ts, reqId, status, latRaw, latency, method, path);
    }

    private static bool IsAiPath(string path) =>
        path.StartsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/messages",         StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/completions",      StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1beta/models/",      StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/v1/responses",        StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/provider/",       StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/backend-api/",        StringComparison.OrdinalIgnoreCase);

    private static string InferProvider(string path)
    {
        if (path.StartsWith("/v1/messages",         StringComparison.OrdinalIgnoreCase)) return "Claude";
        if (path.StartsWith("/v1beta/models/",      StringComparison.OrdinalIgnoreCase)) return "Gemini";
        if (path.StartsWith("/v1/responses",        StringComparison.OrdinalIgnoreCase)) return "OpenAI Responses";
        if (path.StartsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) return "OpenAI Completions";
        if (path.StartsWith("/v1/completions",      StringComparison.OrdinalIgnoreCase)) return "OpenAI Completions";
        if (path.StartsWith("/api/provider/",       StringComparison.OrdinalIgnoreCase))
        {
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return seg.Length >= 2 ? Titlecase(seg[1]) : "Custom";
        }
        // pi-cliproxyapi-provider extension: registers inference at
        // "{root}/backend-api/" and sends Codex-style traffic to
        // "/backend-api/codex/responses".
        if (path.StartsWith("/backend-api/",         StringComparison.OrdinalIgnoreCase))
        {
            var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return seg.Length >= 2 && seg[1].Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? "Codex"
                : "OpenAI Completions";
        }
        return "OpenAI Completions";
    }

    private static TimeSpan ParseLatency(string raw)
    {
        if (raw.EndsWith("ms", StringComparison.Ordinal) &&
            double.TryParse(raw[..^2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var ms))
            return TimeSpan.FromMilliseconds(ms);

        if (raw.EndsWith("µs", StringComparison.Ordinal) &&
            double.TryParse(raw[..^2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var us))
            return TimeSpan.FromMicroseconds(us);

        if (raw.EndsWith('s') && !raw.Contains('m'))
        {
            if (double.TryParse(raw[..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var sec))
                return TimeSpan.FromSeconds(sec);
        }

        var mIdx = raw.IndexOf('m', StringComparison.Ordinal);
        if (mIdx > 0 && raw.EndsWith('s'))
        {
            if (double.TryParse(raw[..mIdx], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var mins) &&
                double.TryParse(raw[(mIdx + 1)..^1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var secs))
                return TimeSpan.FromSeconds(mins * 60 + secs);
        }

        return TimeSpan.Zero;
    }

    private static string FormatLatency(TimeSpan latency)
    {
        if (latency <= TimeSpan.Zero) return "–";
        return latency.TotalMilliseconds >= 1000
            ? latency.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : latency.TotalMilliseconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "ms";
    }

    private static string CleanProvider(string provider)
    {
        const string prefix = "OpenAI-compatible-";
        var trimmed = provider.Trim();
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static string Titlecase(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
