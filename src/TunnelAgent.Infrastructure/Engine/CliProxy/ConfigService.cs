using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using TunnelAgent.Infrastructure.Services;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Engine.CliProxy;

/// <summary>
/// Generates and writes proxy config for CLIProxyAPI.
/// Supports OAuth provider exclusions and multi-account OpenAI-compat providers.
/// </summary>
public sealed class ConfigService
{
    private static readonly IPlatformInfo Platform = IPlatformInfo.Current;

    private readonly SettingsService _settings;
    private readonly string _authDir;

    public string ConfigPath { get; }

    public ConfigService(SettingsService settings)
        : this(settings, null, null) { }

    public ConfigService(
        SettingsService settings,
        string? configPath,
        string? authDir,
        CustomProviderCredentialStore? credentialStore = null)
    {
        _settings        = settings;
        _authDir         = authDir ?? Platform.AuthDirectory;
        ConfigPath       = configPath ?? Path.Combine(Platform.SettingsDirectory, "proxy-config.yaml");
    }

    /// <summary>
    /// Reads provider intent from proxy-config.yaml. This keeps proxy-config.yaml
    /// as source of truth while AppSettings keeps only app/UI preferences.
    /// </summary>
    public async Task<List<ProviderSettings>> ReadProviderSettingsFromConfigAsync()
    {
        if (!File.Exists(ConfigPath)) return [];
        var lines = await File.ReadAllLinesAsync(ConfigPath);
        var result = new List<ProviderSettings>();

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "oauth-excluded-models:")
            {
                for (i++; i < lines.Length && lines[i].StartsWith("  "); i++)
                {
                    var t = lines[i].Trim();
                    if (!t.EndsWith(":", System.StringComparison.Ordinal)) continue;
                    var id = Unyaml(t[..^1]);
                    if (!string.IsNullOrWhiteSpace(id))
                        result.Add(new ProviderSettings { Id = id, Enabled = false });
                }
                i--;
                continue;
            }

            if (line.Trim() == "openai-compatibility:")
            {
                ProviderSettings? current = null;
                ProviderAccountSettings? currentAccount = null;
                for (i++; i < lines.Length && (lines[i].StartsWith("  ") || string.IsNullOrWhiteSpace(lines[i])); i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("- name:", System.StringComparison.Ordinal))
                    {
                        if (current is not null && !string.IsNullOrWhiteSpace(current.Id)) result.Add(current);
                        current = new ProviderSettings { Id = Unyaml(t[7..].Trim()), Enabled = true, Kind = ProviderKind.OpenAICompatibility };
                        currentAccount = null;
                    }
                    else if (current is not null && t.StartsWith("display-name:", System.StringComparison.Ordinal))
                    {
                        current.DisplayName = Unyaml(t[13..].Trim());
                    }
                    else if (current is not null && t.StartsWith("disabled:", System.StringComparison.Ordinal))
                    {
                        current.Enabled = !string.Equals(Unyaml(t[9..].Trim()), "true", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (current is not null && t.StartsWith("base-url:", System.StringComparison.Ordinal))
                    {
                        current.BaseUrl = Unyaml(t[9..].Trim());
                    }
                    else if (current is not null && t.StartsWith("- api-key:", System.StringComparison.Ordinal))
                    {
                        var key = Unyaml(t[10..].Trim());
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            currentAccount = new ProviderAccountSettings { ApiKey = key };
                            current.Accounts.Add(currentAccount);
                        }
                    }
                    else if (currentAccount is not null && t.StartsWith("label:", System.StringComparison.Ordinal))
                    {
                        currentAccount.Label = Unyaml(t[6..].Trim());
                    }
                }
                if (current is not null && !string.IsNullOrWhiteSpace(current.Id)) result.Add(current);
                i--;
                continue;
            }

