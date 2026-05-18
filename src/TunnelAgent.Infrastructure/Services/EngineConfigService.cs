using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Generates and writes config.yaml for CLIProxyAPI.
/// Supports OAuth provider exclusions and multi-account OpenAI-compat providers.
/// </summary>
public sealed class EngineConfigService
{
    private static readonly IPlatformInfo Platform = IPlatformInfo.Current;

    private readonly SettingsService _settings;
    private readonly CustomProviderCredentialStore _credentialStore;
    private readonly string _authDir;

    public string ConfigPath { get; }

    public EngineConfigService(SettingsService settings)
        : this(settings, null, null, null) { }

    public EngineConfigService(
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
    /// Writes config.yaml from current AppSettings. Must be called before starting the process.
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

        sb.Append($"""
            host: "127.0.0.1"
            port: {s.Port}
            auth-dir: "{authDir}"
            api-keys: []
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

    private List<string> BuildCustomProviderEntries(AppSettings s)
    {
        var entries = new List<string>();

        foreach (var ps in s.Providers.Where(p => p.Enabled && !string.IsNullOrEmpty(p.BaseUrl)))
        {
            // Gather active keys: settings (inline) + credential store files
            var activeKeys = new List<string>();

            // From persisted account settings (inline / previously migrated)
            activeKeys.AddRange(ps.Accounts
                .Where(a => !a.Disabled && !string.IsNullOrWhiteSpace(a.ApiKey))
                .Select(a => a.ApiKey));

            // From credential store files (source of truth for UI-added accounts)
            activeKeys.AddRange(_credentialStore
                .LoadForProvider(ps.Id)
                .Where(r => !r.IsDisabled)
                .Select(r => r.ApiKey));

            // Deduplicate preserving order
            var seen      = new HashSet<string>();
            var dedupKeys = activeKeys.Where(k => seen.Add(k)).ToList();

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

    // ── YAML helpers ─────────────────────────────────────────────────────────

    private static string YamlKey(string s) =>
        Regex.IsMatch(s, @"^[A-Za-z0-9_\-]+$") ? s : YamlQuote(s);

    private static string YamlQuote(string s) =>
        $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
}
