using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
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
            "gemini-cli"     => FetchGeminiAsync(account, ct),
            "antigravity"    => FetchAntigravityAsync(account, ct),
            "kiro"           => FetchKiroAsync(account, ct),
            "trae"           => FetchTraeAsync(account, ct),
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
    // Pro/Business: quota_snapshots.{ chat, completions, premium_interactions }
    //               each: { percent_remaining, remaining, entitlement, unlimited }
    // Free/Individual: limited_user_quotas + monthly_quotas: { chat, completions }
    // Reset: quota_reset_date_utc
    // Plan:  copilot_plan + access_type_sku

    private async Task FetchCopilotAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        var token = ReadAccessToken("github-copilot", account.Email);
        if (token is null) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/copilot_internal/user");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("Accept", "application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            req.Headers.Add("User-Agent", "TunnelAgent/1.0");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            // Plan badge
            var sku      = body["access_type_sku"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
            var planRaw  = body["copilot_plan"]?.GetValue<string>()?.ToLowerInvariant() ?? "";
            var planBadge = CopilotPlanBadge(sku, planRaw);
            if (!string.IsNullOrEmpty(planBadge))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => account.PlanBadge = planBadge);

            var resetIso = body["quota_reset_date_utc"]?.GetValue<string>()
                        ?? body["quota_reset_date"]?.GetValue<string>();

            var bars = new List<(string title, double used, string? resetTime)>();

            // Method 1: quota_snapshots (Pro / Business / Enterprise)
            var snapshots = body["quota_snapshots"];
            if (snapshots is not null)
            {
                TryCopilotSnapshot(bars, snapshots["chat"],                 "Chat",        resetIso);
                TryCopilotSnapshot(bars, snapshots["completions"],          "Completions", resetIso);
                TryCopilotSnapshot(bars, snapshots["premium_interactions"], "Premium",     resetIso);
            }

            // Method 2: limited_user_quotas + monthly_quotas (Free / Individual)
            if (bars.Count == 0)
            {
                var limited = body["limited_user_quotas"];
                var monthly = body["monthly_quotas"];
                if (limited is not null && monthly is not null)
                {
                    TryCopilotLimited(bars, limited["chat"],        monthly["chat"],        "Chat",        resetIso);
                    TryCopilotLimited(bars, limited["completions"], monthly["completions"], "Completions", resetIso);
                }
            }

            if (bars.Count > 0)
                ApplyBarsFromFraction(account, bars);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static void TryCopilotSnapshot(
        List<(string, double, string?)> bars, JsonNode? snap, string title, string? resetIso)
    {
        if (snap is null) return;
        if (snap["unlimited"]?.GetValue<bool>() == true) return;

        double used;
        if (snap["percent_remaining"] is JsonNode pct)
        {
            used = (100.0 - Math.Clamp(pct.GetValue<double>(), 0, 100)) / 100.0;
        }
        else
        {
            var remaining   = snap["remaining"]?.GetValue<double>()   ?? 0;
            var entitlement = snap["entitlement"]?.GetValue<double>() ?? 0;
            if (entitlement <= 0) return;
            used = Math.Clamp((entitlement - remaining) / entitlement, 0, 1);
        }

        bars.Add((title, used, resetIso));
    }

    private static void TryCopilotLimited(
        List<(string, double, string?)> bars,
        JsonNode? remaining, JsonNode? total, string title, string? resetIso)
    {
        if (remaining is null || total is null) return;
        var rem = remaining.GetValue<double>();
        var tot = total.GetValue<double>();
        if (tot <= 0) return;
        bars.Add((title, Math.Clamp((tot - rem) / tot, 0, 1), resetIso));
    }

    private static string CopilotPlanBadge(string sku, string plan)
    {
        if (sku.Contains("enterprise") || plan == "enterprise") return "ENTERPRISE";
        if (sku.Contains("business")   || plan == "business")   return "BUSINESS";
        if (sku.Contains("educational"))                         return "PRO";
        if (sku.Contains("pro")        || plan.Contains("pro")) return "PRO";
        if (plan == "individual" && !sku.Contains("free_limited")) return "PRO";
        if (sku.Contains("free_limited") || sku == "free")       return "FREE";
        if (plan.Contains("free"))                               return "FREE";
        if (!string.IsNullOrEmpty(plan)) return plan.ToUpperInvariant();
        return sku.ToUpperInvariant();
    }

    // ── Gemini CLI ────────────────────────────────────────────────────────────
    // POST https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota
    // { "buckets": [ { "modelId": "gemini-2.5-flash", "remainingFraction": 0.93, "resetTime": "..." } ] }

    private async Task FetchGeminiAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        var (token, projectId) = await ReadGeminiTokenAsync(account.Email, ct);
        if (token is null) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuota");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Content = new StringContent($"{{\"project\":\"{projectId}\"}}",
                System.Text.Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var buckets = body["buckets"]?.AsArray();
            if (buckets is null) return;

            double flashFraction = double.MaxValue;
            string? flashReset = null;
            double proFraction = double.MaxValue;
            string? proReset = null;

            foreach (var bucket in buckets)
            {
                if (bucket is null) continue;
                var modelId   = bucket["modelId"]?.GetValue<string>() ?? "";
                var remaining = bucket["remainingFraction"]?.GetValue<double>() ?? 1.0;
                var resetTime = bucket["resetTime"]?.GetValue<string>();

                if (modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                {
                    if (modelId.StartsWith("gemini-2.0-flash", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (remaining < flashFraction) flashFraction = remaining;
                    flashReset = EarliestIso(flashReset, resetTime);
                }
                else if (modelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
                {
                    if (remaining < proFraction) proFraction = remaining;
                    proReset = EarliestIso(proReset, resetTime);
                }
            }

            var bars = new List<(string title, double used, string? resetTime)>();
            if (flashFraction != double.MaxValue)
                bars.Add(("Gemini Flash", 1.0 - flashFraction, flashReset));
            if (proFraction != double.MaxValue)
                bars.Add(("Gemini Pro", 1.0 - proFraction, proReset));

            if (bars.Count == 0) return;
            ApplyBarsFromFraction(account, bars);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private async Task<(string? token, string projectId)> ReadGeminiTokenAsync(
        string email, CancellationToken ct)
    {
        if (!Directory.Exists(_authDir)) return (null, "");
        foreach (var file in Directory.GetFiles(_authDir, "gemini-*.json"))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (doc is null) continue;
                if (doc["disabled"]?.GetValue<bool>() == true) continue;

                var fileEmail = doc["email"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(email) &&
                    !string.Equals(fileEmail, email, StringComparison.OrdinalIgnoreCase))
                    continue;

                var tokenObj    = doc["token"]?.AsObject();
                if (tokenObj is null) continue;
                var accessToken  = tokenObj["access_token"]?.GetValue<string>();
                var refreshToken = tokenObj["refresh_token"]?.GetValue<string>();
                var expiry       = tokenObj["expiry"]?.GetValue<string>();
                var tokenUri     = tokenObj["token_uri"]?.GetValue<string>() ?? "https://oauth2.googleapis.com/token";
                var clientId     = tokenObj["client_id"]?.GetValue<string>() ?? "";
                var clientSecret = tokenObj["client_secret"]?.GetValue<string>() ?? "";
                var projectId    = doc["project_id"]?.GetValue<string>() ?? "";

                if (accessToken is null) continue;

                var isExpired = DateTimeOffset.TryParse(expiry, out var expiryDt)
                    && expiryDt - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(60);

                if (isExpired && refreshToken is not null)
                {
                    var refreshed = await RefreshGoogleTokenAsync(tokenUri, clientId, clientSecret, refreshToken, ct);
                    if (refreshed is null) return (null, "");
                    accessToken = refreshed;
                }

                return (accessToken, projectId);
            }
            catch { }
        }
        return (null, "");
    }

    // ── Antigravity ──────────────────────────────────────────────────────────
    // POST https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels
    // { "models": { "claude-3-5-sonnet": { "quotaInfo": { "remainingFraction": 0.85, "resetTime": "..." } } } }

    private async Task FetchAntigravityAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        var token = await ReadAntigravityTokenAsync(account.Email, ct);
        if (token is null) return;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
            req.Headers.Add("Authorization", $"Bearer {token}");
            req.Headers.Add("User-Agent", "antigravity/1.11.3");
            req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var models = body["models"]?.AsObject();
            if (models is null) return;

            double claudeFraction = double.MaxValue;
            string? claudeReset = null;
            double geminiProFraction = double.MaxValue;
            string? geminiProReset = null;
            double geminiFlashFraction = double.MaxValue;
            string? geminiFlashReset = null;

            foreach (var kvp in models)
            {
                var modelName = kvp.Key;
                var quotaInfo = kvp.Value?["quotaInfo"];
                if (quotaInfo is null) continue;

                var remaining = quotaInfo["remainingFraction"]?.GetValue<double>() ?? 1.0;
                var resetTime = quotaInfo["resetTime"]?.GetValue<string>();

                var nameL = modelName.ToLowerInvariant();
                if (nameL.Contains("claude") || nameL.Contains("gpt") || nameL.Contains("oss"))
                {
                    if (remaining < claudeFraction) claudeFraction = remaining;
                    claudeReset = EarliestIso(claudeReset, resetTime);
                }
                else if (nameL.Contains("gemini") && nameL.Contains("pro"))
                {
                    if (remaining < geminiProFraction) geminiProFraction = remaining;
                    geminiProReset = EarliestIso(geminiProReset, resetTime);
                }
                else if (nameL.Contains("gemini") && nameL.Contains("flash"))
                {
                    if (remaining < geminiFlashFraction) geminiFlashFraction = remaining;
                    geminiFlashReset = EarliestIso(geminiFlashReset, resetTime);
                }
            }

            var bars = new List<(string title, double used, string? resetTime)>();
            if (claudeFraction != double.MaxValue)
                bars.Add(("Claude", 1.0 - claudeFraction, claudeReset));
            if (geminiProFraction != double.MaxValue)
                bars.Add(("Gemini Pro", 1.0 - geminiProFraction, geminiProReset));
            if (geminiFlashFraction != double.MaxValue)
                bars.Add(("Gemini Flash", 1.0 - geminiFlashFraction, geminiFlashReset));

            if (bars.Count == 0) return;
            ApplyBarsFromFraction(account, bars);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private async Task<string?> ReadAntigravityTokenAsync(string email, CancellationToken ct)
    {
        // Antigravity OAuth credentials (from Quotio open-source implementation)
        // These are public OAuth app credentials, not user secrets
        const string clientId     = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
        const string clientSecret = "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";
        const string tokenUri     = "https://oauth2.googleapis.com/token";

        if (!Directory.Exists(_authDir)) return null;
        foreach (var file in Directory.GetFiles(_authDir, "antigravity-*.json"))
        {
            try
            {
                var doc = JsonNode.Parse(File.ReadAllText(file))?.AsObject();
                if (doc is null) continue;
                if (doc["disabled"]?.GetValue<bool>() == true) continue;

                var fileEmail = doc["email"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrEmpty(email) &&
                    !string.Equals(fileEmail, email, StringComparison.OrdinalIgnoreCase))
                    continue;

                var accessToken  = doc["access_token"]?.GetValue<string>();
                var refreshToken = doc["refresh_token"]?.GetValue<string>();
                var expiry       = doc["expired"]?.GetValue<string>();

                if (accessToken is null) continue;

                var isExpired = DateTimeOffset.TryParse(expiry, out var expiryDt)
                    && expiryDt - DateTimeOffset.UtcNow <= TimeSpan.FromSeconds(60);

                if (isExpired && refreshToken is not null)
                {
                    var refreshed = await RefreshGoogleTokenAsync(tokenUri, clientId, clientSecret, refreshToken, ct);
                    if (refreshed is null) return null;
                    return refreshed;
                }

                return accessToken;
            }
            catch { }
        }
        return null;
    }

    // ── Kiro (Amazon) ─────────────────────────────────────────────────────────

    private static async Task FetchKiroAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        try
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var path = Path.Combine(userProfile, ".aws", "sso", "cache", "kiro-auth-token.json");
            if (!File.Exists(path)) return;

            var doc = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (doc is null) return;

            var refreshToken = doc["refresh_token"]?.GetValue<string>();
            var expiresAt    = doc["expiresAt"]?.GetValue<string>();
            var authMethod   = doc["authMethod"]?.GetValue<string>() ?? "";
            var profileArn   = doc["profileArn"]?.GetValue<string>();
            var region       = doc["region"]?.GetValue<string>() ?? "us-east-1";
            var clientIdHash = doc["clientIdHash"]?.GetValue<string>();
            var clientId     = doc["client_id"]?.GetValue<string>();
            var clientSecret = doc["client_secret"]?.GetValue<string>();
            var accessToken  = doc["access_token"]?.GetValue<string>();

            if ((clientId is null || clientSecret is null) && clientIdHash is not null)
            {
                var hashPath = Path.Combine(userProfile, ".aws", "sso", "cache", $"{clientIdHash}.json");
                if (File.Exists(hashPath))
                {
                    try
                    {
                        var hd = JsonNode.Parse(File.ReadAllText(hashPath))?.AsObject();
                        clientId     ??= hd?["client_id"]?.GetValue<string>();
                        clientSecret ??= hd?["client_secret"]?.GetValue<string>();
                    }
                    catch { }
                }
            }

            if (refreshToken is null) return;

            // Machine ID: SHA256(clientId ?? refreshToken) → lowercase hex
            var seed      = clientId ?? refreshToken;
            var machineId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed))).ToLowerInvariant();

            // Refresh token if expired (within 5 minutes)
            var isExpired = DateTimeOffset.TryParse(expiresAt, out var expiryDt)
                && expiryDt - DateTimeOffset.UtcNow <= TimeSpan.FromMinutes(5);

            if (isExpired || accessToken is null)
            {
                accessToken = await RefreshKiroTokenAsync(authMethod, region, clientId, clientSecret, refreshToken, ct);
                if (accessToken is null) return;
            }

            // Build URL
            var url = $"https://q.{region}.amazonaws.com/getUsageLimits?origin=AI_EDITOR&resourceType=AGENTIC_REQUEST";
            if (!string.IsNullOrEmpty(profileArn))
                url += $"&profileArn={Uri.EscapeDataString(profileArn)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("Authorization", $"Bearer {accessToken}");
            req.Headers.Add("Host", $"q.{region}.amazonaws.com");
            req.Headers.Add("User-Agent", $"aws-sdk-js/1.0.0 ua/2.1 os/windows#10.0 lang/js md/nodejs#22.21.1 api/codewhispererruntime#1.0.0 m/N,E KiroIDE-0.10.32-{machineId}");
            req.Headers.Add("x-amz-user-agent", $"aws-sdk-js/1.0.0 KiroIDE-0.10.32-{machineId}");
            req.Headers.Add("amz-sdk-invocation-id", Guid.NewGuid().ToString().ToLower());
            req.Headers.Add("amz-sdk-request", "attempt=1; max=1");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var breakdowns = body["usageBreakdownList"]?.AsArray();
            if (breakdowns is null) return;

            var bars = new List<(string title, double used, double total, long? resetAt)>();

            foreach (var item in breakdowns)
            {
                if (item is null) continue;
                var displayName  = item["displayName"]?.GetValue<string>() ?? "Usage";
                var currentUsage = item["currentUsage"]?.GetValue<double>() ?? 0;
                var usageLimit   = item["usageLimit"]?.GetValue<double>() ?? 0;
                var nextReset    = item["nextDateReset"] is not null ? (long?)((long)item["nextDateReset"]!.GetValue<double>()) : null;

                var trialInfo = item["freeTrialInfo"]?.AsObject();
                var trialStatus = trialInfo?["freeTrialStatus"]?.GetValue<string>() ?? "";

                if (string.Equals(trialStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase) && trialInfo is not null)
                {
                    var trialCurrent = trialInfo["currentUsage"]?.GetValue<double>() ?? 0;
                    var trialLimit   = trialInfo["usageLimit"]?.GetValue<double>() ?? 1;
                    var trialExpiry  = trialInfo["freeTrialExpiry"] is not null
                        ? (long?)((long)trialInfo["freeTrialExpiry"]!.GetValue<double>())
                        : null;
                    bars.Add(($"Bonus {displayName}", trialCurrent, trialLimit, trialExpiry));
                }

                if (usageLimit > 0)
                {
                    var title = string.Equals(trialStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase)
                        ? $"{displayName} (Base)"
                        : displayName;
                    bars.Add((title, currentUsage, usageLimit, nextReset));
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                account.QuotaBars.Clear();
                foreach (var (title, used, total, resetAt) in bars)
                {
                    account.QuotaBars.Add(new QuotaBarViewModel
                    {
                        Title   = title,
                        Used    = total > 0 ? Math.Clamp(used / total, 0, 1) : 0,
                        ResetIn = FormatResetAtUnix(resetAt),
                    });
                }
            });
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private static async Task<string?> RefreshKiroTokenAsync(
        string authMethod, string region,
        string? clientId, string? clientSecret,
        string refreshToken, CancellationToken ct)
    {
        try
        {
            if (string.Equals(authMethod, "social", StringComparison.OrdinalIgnoreCase))
            {
                var url  = $"https://prod.{region}.auth.desktop.kiro.dev/refreshToken";
                var body = $"{{\"refreshToken\":\"{refreshToken}\"}}"; 
                using var req  = new HttpRequestMessage(HttpMethod.Post, url);
                req.Content    = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return json?["accessToken"]?.GetValue<string>();
            }
            else
            {
                if (clientId is null || clientSecret is null) return null;
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("clientId",     clientId),
                    new KeyValuePair<string, string>("clientSecret", clientSecret),
                    new KeyValuePair<string, string>("grantType",    "refresh_token"),
                    new KeyValuePair<string, string>("refreshToken", refreshToken),
                });
                using var resp = await Http.PostAsync($"https://oidc.{region}.amazonaws.com/token", form, ct);
                if (!resp.IsSuccessStatusCode) return null;
                var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
                return json?["accessToken"]?.GetValue<string>();
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    // ── Trae (ByteDance) ──────────────────────────────────────────────────────

    private static async Task FetchTraeAsync(ProviderAccountViewModel account, CancellationToken ct)
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var path = Path.Combine(appData, "Trae", "User", "globalStorage", "storage.json");
            if (!File.Exists(path)) return;

            var doc = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if (doc is null) return;

            var authInfoRaw = doc["iCubeAuthInfo://icube.cloudide"]?.GetValue<string>();
            if (string.IsNullOrEmpty(authInfoRaw)) return;

            var authDoc = JsonNode.Parse(authInfoRaw)?.AsObject();
            if (authDoc is null) return;

            var token   = authDoc["token"]?.GetValue<string>();
            var host    = authDoc["host"]?.GetValue<string>() ?? "https://api-sg-central.trae.ai";
            var acctDoc = authDoc["account"]?.AsObject();
            var email   = acctDoc?["email"]?.GetValue<string>() ?? "";

            // Match account by email if set
            if (!string.IsNullOrEmpty(account.Email) &&
                !string.Equals(account.Email, email, StringComparison.OrdinalIgnoreCase))
                return;

            if (token is null) return;

            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"{host.TrimEnd('/')}/trae/api/v1/pay/user_current_entitlement_list");
            req.Headers.Add("Authorization", $"Cloud-IDE-JWT {token}");
            req.Headers.Add("Accept", "application/json, text/plain, */*");
            req.Headers.Add("Origin", "https://www.trae.ai");
            req.Headers.Add("Referer", "https://www.trae.ai/");
            req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            req.Content = new StringContent("{\"require_usage\":true}", Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return;

            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (body is null) return;

            var packs = body["user_entitlement_pack_list"]?.AsArray();
            if (packs is null) return;

            JsonNode? pack = null;
            foreach (var p in packs)
            {
                if (p?["status"]?.GetValue<int>() == 1) { pack = p; break; }
            }
            if (pack is null) return;

            var baseInfo = pack["entitlement_base_info"];
            var usage    = pack["usage"];
            var quota    = baseInfo?["quota"];
            var endTime  = baseInfo?["end_time"]?.GetValue<long>();
            var prodType = baseInfo?["product_type"]?.GetValue<int>() ?? 0;

            var plan = prodType switch
            {
                1 => "PRO",
                2 => "TEAM",
                3 => "BUILDER",
                _ => "FREE",
            };

            Avalonia.Threading.Dispatcher.UIThread.Post(() => account.PlanBadge = plan);

            var fastLimit  = quota?["premium_model_fast_request_limit"]?.GetValue<double>() ?? 0;
            var slowLimit  = quota?["premium_model_slow_request_limit"]?.GetValue<double>() ?? 0;
            var advLimit   = quota?["advanced_model_request_limit"]?.GetValue<double>() ?? 0;
            var autoLimit  = quota?["auto_completion_limit"]?.GetValue<double>() ?? 0;

            var fastUsed   = usage?["premium_model_fast_amount"]?.GetValue<double>() ?? 0;
            var slowUsed   = usage?["premium_model_slow_amount"]?.GetValue<double>() ?? 0;
            var advUsed    = usage?["advanced_model_amount"]?.GetValue<double>() ?? 0;
            var autoUsed   = usage?["auto_completion_amount"]?.GetValue<double>() ?? 0;

            var bars = new List<(string title, double used, double total)>();
            if (fastLimit > 0) bars.Add(("Premium Fast",   fastUsed, fastLimit));
            if (slowLimit > 0) bars.Add(("Premium Slow",   slowUsed, slowLimit));
            if (advLimit  > 0) bars.Add(("Advanced Models", advUsed,  advLimit));
            if (autoLimit > 0) bars.Add(("Auto Completion", autoUsed, autoLimit));

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                account.QuotaBars.Clear();
                foreach (var (title, used, total) in bars)
                {
                    account.QuotaBars.Add(new QuotaBarViewModel
                    {
                        Title   = title,
                        Used    = total > 0 ? Math.Clamp(used / total, 0, 1) : 0,
                        ResetIn = FormatResetAtUnix(endTime),
                    });
                }
            });
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    // ── Google OAuth2 token refresh ──────────────────────────────────────────

    private static async Task<string?> RefreshGoogleTokenAsync(
        string tokenUri, string clientId, string clientSecret, string refreshToken, CancellationToken ct)
    {
        try
        {
            var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("client_id",     clientId),
                new KeyValuePair<string, string>("client_secret", clientSecret),
                new KeyValuePair<string, string>("refresh_token", refreshToken),
                new KeyValuePair<string, string>("grant_type",    "refresh_token"),
            });
            using var resp = await Http.PostAsync(tokenUri, form, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            return body?["access_token"]?.GetValue<string>();
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
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

    /// <summary>Gemini/Antigravity: used = 1.0 - remainingFraction; resetTime is ISO 8601.</summary>
    private static void ApplyBarsFromFraction(
        ProviderAccountViewModel account,
        List<(string title, double used, string? resetTime)> bars)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            account.QuotaBars.Clear();
            foreach (var (title, used, resetTime) in bars)
            {
                account.QuotaBars.Add(new QuotaBarViewModel
                {
                    Title   = title,
                    Used    = Math.Clamp(used, 0, 1),
                    ResetIn = FormatResetAtIso(resetTime),
                });
            }
        });
    }

    private static string? EarliestIso(string? a, string? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return DateTimeOffset.TryParse(a, out var dtA) && DateTimeOffset.TryParse(b, out var dtB) && dtB < dtA ? b : a;
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