            var apiKeyKind = line.Trim() switch
            {
                "claude-api-key:" => ProviderKind.ClaudeApiKey,
                "gemini-api-key:" => ProviderKind.GeminiApiKey,
                "codex-api-key:"  => ProviderKind.CodexApiKey,
                _ => (ProviderKind?)null
            };
            if (apiKeyKind is { } kind)
            {
                var id = kind switch
                {
                    ProviderKind.ClaudeApiKey => "claude",
                    ProviderKind.CodexApiKey  => "codex",
                    _ => "gemini-cli"
                };
                var current = new ProviderSettings { Id = id, Enabled = true, Kind = kind };
                ProviderAccountSettings? currentAccount = null;
                for (i++; i < lines.Length && (lines[i].StartsWith("  ") || string.IsNullOrWhiteSpace(lines[i])); i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("- api-key:", System.StringComparison.Ordinal))
                    {
                        var key = Unyaml(t[10..].Trim());
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            currentAccount = new ProviderAccountSettings { ApiKey = key };
                            current.Accounts.Add(currentAccount);
                        }
                    }
                    else if (currentAccount is not null && t.StartsWith("label:", System.StringComparison.Ordinal))
                    {
                        currentAccount.Label = Unyaml(t[6..].Trim());
                    }
                    else if (t.StartsWith("base-url:", System.StringComparison.Ordinal))
                        current.BaseUrl = Unyaml(t[9..].Trim());
                }
                result.Add(current);
                i--;
            }
        }

        return result
            .GroupBy(p => p.Id)
            .Select(g => g.Last())
            .ToList();
    }

    /// <summary>
    /// Writes config from current AppSettings. Must be called before starting the process.
    /// </summary>
    public async Task WriteConfigAsync(bool forceManagementKey = false)
    {
        var s       = _settings.Current;
        var authDir = _authDir.Replace('\\', '/');
        var yaml    = await BuildYamlAsync(s, authDir, forceManagementKey);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, yaml);
    }

    /// <summary>
    /// One-time migration: reads CliProxyApiKeys / DefaultCliProxyApiKey from the legacy
    /// settings.json (before they were removed) and writes them to proxy-config.yaml.
    /// Safe to call on every startup — no-ops if keys already exist in yaml or settings has none.
    /// </summary>
    public async Task MigrateApiKeysFromSettingsAsync(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return;
        try
        {
            var json = await File.ReadAllTextAsync(settingsPath);
            var node = JsonNode.Parse(json);
            if (node is not JsonObject obj) return;

            var legacyKeys = obj["CliProxyApiKeys"]?.AsArray()
                .Select(n => n?.GetValue<string>() ?? "")
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList() ?? [];
            var legacyDefault = obj["DefaultCliProxyApiKey"]?.GetValue<string>() ?? "";

            if (legacyKeys.Count == 0) return;

            // Only migrate if yaml has no api-keys yet
            var existing = await ReadApiKeysFromConfigAsync();
            if (existing.Count == 0)
                await WriteApiKeysToConfigAsync(legacyKeys);

            // Migrate default key to env var if not already set
            if (!string.IsNullOrWhiteSpace(legacyDefault) &&
                string.IsNullOrWhiteSpace(TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get("TUNNEL_AGENT_CLIPROXY_API_KEY")))
            {
                UserEnvironmentService.Set("TUNNEL_AGENT_CLIPROXY_API_KEY", legacyDefault);
            }
        }
        catch { /* migration is best-effort */ }
    }

    // ── YAML builder ─────────────────────────────────────────────────────────

    private async Task<string> BuildYamlAsync(AppSettings s, string authDir, bool forceManagementKey)
    {
        var sb = new StringBuilder();

        var routingStrategy = s.RoutingStrategy switch
        {
            ViewModels.RoutingStrategy.FillFirst => "fill-first",
            _ => "round-robin"
        };

        var runtime = s.GetOrAddEngine(Core.Engine.EngineCatalog.CliProxyApi.Id, Core.Engine.EngineCatalog.CliProxyApi.DefaultPort);

        sb.AppendLine("host: \"127.0.0.1\"");
        sb.AppendLine($"port: {runtime.Port}");
        sb.AppendLine($"auth-dir: \"{authDir}\"");

        AppendApiKeys(sb, await ReadApiKeysFromConfigAsync());

        // Preserve existing secret-key from config (CLIProxyAPI bcrypt-hashes it on startup).
        // Only write the plain key from settings if the config has no key yet — same as Quotio.
        var existingSecretKey = forceManagementKey ? null : await ReadSecretKeyFromConfigAsync();
        var secretKey = string.IsNullOrWhiteSpace(existingSecretKey) ? s.ManagementKey : existingSecretKey;

        sb.Append($"""
            debug: false
            logging-to-file: true
            routing:
              strategy: "{routingStrategy}"
            remote-management:
              disable-control-panel: true
              secret-key: "{secretKey}"

            """);

        // ── ampcode ──────────────────────────────────────────────────────────────
        var ampKey = await ReadExistingAmpUpstreamApiKeyAsync();
        if (!string.IsNullOrWhiteSpace(ampKey))
        {
            sb.AppendLine("ampcode:");
            sb.AppendLine("  upstream-url: \"https://ampcode.com\"");
            sb.AppendLine($"  upstream-api-key: {YamlQuote(ampKey)}");
            sb.AppendLine();
        }

        // ── oauth-excluded-models ─────────────────────────────────────────────
        var disabledOAuth = s.Providers
            .Where(p => !p.Enabled && OAuthTokenDetector.KnownProviders.ContainsKey(p.Id))
            .Select(p => p.Id)
            .OrderBy(x => x)
            .ToList();

        if (disabledOAuth.Count > 0)
        {
            sb.AppendLine("oauth-excluded-models:");
            foreach (var id in disabledOAuth)
            {
                sb.AppendLine($"  {YamlKey(id)}:");
                sb.AppendLine($"    - \"*\"");
            }
            sb.AppendLine();
        }

        // ── openai-compatibility ──────────────────────────────────────────────
        var customEntries = BuildCustomProviderEntries(s);
        if (customEntries.Count > 0)
        {
            sb.AppendLine("openai-compatibility:");
            foreach (var entry in customEntries)
                sb.Append(entry);
        }

        AppendNativeApiKeyProviders(sb, s);

        return sb.ToString();
    }

    private static void AppendApiKeys(StringBuilder sb, IEnumerable<string> apiKeys)
    {
        var keys = GetApiKeys(apiKeys);

        if (keys.Count == 0)
            return;

        sb.AppendLine("api-keys:");
        foreach (var key in keys)
            sb.AppendLine($"  - {YamlQuote(key)}");
    }

    private static List<string> GetApiKeys(IEnumerable<string> apiKeys) => apiKeys
        .Where(k => !string.IsNullOrWhiteSpace(k))
        .Distinct()
        .ToList();

    private List<string> BuildCustomProviderEntries(AppSettings s)
    {
        var entries = new List<string>();

        foreach (var ps in s.Providers.Where(p => p.Kind == ProviderKind.OpenAICompatibility && !string.IsNullOrEmpty(p.BaseUrl)))
        {
            var dedupKeys = GetActiveProviderKeyEntries(ps);
            if (dedupKeys.Count == 0) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"  - name: {YamlKey(ps.Id)}");
            sb.AppendLine($"    disabled: {(!ps.Enabled).ToString().ToLowerInvariant()}");
            sb.AppendLine($"    base-url: {YamlQuote(ps.BaseUrl)}");
            sb.AppendLine($"    api-key-entries:");
            foreach (var key in dedupKeys)
            {
                sb.AppendLine($"      - api-key: {YamlQuote(key.ApiKey)}");
                if (!string.IsNullOrWhiteSpace(key.Label))
                    sb.AppendLine($"        label: {YamlQuote(key.Label)}");
            }

            entries.Add(sb.ToString());
        }

        return entries;
    }

    private void AppendNativeApiKeyProviders(StringBuilder sb, AppSettings s)
    {
        AppendNativeApiKeyProvider(sb, s, ProviderKind.ClaudeApiKey, "claude-api-key");
        AppendNativeApiKeyProvider(sb, s, ProviderKind.GeminiApiKey, "gemini-api-key");
        AppendNativeApiKeyProvider(sb, s, ProviderKind.CodexApiKey, "codex-api-key");
    }

    private void AppendNativeApiKeyProvider(StringBuilder sb, AppSettings s, ProviderKind kind, string blockName)
    {
        var providers = s.Providers.Where(p => p.Enabled && p.Kind == kind).ToList();
        var entries = providers
            .SelectMany(ps => GetActiveProviderKeyEntries(ps).Select(key => (key.ApiKey, key.Label, ps.BaseUrl)))
            .GroupBy(x => x.ApiKey)
            .Select(g => g.First())
            .ToList();
        if (entries.Count == 0) return;

        sb.AppendLine($"{blockName}:");
        foreach (var (apiKey, label, baseUrl) in entries)
        {
            sb.AppendLine($"  - api-key: {YamlQuote(apiKey)}");
            if (!string.IsNullOrWhiteSpace(label))
                sb.AppendLine($"    label: {YamlQuote(label)}");
            if (!string.IsNullOrWhiteSpace(baseUrl))
                sb.AppendLine($"    base-url: {YamlQuote(baseUrl)}");
        }
        sb.AppendLine();
    }

    private static List<ProviderAccountSettings> GetActiveProviderKeyEntries(ProviderSettings ps)
    {
        var seen = new HashSet<string>();
        return ps.Accounts
            .Where(a => !string.IsNullOrWhiteSpace(a.ApiKey))
            .Where(a => seen.Add(a.ApiKey))
            .ToList();
    }

    // ── Existing config readers ─────────────────────────────────────────────

    public async Task<List<string>> ReadApiKeysFromConfigAsync()
    {
        if (!File.Exists(ConfigPath)) return [];
        var keys = new List<string>();
        var lines = await File.ReadAllLinesAsync(ConfigPath);
        var inBlock = false;
        foreach (var line in lines)
        {
            if (line.TrimEnd() == "api-keys:") { inBlock = true; continue; }
            if (inBlock)
            {
                if (!line.StartsWith(" ") && !line.StartsWith("\t")) { inBlock = false; continue; }
                var t = line.Trim();
                if (t.StartsWith("- ")) keys.Add(Unyaml(t[2..].Trim()));
            }
        }
        return keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task WriteApiKeysToConfigAsync(IEnumerable<string> apiKeys)
    {
        if (!File.Exists(ConfigPath))
        {
            await WriteConfigAsync().ConfigureAwait(false);
            return;
        }

        var lines = (await File.ReadAllLinesAsync(ConfigPath).ConfigureAwait(false)).ToList();

        // Remove existing api-keys block
        var start = lines.FindIndex(l => l.TrimEnd() == "api-keys:");
        if (start >= 0)
        {
            var end = start + 1;
            while (end < lines.Count && (lines[end].StartsWith(" ") || lines[end].StartsWith("\t")))
                end++;
            lines.RemoveRange(start, end - start);
        }

        var keys = GetApiKeys(apiKeys);
        if (keys.Count > 0)
        {
            // Insert after auth-dir line
            var authDirIdx = lines.FindIndex(l => l.TrimStart().StartsWith("auth-dir:", System.StringComparison.Ordinal));
            var insertAt = authDirIdx >= 0 ? authDirIdx + 1 : 0;
            var block = new List<string> { "api-keys:" };
            block.AddRange(keys.Select(k => $"  - {YamlQuote(k)}"));
            lines.InsertRange(insertAt, block);
        }

        await File.WriteAllLinesAsync(ConfigPath, lines).ConfigureAwait(false);
    }

    public async Task<string> GetAmpUpstreamApiKeyAsync() => (await ReadExistingAmpUpstreamApiKeyAsync()) ?? "";

    public async Task SetAmpUpstreamApiKeyAsync(string apiKey)
    {
        if (!File.Exists(ConfigPath))
        {
            await WriteConfigAsync().ConfigureAwait(false);
            return;
        }

        var lines = (await File.ReadAllLinesAsync(ConfigPath).ConfigureAwait(false)).ToList();

        // Remove existing ampcode block
        var start = lines.FindIndex(l => l.TrimEnd() == "ampcode:");
        if (start >= 0)
        {
            var end = start + 1;
            while (end < lines.Count && (lines[end].StartsWith(" ") || lines[end].StartsWith("\t")))
                end++;
            lines.RemoveRange(start, end - start);
            if (start > 0 && string.IsNullOrWhiteSpace(lines[start - 1]))
                lines.RemoveAt(start - 1);
        }

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            lines.Add("");
            lines.Add("ampcode:");
            lines.Add("  upstream-url: \"https://ampcode.com\"");
            lines.Add($"  upstream-api-key: {YamlQuote(apiKey)}");
        }

        await File.WriteAllLinesAsync(ConfigPath, lines).ConfigureAwait(false);
    }

    private async Task<string?> ReadSecretKeyFromConfigAsync()
    {
        if (!File.Exists(ConfigPath)) return null;
        var lines = await File.ReadAllLinesAsync(ConfigPath);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("secret-key:", System.StringComparison.Ordinal))
                return Unyaml(t["secret-key:".Length..].Trim());
        }
        return null;
    }

    private async Task<string?> ReadExistingAmpUpstreamApiKeyAsync()
    {
        if (!File.Exists(ConfigPath)) return null;
        var lines = await File.ReadAllLinesAsync(ConfigPath);
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith("upstream-api-key:", System.StringComparison.Ordinal))
                return Unyaml(t["upstream-api-key:".Length..].Trim());
        }
        return null;
    }

    // ── YAML helpers ─────────────────────────────────────────────────────────

    private static string YamlKey(string s) =>
        Regex.IsMatch(s, @"^[A-Za-z0-9_\-]+$") ? s : YamlQuote(s);

    private static string YamlQuote(string s) =>
        $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string Unyaml(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return value[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        return value;
    }
}
