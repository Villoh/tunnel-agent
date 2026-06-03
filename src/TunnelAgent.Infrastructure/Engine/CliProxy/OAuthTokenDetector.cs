using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>
/// One authenticated OAuth session found in the auth-dir.
/// Filename format: {type}-{email}[-{plan}].json
/// e.g. codex-me@gmail.com-plus.json → type=codex, email=me@gmail.com, plan=PLUS
/// </summary>
public sealed class OAuthAccount
{
    public string ProviderId { get; init; } = "";
    public string Email      { get; init; } = "";
    /// <summary>Uppercase plan badge, e.g. "PLUS", "PRO", "FREE". Empty = no badge.</summary>
    public string Plan       { get; init; } = "";
    public bool   IsDisabled { get; init; }
}

/// <summary>
/// Detects which OAuth providers have active token files in the auth-dir
/// and parses the account details (email, plan) from each file.
/// </summary>
public sealed class OAuthTokenDetector
{
    // Maps provider-id → filename prefix used by CLIProxyAPI
    public static readonly IReadOnlyDictionary<string, string> KnownProviders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"]          = "claude",
            ["codex"]           = "codex",
            ["gemini-cli"]      = "gemini",
            ["kimi"]            = "kimi",
            ["antigravity"]     = "antigravity",
        };

    private readonly string _directory;

    public OAuthTokenDetector(string directory) => _directory = directory;

    /// <summary>Returns all active OAuth accounts grouped by provider ID.</summary>
    public Dictionary<string, List<OAuthAccount>> GetAccounts()
    {
        var result = new Dictionary<string, List<OAuthAccount>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_directory)) return result;

        foreach (var (providerId, prefix) in KnownProviders)
        {
            var files = Directory.GetFiles(_directory, $"{prefix}-*.json")
                .Where(f => !Path.GetFileName(f).StartsWith("openai-compat-", StringComparison.OrdinalIgnoreCase));

            foreach (var file in files)
            {
                var account = ParseAccount(file, providerId, prefix);
                if (account is null) continue;

                if (!result.TryGetValue(providerId, out var list))
                    result[providerId] = list = new List<OAuthAccount>();
                list.Add(account);
            }
        }

        return result;
    }

    /// <summary>Patches the disabled field on the token file matching the given email.</summary>
    public void SetDisabled(string providerId, string email, bool disabled)
    {
        if (!KnownProviders.TryGetValue(providerId, out var prefix)) return;
        if (!Directory.Exists(_directory)) return;

        foreach (var file in Directory.GetFiles(_directory, $"{prefix}-{email}*.json"))
        {
            if (Path.GetFileName(file).StartsWith("openai-compat-", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                var text = File.ReadAllText(file);
                var doc  = System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject();
                if (doc is null) continue;
                doc["disabled"] = disabled;
                File.WriteAllText(file, doc.ToJsonString(
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }
    }

    /// <summary>Returns IDs of providers that have at least one active account.</summary>
    public HashSet<string> GetConnectedProviderIds()
    {
        var accounts = GetAccounts();
        return new HashSet<string>(
            accounts.Where(kv => kv.Value.Any(a => !a.IsDisabled)).Select(kv => kv.Key),
            StringComparer.OrdinalIgnoreCase);
    }

    // ── private ──────────────────────────────────────────────────────────────

    private static OAuthAccount? ParseAccount(string filePath, string providerId, string prefix)
    {
        try
        {
            var text = File.ReadAllText(filePath);
            var doc  = JsonNode.Parse(text)?.AsObject();
            if (doc is null) return null;

            var disabled = doc["disabled"]?.GetValue<bool>() ?? false;

            // Require at least one auth-related field
            var hasAuth = doc["access_token"] != null
                       || doc["token"]        != null
                       || doc["api_key"]      != null
                       || doc["oauth_token"]  != null
                       || doc["credentials"]  != null
                       || doc.Count > 2;
            if (!hasAuth) return null;

            var email = doc["email"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(email))
                email = EmailFromFilename(filePath, prefix);

            // Plan: prefer JSON field, fall back to filename suffix
            var plan = doc["plan"]?.GetValue<string>()?.ToUpperInvariant() ?? "";
            if (string.IsNullOrEmpty(plan))
                plan = PlanFromFilename(filePath, prefix, email);

            return new OAuthAccount
            {
                ProviderId = providerId,
                Email      = email,
                Plan       = plan,
                IsDisabled = disabled,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Extracts email from filename: {prefix}-{email}[-{plan}].json
    /// e.g. "codex-me@gmail.com-plus.json" → "me@gmail.com"
    /// </summary>
    private static string EmailFromFilename(string filePath, string prefix)
    {
        var name = Path.GetFileNameWithoutExtension(filePath); // e.g. "codex-me@gmail.com-plus"
        var afterPrefix = name.Length > prefix.Length + 1
            ? name[(prefix.Length + 1)..]   // "me@gmail.com-plus"
            : "";

        if (string.IsNullOrEmpty(afterPrefix)) return "";

        // If it contains '@' it's an email; strip any trailing "-plan" suffix
        if (afterPrefix.Contains('@'))
        {
            // Find last '-' that comes after '@' — that would be the plan suffix
            var atIdx   = afterPrefix.IndexOf('@');
            var dashIdx = afterPrefix.LastIndexOf('-');
            if (dashIdx > atIdx)
            {
                var candidate = afterPrefix[(dashIdx + 1)..];
                // Known plan tokens
                if (IsKnownPlan(candidate))
                    return afterPrefix[..dashIdx];
            }
            return afterPrefix;
        }

        return afterPrefix;
    }

    /// <summary>
    /// Extracts plan badge from filename suffix after the email.
    /// e.g. "codex-me@gmail.com-plus.json" → "PLUS"
    /// </summary>
    private static string PlanFromFilename(string filePath, string prefix, string email)
    {
        if (string.IsNullOrEmpty(email)) return "";

        var name = Path.GetFileNameWithoutExtension(filePath);
        // Everything after "{prefix}-{email}-"
        var key  = $"{prefix}-{email}-";
        if (!name.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return "";

        var suffix = name[key.Length..];
        return IsKnownPlan(suffix) ? suffix.ToUpperInvariant() : "";
    }

    private static bool IsKnownPlan(string s) =>
        s.Equals("plus",  StringComparison.OrdinalIgnoreCase) ||
        s.Equals("pro",   StringComparison.OrdinalIgnoreCase) ||
        s.Equals("free",  StringComparison.OrdinalIgnoreCase) ||
        s.Equals("team",  StringComparison.OrdinalIgnoreCase) ||
        s.Equals("enterprise", StringComparison.OrdinalIgnoreCase);
}
