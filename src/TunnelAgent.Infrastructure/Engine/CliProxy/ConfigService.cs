using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

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
    private readonly CustomProviderCredentialStore _credentialStore;
    private readonly string _authDir;

    public string ConfigPath { get; }

    public ConfigService(SettingsService settings)
        : this(settings, null, null, null) { }

    public ConfigService(
        SettingsService settings,
        string? configPath,
        string? authDir,
        CustomProviderCredentialStore? credentialStore = null)
    {
        _settings        = settings;
        _authDir         = authDir ?? Platform.AuthDirectory;
        _credentialStore = credentialStore ?? new CustomProviderCredentialStore(_authDir);
        ConfigPath       = configPath ?? Path.Combine(Platform.SettingsDirectory, "proxy-config.yaml");
    }

    /// <summary>
    /// Reads provider intent from proxy-config.yaml. This keeps proxy-config.yaml
    /// as source of truth while AppSettings keeps only app/UI preferences.
    /// </summary>
    public List<ProviderSettings> ReadProviderSettingsFromConfig()
    {
        if (!File.Exists(ConfigPath)) return [];
        var lines = File.ReadAllLines(ConfigPath);
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
                for (i++; i < lines.Length && (lines[i].StartsWith("  ") || string.IsNullOrWhiteSpace(lines[i])); i++)
                {
                    var t = lines[i].Trim();
                    if (t.StartsWith("- name:", System.StringComparison.Ordinal))
                    {
                        if (current is not null && !string.IsNullOrWhiteSpace(current.Id)) result.Add(current);
                        current = new ProviderSettings { Id = Unyaml(t[7..].Trim()), Enabled = true };
                    }
                    else if (current is not null && t.StartsWith("display-name:", System.StringComparison.Ordinal))
                    {
                        current.DisplayName = Unyaml(t[13..].Trim());
                    }
                    else if (current is not null && t.StartsWith("base-url:", System.StringComparison.Ordinal))
                    {
                        current.BaseUrl = Unyaml(t[9..].Trim());
                    }
                }
                if (current is not null && !string.IsNullOrWhiteSpace(current.Id)) result.Add(current);
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
    public async Task WriteConfigAsync()
    {
        var s       = _settings.Current;
        var authDir = _authDir.Replace('\\', '/');
        var yaml    = BuildYaml(s, authDir);

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, yaml);
    }

    // ── YAML builder ─────────────────────────────────────────────────────────

    private string BuildYaml(AppSettings s, string authDir)
    {
        var sb = new StringBuilder();

        var routingStrategy = s.RoutingStrategy switch
        {
            ViewModels.RoutingStrategy.FillFirst => "fill-first",
            _ => "round-robin"
        };

        sb.AppendLine("host: \"127.0.0.1\"");
        sb.AppendLine($"port: {s.Port}");
        sb.AppendLine($"auth-dir: \"{authDir}\"");

        AppendApiKeys(sb, s.CliProxyApiKeys);

        sb.Append($"""
            debug: false
            routing:
              strategy: "{routingStrategy}"
            remote-management:
              disable-control-panel: true

            """);

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

        foreach (var ps in s.Providers.Where(p => p.Enabled && !string.IsNullOrEmpty(p.BaseUrl)))
        {
            var dedupKeys = GetActiveProviderKeys(ps);
            if (dedupKeys.Count == 0) continue;

            var sb = new StringBuilder();
            sb.AppendLine($"  - name: {YamlKey(ps.Id)}");
            if (!string.IsNullOrEmpty(ps.DisplayName))
                sb.AppendLine($"    display-name: {YamlQuote(ps.DisplayName)}");
            sb.AppendLine($"    base-url: {YamlQuote(ps.BaseUrl)}");
            sb.AppendLine($"    api-key-entries:");
            foreach (var key in dedupKeys)
                sb.AppendLine($"      - api-key: {YamlQuote(key)}");

            entries.Add(sb.ToString());
        }

        return entries;
    }

    private List<string> GetActiveProviderKeys(ProviderSettings ps)
    {
        var activeKeys = new List<string>();

        activeKeys.AddRange(ps.Accounts
            .Where(a => !a.Disabled && !string.IsNullOrWhiteSpace(a.ApiKey))
            .Select(a => a.ApiKey));

        activeKeys.AddRange(_credentialStore
            .LoadForProvider(ps.Id)
            .Where(r => !r.IsDisabled)
            .Select(r => r.ApiKey));

        var seen = new HashSet<string>();
        return activeKeys.Where(k => seen.Add(k)).ToList();
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
