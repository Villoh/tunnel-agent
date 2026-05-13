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
/// access_token stored in each auth-dir JSON file. Mirrors the approach
/// used by ProxyPal (src-tauri/src/commands/quota.rs).
/// </summary>
public sealed class QuotaFetchService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly string _authDir;

    public QuotaFetchService(string authDir) => _authDir = authDir;

    // ── Public entry point ───────────────────────────────────────────────────

    /// <summary>
    /// Fetches quota for all accounts of the given provider and populates
    /// QuotaBars on each ProviderAccountViewModel.
    /// </summary>
    public async Task FetchAndApplyAsync(ProviderViewModel provider, CancellationToken ct = default)
    {
        var tasks = new List<Task>();
        foreach (var account in provider.Accounts)
        {
            tasks.Add(FetchAccountAsync(provider.Id, account, ct));
        }
        await Task.WhenAll(tasks);
    }

    // ── Per-provider fetch ───────────────────────────────────────────────────

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
    // Response: { "rate_limit_tier": "pro",
    //   "five_hour":  { "used": 34,  "limit": 100, "reset_at": 1737561600 },
    //   "seven_day":  { "used": 12,  "limit": 100, "reset_at": 1738166400 },
    //   "extra_usage": { "spend": 5.0, "limit": 200.0 } }

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

            var plan = body["rate_limit_tier"]?.GetValue<string>()
                    ?? body["plan"]?.GetValue<string>()
                    ?? "";

            if (!string.IsNullOrEmpty(plan))
                account.PlanBadge = plan.ToUpperInvariant();

            var fiveH   = body["five_hour"];
            var sevenD  = body["seven_day"];

            var bars = new List<(string title, double used, double limit, long? resetAt)>();

            if (fiveH is not null)
                bars.Add(("Primary Limit (5h)",
                    fiveH["used"]?.GetValue<double>() ?? 0,
                    fiveH["limit"]?.GetValue<double>() ?? 100,
                    fiveH["reset_at"]?.GetValue<long>()));

            if (sevenD is not null)
                bars.Add(("Weekly Limit",
                    sevenD["used"]?.GetValue<double>() ?? 0,
                    sevenD["limit"]?.GetValue<double>() ?? 100,
                    sevenD["reset_at"]?.GetValue<long>()));

            ApplyBars(account, bars);
        }
        catch (OperationCanceledException) { }
        catch { /* quota fetch is best-effort */ }
    }

    // ── Codex (ChatGPT) ──────────────────────────────────────────────────────
    // GET https://chatgpt.com/backend-api/wham/usage
    // Response: { "plan_type": "plus",
    //   "rate_limit": {
    //     "primary_window":   { "used_percent": 1.0,  "reset_at": 1737561600 },
    //     "secondary_window": { "used_percent": 22.0, "reset_at": 1738166400 } } }

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
                account.PlanBadge = plan.ToUpperInvariant();

            var rl       = body["rate_limit"];
            var primary  = rl?["primary_window"];
            var secondary = rl?["secondary_window"];

            var bars = new List<(string title, double usedPct, long? resetAt)>();

            if (primary is not null)
                bars.Add(("Primary Limit (3h)",
                    primary["used_percent"]?.GetValue<double>() ?? 0,
                    primary["reset_at"]?.GetValue<long>()));

            if (secondary is not null)
                bars.Add(("Weekly Limit",
                    secondary["used_percent"]?.GetValue<double>() ?? 0,
                    secondary["reset_at"]?.GetValue<long>()));

            ApplyBarsFromPercent(account, bars);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── GitHub Copilot ───────────────────────────────────────────────────────
    // GET https://api.github.com/copilot_internal/user
    // Response: { "plan": "pro",
    //   "quota": { "premium_interactions": { "remaining": 75, "total": 300 },
    //              "chat":                  { "remaining": 90, "total": 100 } } }

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
                account.PlanBadge = plan.ToUpperInvariant();

            var quota = body["quota"];
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
        catch (OperationCanceledException) { }
        catch { }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the access_token from the provider's token file matching the email.
    /// </summary>
    private string? ReadAccessToken(string prefix, string email)
    {
        if (!Directory.Exists(_authDir)) return null;

        // Find the file: {prefix}-{email}[-plan].json
        var pattern = $"{prefix}-{email}*.json";
        foreach (var file in Directory.GetFiles(_authDir, pattern))
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

    private static void ApplyBars(
        ProviderAccountViewModel account,
        List<(string title, double used, double limit, long? resetAt)> bars)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            account.QuotaBars.Clear();
            foreach (var (title, used, limit, resetAt) in bars)
            {
                var fraction = limit > 0 ? used / limit : 0;
                account.QuotaBars.Add(new QuotaBarViewModel
                {
                    Title   = title,
                    Used    = Math.Clamp(fraction, 0, 1),
                    ResetIn = FormatResetAt(resetAt),
                });
            }
        });
    }

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
                    ResetIn = FormatResetAt(resetAt),
                });
            }
        });
    }

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

    private static string FormatResetAt(long? unixSeconds)
    {
        if (unixSeconds is null) return "";
        var dt   = DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value);
        var diff = dt - DateTimeOffset.UtcNow;
        if (diff <= TimeSpan.Zero) return "Resets in Now";

        if (diff.TotalDays >= 1)
            return $"Resets in {(int)diff.TotalDays}d {diff.Hours}h";
        if (diff.TotalHours >= 1)
            return $"Resets in {(int)diff.TotalHours}h {diff.Minutes}m";
        return $"Resets in {diff.Minutes}m";
    }
}
