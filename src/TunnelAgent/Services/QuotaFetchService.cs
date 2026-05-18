using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using TunnelAgent.ViewModels;

namespace TunnelAgent.Services;

/// <summary>
/// Fetches live quota data directly from provider APIs using the OAuth
/// access_token stored in each auth-dir JSON file.
/// </summary>
public sealed class QuotaFetchService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly string _authDir;

    public QuotaFetchService(string authDir) => _authDir = authDir;

    public async Task FetchAndApplyAsync(ProviderViewModel provider, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var account in provider.Accounts)
            tasks.Add(FetchAccountAsync(provider.Id, account, ct));
        await Task.WhenAll(tasks);
    }

    public Task FetchAccountPublicAsync(string providerId, ProviderAccountViewModel account,
        CancellationToken ct = default) =>
        FetchAccountAsync(providerId, account, ct);

    private Task FetchAccountAsync(string providerId, ProviderAccountViewModel account, CancellationToken ct) =>
        providerId switch
        {
            "claude"         => FetchClaudeAsync(account, ct),
            "codex"          => FetchCodexAsync(account, ct),
            "github-copilot" => FetchCopilotAsync(account, ct),
            _                => Task.CompletedTask,
        };

    // ── Claude ───────────────────────────────────────────────────────────────
    // GET https://api.anthropic.com/api/oauth/usage
    // { "five_hour":  { "utilization": 70.0, "resets_at": "2026-05-13T23:00:00Z" },
    //   "seven_day":  { "utilization": 41.0, "resets_at": "2026-05-19T06:00:00Z" } }

    private async Task FetchClaudeAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        var token = ReadAccessToken("claude", account.Email);
        if (token is null) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.Add("User-Agent", "TunnelAgent/1.0");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var bars = new List<(string title, double utilization, string? resetsAt)>();

            var fiveH  = body["five_hour"];
            var sevenD = body["seven_day"];

            if (fiveH?["utilization"] is not null)
                bars.Add(("Primary Limit (5h)",
                    fiveH["utilization"]!.GetValue<double>(),
                    fiveH["resets_at"]?.GetValue<string>()));

            if (sevenD?["utilization"] is not null)
                bars.Add(("Weekly Limit",
                    sevenD["utilization"]!.GetValue<double>(),
                    sevenD["resets_at"]?.GetValue<string>()));

            ApplyBarsFromUtilization(account, bars);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ── Codex (ChatGPT) ──────────────────────────────────────────────────────
    // GET https://chatgpt.com/backend-api/wham/usage
    // { "plan_type": "plus",
    //   "rate_limit": {
    //     "primary_window":   { "used_percent": 1,  "reset_at": 1778725667 },
    //     "secondary_window": { "used_percent": 22, "reset_at": 1779181031 } } }

    private async Task FetchCodexAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        var token = ReadAccessToken("codex", account.Email);
        if (token is null) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("User-Agent", "TunnelAgent/1.0");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var plan = body["plan_type"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(plan))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    account.PlanBadge = plan.ToUpperInvariant());
            }

            var rl        = body["rate_limit"];
            var primary   = rl?["primary_window"];
            var secondary = rl?["secondary_window"];

            // primary_window = 5h (18000s), secondary_window = weekly (604800s)
            var bars = new List<(string title, double usedPct, long? resetAt)>();

            if (primary is not null)
                bars.Add(("Primary Limit (5h)",
                    primary["used_percent"]?.GetValue<double>() ?? 0,
                    primary["reset_at"]?.GetValue<long>()));

            if (secondary is not null)
                bars.Add(("Weekly Limit",
                    secondary["used_percent"]?.GetValue<double>() ?? 0,
                    secondary["reset_at"]?.GetValue<long>()));

            ApplyBarsFromPercent(account, bars);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ── GitHub Copilot ───────────────────────────────────────────────────────
    // GET https://api.github.com/copilot_internal/user
    // { "plan": "pro", "quota": {
    //     "premium_interactions": { "remaining": 75, "total": 300 },
    //     "chat":                  { "remaining": 90, "total": 100 } } }

    private async Task FetchCopilotAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        var token = ReadAccessToken("github-copilot", account.Email);
        if (token is null) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/copilot_internal/user");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/json");
            req.Headers.Add("User-Agent", "TunnelAgent/1.0");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var plan = body["plan"]?.GetValue<string>() ?? "";
            if (!string.IsNullOrEmpty(plan))
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    account.PlanBadge = plan.ToUpperInvariant());
            }

            var quota   = body["quota"];
            var premium = quota?["premium_interactions"];
            var chat    = quota?["chat"];

            var bars = new List<(string title, double used, double total)>();

            if (premium is not null)
            {
                var remaining = premium["remaining"]?.GetValue<double>() ?? 0;
                var total     = premium["total"]?.GetValue<double>() ?? 1;
                bars.Add(("Premium Interactions", total - remaining, total));
            }

            if (chat is not null)
            {
                var remaining = chat["remaining"]?.GetValue<double>() ?? 0;
                var total     = chat["total"]?.GetValue<double>() ?? 1;
                bars.Add(("Chat Quota", total - remaining, total));
            }

            ApplyBarsFromUsedTotal(account, bars);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ── Apply helpers ─────────────────────────────────────────────────────────

    /// <summary>Claude: utilization is 0-100, resets_at is ISO 8601 string.</summary>
    private static void ApplyBarsFromUtilization(
        ProviderAccountViewModel account,
        List<(string title, double utilization, string? resetsAt)> bars)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            account.QuotaBars.Clear();
            foreach (var (title, utilization, resetsAt) in bars)
            {
                account.QuotaBars.Add(new QuotaBarViewModel
                {
                    Title   = title,
                    Used    = Math.Clamp(utilization / 100.0, 0, 1),
                    ResetIn = FormatResetAtIso(resetsAt),
                });
            }
        });
    }

    /// <summary>Codex: used_percent is 0-100, reset_at is unix timestamp.</summary>
    private static void ApplyBarsFromPercent(
        ProviderAccountViewModel account,
        List<(string title, double usedPct, long? resetAt)> bars)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            account.QuotaBars.Clear();
            foreach (var (title, usedPct, resetAt) in bars)
            {
                account.QuotaBars.Add(new QuotaBarViewModel
                {
                    Title   = title,
                    Used    = Math.Clamp(usedPct / 100.0, 0, 1),
                    ResetIn = FormatResetAtUnix(resetAt),
                });
            }
        });
    }

    /// <summary>Copilot: used = total - remaining, total = max.</summary>
    private static void ApplyBarsFromUsedTotal(
        ProviderAccountViewModel account,
        List<(string title, double used, double total)> bars)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            account.QuotaBars.Clear();
            foreach (var (title, used, total) in bars)
            {
                var fraction = total > 0 ? used / total : 0;
                account.QuotaBars.Add(new QuotaBarViewModel
                {
                    Title   = title,
                    Used    = Math.Clamp(fraction, 0, 1),
                    ResetIn = "",
                });
            }
        });
    }

    // ── Token reader ─────────────────────────────────────────────────────────

    private string? ReadAccessToken(string prefix, string email)
    {
        if (!Directory.Exists(_authDir)) return null;
        foreach (var file in Directory.GetFiles(_authDir, $"{prefix}-{email}*.json"))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (doc is null) continue;
                if (doc["disabled"]?.GetValue<bool>() == true) continue;
                return doc["access_token"]?.GetValue<string>()
                    ?? doc["accessToken"]?.GetValue<string>();
            }
            catch { }
        }
        return null;
    }

    // ── Format helpers ────────────────────────────────────────────────────────

    private static string FormatResetAtUnix(long? unixSeconds)
    {
        if (unixSeconds is null) return "";
        var diff = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value) - DateTimeOffset.UtcNow;
        return FormatDiff(diff);
    }

    private static string FormatResetAtIso(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        if (!DateTimeOffset.TryParse(iso, out var dt)) return "";
        return FormatDiff(dt - DateTimeOffset.UtcNow);
    }

    private static string FormatDiff(TimeSpan diff)
    {
        if (diff <= TimeSpan.Zero) return "Resets in Now";
        if (diff.TotalDays >= 1)  return $"Resets in {(int)diff.TotalDays}d {diff.Hours}h";
        if (diff.TotalHours >= 1) return $"Resets in {(int)diff.TotalHours}h {diff.Minutes}m";
        return $"Resets in {diff.Minutes}m";
    }
}
