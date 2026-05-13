using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace TunnelAgent.Services;

/// <summary>
/// Detects which OAuth providers have active token files in the auth-dir.
/// CLIProxyAPIPlus writes one JSON file per authenticated OAuth session.
/// Known file patterns per provider:
///   claude     → claude-*.json  (type = "claude")
///   codex      → codex-*.json   (type = "codex")
///   gemini-cli → gemini-*.json  (type = "gemini-cli")
///   kimi       → kimi-*.json    (type = "kimi")
///   github-copilot → github-copilot-*.json
///   antigravity → antigravity-*.json
///   qwen       → qwen-*.json
/// The detector does not depend on the binary running — it just reads files.
/// </summary>
public sealed class OAuthTokenDetector
{
    // Maps provider-id → glob prefix used by CLIProxyAPIPlus
    public static readonly IReadOnlyDictionary<string, string> KnownProviders =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"]          = "claude",
            ["codex"]           = "codex",
            ["gemini-cli"]      = "gemini",
            ["kimi"]            = "kimi",
            ["github-copilot"]  = "github-copilot",
            ["antigravity"]     = "antigravity",
            ["qwen"]            = "qwen",
        };

    private readonly string _directory;

    public OAuthTokenDetector(string directory) => _directory = directory;

    /// <summary>
    /// Returns the set of provider IDs that have at least one active (non-disabled) token file.
    /// </summary>
    public HashSet<string> GetConnectedProviderIds()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_directory)) return result;

        foreach (var (providerId, prefix) in KnownProviders)
        {
            var files = Directory.GetFiles(_directory, $"{prefix}-*.json");
            if (files.Any(f => IsActiveTokenFile(f, providerId)))
                result.Add(providerId);
        }

        return result;
    }

    public bool IsConnected(string providerId) =>
        KnownProviders.TryGetValue(providerId, out var prefix) &&
        Directory.Exists(_directory) &&
        Directory.GetFiles(_directory, $"{prefix}-*.json")
            .Any(f => IsActiveTokenFile(f, providerId));

    // ── private ──────────────────────────────────────────────────────────────

    private static bool IsActiveTokenFile(string filePath, string providerId)
    {
        // Skip custom-provider credential files — they are handled separately
        var name = Path.GetFileName(filePath);
        if (name.StartsWith("openai-compat-", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            var text = File.ReadAllText(filePath);
            var doc  = JsonNode.Parse(text)?.AsObject();
            if (doc is null) return false;

            // Treat as active if not explicitly disabled
            if (doc["disabled"]?.GetValue<bool>() == true) return false;

            // Must contain some evidence of an authenticated session
            return doc["access_token"] != null
                || doc["token"] != null
                || doc["api_key"] != null
                || doc["oauth_token"] != null
                || doc["credentials"] != null
                || doc.Count > 1; // non-empty object signals auth data
        }
        catch { return false; }
    }
}
