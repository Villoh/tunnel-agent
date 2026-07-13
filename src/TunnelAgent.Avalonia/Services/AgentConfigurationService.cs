using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

// ── Result types ─────────────────────────────────────────────────────────────

public enum AgentSetupMode  { Proxy, Default }
public enum AgentConfigMode { Automatic, Manual }

public sealed record RawConfigPreview(string Filename, string TargetPath, string Content);

public sealed record AgentConfigApplyResult(
    bool     Success,
    string?  ConfigPath,
    string?  BackupPath,
    string?  Error,
    string   Instructions,
    IReadOnlyList<RawConfigPreview> RawPreviews)
{
    public static AgentConfigApplyResult Failure(string error) =>
        new(false, null, null, error, "", Array.Empty<RawConfigPreview>());

    public static AgentConfigApplyResult Ok(
        string instructions,
        string? configPath = null,
        string? backupPath = null,
        IReadOnlyList<RawConfigPreview>? raw = null) =>
        new(true, configPath, backupPath, null, instructions, raw ?? Array.Empty<RawConfigPreview>());
}

public sealed record ModelEntry(string Id, string OwnedBy, string EngineBaseUrl = "", string ApiKey = "", string DisplayName = "");

// ── Service ───────────────────────────────────────────────────────────────────

public sealed class AgentConfigurationService
{
    // Sentinel comment used to bracket managed blocks in TOML/shell files.
    private const string ManagedBanner = "# >>> Managed by Tunnel Agent — do not edit this block <<<";
    private const string ManagedEnd    = "# <<< End Tunnel Agent block >>>";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    // ── Public entry points ──────────────────────────────────────────────────

    /// <summary>Generate a preview of what Apply would write, without touching any files.</summary>
    public IReadOnlyList<RawConfigPreview> Preview(AgentDefinition agent, string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models = null, IReadOnlyList<ModelEntry>? modelEntries = null) =>
        GenerateRaw(agent, proxyBaseUrl, apiKey, models, modelEntries);

    /// <summary>Async preview — resolves context windows for Pi.</summary>
    public async Task<IReadOnlyList<RawConfigPreview>> PreviewAsync(AgentDefinition agent, string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models = null, IReadOnlyList<ModelEntry>? modelEntries = null, CancellationToken ct = default)
    {
        if (agent.Id == "pi" && modelEntries is { Count: > 0 })
        {
            var modelInfoMap = await BuildModelInfoMapAsync(modelEntries.Select(m => m.Id).ToList(), ct).ConfigureAwait(false);
            var configPath = ExpandPath("~/.pi/agent/models.json");
            var preview = new JsonObject
            {
                ["providers"] = BuildPiProvidersBlock(modelEntries, modelInfoMap)
            };
            return new[] { new RawConfigPreview("models.json", configPath, preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) };
        }
        if (agent.Id == "opencode" && modelEntries is { Count: > 0 })
        {
            var modelInfoMap = await BuildModelInfoMapAsync(modelEntries.Select(m => m.Id).ToList(), ct).ConfigureAwait(false);
            var configPath = ExpandPath("~/.config/opencode/opencode.json");
            var preview = new JsonObject
            {
                ["$schema"]  = "https://opencode.ai/config.json",
                ["provider"] = BuildOpenCodeProvidersBlock(modelEntries, modelInfoMap)
            };
            return new[] { new RawConfigPreview("opencode.json", configPath, preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) };
        }
        if (agent.Id == "grok-build" && modelEntries is { Count: > 0 })
        {
            var entries = GrokEntries(modelEntries);
            var modelInfoMap = await BuildModelInfoMapAsync(entries.Select(m => m.Id), ct).ConfigureAwait(false);
            var configPath = ExpandPath(GrokConfigPath);
            var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
            var content = MergeGrokConfig(existing, entries, modelInfoMap, apiKey, proxyBaseUrl, remove: false);
            return new[] { new RawConfigPreview("config.toml", configPath, content) };
        }
        return GenerateRaw(agent, proxyBaseUrl, apiKey, models, modelEntries);
    }

    /// <summary>Apply proxy configuration. Backs up existing files before writing.</summary>
    public async Task<AgentConfigApplyResult> ApplyAsync(AgentDefinition agent, string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models = null, IReadOnlyList<ModelEntry>? modelEntries = null, CancellationToken ct = default)
    {
        try
        {
            if (agent.Id == "pi")
                return await ApplyPiAsync(remove: false, modelEntries, ct).ConfigureAwait(false);
            if (agent.Id == "opencode")
                return await ApplyOpenCodeAsync(proxyBaseUrl, apiKey, remove: false, modelEntries, ct).ConfigureAwait(false);
            if (agent.Id == "grok-build")
                return await ApplyGrokBuildAsync(proxyBaseUrl, apiKey, remove: false, modelEntries, ct).ConfigureAwait(false);
            return WriteConfigSync(agent, proxyBaseUrl, apiKey, remove: false, models, modelEntries);
        }
        catch (Exception ex)
        {
            return AgentConfigApplyResult.Failure(ex.Message);
        }
    }

    /// <summary>Remove proxy configuration (restore to default).</summary>
    public AgentConfigApplyResult Revert(AgentDefinition agent) =>
        WriteConfigSync(agent, string.Empty, string.Empty, remove: true, null, null);

    // ── Config generation ────────────────────────────────────────────────────

    private IReadOnlyList<RawConfigPreview> GenerateRaw(AgentDefinition agent, string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models, IReadOnlyList<ModelEntry>? modelEntries = null) =>
        agent.Id switch
        {
            "claude-code"    => new[] { ClaudeCodeRaw(proxyBaseUrl, apiKey) },
            "codex"          => CodexRaw(proxyBaseUrl, apiKey),
            "amp"            => AmpRaw(proxyBaseUrl, apiKey),
            "opencode"       => new[] { OpenCodeRaw(proxyBaseUrl, apiKey, models) },
            "pi"             => new[] { PiRaw(proxyBaseUrl, apiKey, models) },
            "factory-droid"  => new[] { FactoryDroidRaw(proxyBaseUrl, apiKey, modelEntries) },
            "grok-build"     => new[] { GrokBuildRaw(proxyBaseUrl, apiKey, modelEntries) },
            _                => Array.Empty<RawConfigPreview>()
        };

    // ── Apply / revert ───────────────────────────────────────────────────────

    private AgentConfigApplyResult WriteConfigSync(
        AgentDefinition agent, string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<string>? models, IReadOnlyList<ModelEntry>? modelEntries)
    {
        try
        {
            return agent.Id switch
            {
                "claude-code"  => ApplyClaudeCode(proxyBaseUrl, apiKey, remove),
                "codex"        => ApplyCodex(proxyBaseUrl, apiKey, remove),
                "amp"          => ApplyAmp(proxyBaseUrl, apiKey, remove),
                "opencode"     => AgentConfigApplyResult.Failure("OpenCode requires async apply."),
                "factory-droid"=> ApplyFactoryDroid(proxyBaseUrl, apiKey, remove, modelEntries),
                "grok-build"   => ApplyGrokBuild(proxyBaseUrl, apiKey, remove, modelEntries),
                _              => AgentConfigApplyResult.Failure("Unknown agent.")
            };
        }
        catch (Exception ex)
        {
            return AgentConfigApplyResult.Failure(ex.Message);
        }
    }

    // ── Claude Code ──────────────────────────────────────────────────────────

    private static AgentConfigApplyResult ApplyClaudeCode(string proxyBaseUrl, string apiKey, bool remove)
    {
        var configPath = ExpandPath("~/.claude/settings.json");
        var dir = Path.GetDirectoryName(configPath)!;

        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // Read existing settings or start fresh
        JsonObject root;
        if (File.Exists(configPath))
        {
            root = JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject();
        }
        else
        {
            root = new JsonObject();
        }

        // Backup before modification
        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(configPath, backupPath, overwrite: false);
        }

        // Get or create the "env" object
        if (root["env"] is not JsonObject env)
        {
            env = new JsonObject();
            root["env"] = env;
        }

        if (remove)
        {
            env.Remove("ANTHROPIC_BASE_URL");
            env.Remove("ANTHROPIC_AUTH_TOKEN");
            env.Remove("ANTHROPIC_DEFAULT_OPUS_MODEL");
            env.Remove("ANTHROPIC_DEFAULT_SONNET_MODEL");
            env.Remove("ANTHROPIC_DEFAULT_HAIKU_MODEL");
            env.Remove("CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY");
            if (env.Count == 0) root.Remove("env");
        }
        else
        {
            env["ANTHROPIC_BASE_URL"]                        = StripV1(proxyBaseUrl);
            env["ANTHROPIC_AUTH_TOKEN"]                      = HasApiKey(apiKey) ? apiKey : "no-key";
            env["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1";
        }

        // Write back with indented JSON
        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(configPath, json, Utf8NoBom);

        var instructions = remove
            ? $"Removed proxy configuration from {configPath}. Claude Code will use its default Anthropic endpoint."
            : $"Configuration written to {configPath}. Restart Claude Code for changes to take effect.";

        return AgentConfigApplyResult.Ok(instructions, configPath, backupPath);
    }

    private static RawConfigPreview ClaudeCodeRaw(string proxyBaseUrl, string apiKey)
    {
        var configPath = ExpandPath("~/.claude/settings.json");
        var env = new JsonObject
        {
            ["ANTHROPIC_BASE_URL"]                       = StripV1(proxyBaseUrl),
            ["ANTHROPIC_AUTH_TOKEN"]                     = HasApiKey(apiKey) ? apiKey : "no-key",
            ["CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY"] = "1"
        };
        var content = new JsonObject { ["env"] = env }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new RawConfigPreview("settings.json", configPath, content);
    }

    // ── Codex CLI ────────────────────────────────────────────────────────────

    private AgentConfigApplyResult ApplyCodex(string proxyBaseUrl, string apiKey, bool remove)
    {
        var configPath = ExpandPath("~/.codex/config.toml");
        var authPath   = ExpandPath("~/.codex/auth.json");
        var dir        = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.WriteAllText(backupPath, existing, Utf8NoBom);
        }

        var stripped = StripManagedBlock(existing);
        if (remove)
        {
            File.WriteAllText(configPath, stripped.Trim() + "\n", Utf8NoBom);
            return AgentConfigApplyResult.Ok($"Removed proxy configuration from {configPath}.", configPath, backupPath);
        }

        File.WriteAllText(configPath, stripped.Trim() + "\n\n" + BuildCodexManagedBlock(proxyBaseUrl) + "\n", Utf8NoBom);

        var auth = File.Exists(authPath)
            ? JsonNode.Parse(File.ReadAllText(authPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();
        auth["auth_mode"]      = "apikey";
        auth["OPENAI_API_KEY"] = HasApiKey(apiKey) ? apiKey : "no-key";
        File.WriteAllText(authPath, auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        return AgentConfigApplyResult.Ok(
            $"Written {configPath} and {authPath}. Restart Codex CLI for changes to take effect.",
            configPath, backupPath,
            raw: new[] { new RawConfigPreview("auth.json", authPath, auth.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) });
    }

    private RawConfigPreview[] CodexRaw(string proxyBaseUrl, string apiKey)
    {
        var configPath = ExpandPath("~/.codex/config.toml");
        var authPath   = ExpandPath("~/.codex/auth.json");
        var authPreview = new JsonObject
        {
            ["auth_mode"]      = "apikey",
            ["OPENAI_API_KEY"] = HasApiKey(apiKey) ? apiKey : "no-key"
        };
        return new[]
        {
            new RawConfigPreview("config.toml", configPath, BuildCodexManagedBlock(proxyBaseUrl)),
            new RawConfigPreview("auth.json", authPath,
                authPreview.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))
        };
    }

    private string BuildCodexManagedBlock(string proxyBaseUrl)
    {
        var lines = new[]
        {
            ManagedBanner,
            "model_provider = \"cliproxyapi\"",
            string.Empty,
            "[model_providers.cliproxyapi]",
            "name = \"CLIProxyAPI (Tunnel Agent)\"",
            $"base_url = \"{EscapeToml(proxyBaseUrl)}\"",
            "wire_api = \"responses\"",
            ManagedEnd,
        };
        return string.Join(Environment.NewLine, lines.Where(l => l is not null));
    }

    // ── Amp CLI ───────────────────────────────────────────────────────────────

    private static AgentConfigApplyResult ApplyAmp(string proxyBaseUrl, string apiKey, bool remove)
    {
        var baseUrl      = StripV1(proxyBaseUrl);
        var settingsPath = ExpandPath("~/.config/amp/settings.json");
        var secretsPath  = ExpandPath("~/.local/share/amp/secrets.json");

        foreach (var dir in new[] { Path.GetDirectoryName(settingsPath)!, Path.GetDirectoryName(secretsPath)! })
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var settings = File.Exists(settingsPath)
            ? JsonNode.Parse(File.ReadAllText(settingsPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        string? backupPath = null;
        if (File.Exists(settingsPath))
        {
            backupPath = $"{settingsPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(settingsPath, backupPath, overwrite: false);
        }

        if (remove)
        {
            settings.Remove("amp.url");
        }
        else
        {
            settings["amp.url"] = baseUrl;

            var secrets = File.Exists(secretsPath)
                ? JsonNode.Parse(File.ReadAllText(secretsPath))?.AsObject() ?? new JsonObject()
                : new JsonObject();
            secrets[$"apiKey@{baseUrl}"] = HasApiKey(apiKey) ? apiKey : "no-key";
            File.WriteAllText(secretsPath,
                secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
        }

        File.WriteAllText(settingsPath,
            settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var msg = remove
            ? "Removed proxy config from Amp CLI settings."
            : $"Written {settingsPath} and {secretsPath}. Restart Amp CLI for changes to take effect.";
        var raw = remove ? Array.Empty<RawConfigPreview>() : new[] { new RawConfigPreview("secrets.json", secretsPath, "") };
        return AgentConfigApplyResult.Ok(msg, settingsPath, backupPath, raw);
    }

    private static RawConfigPreview[] AmpRaw(string proxyBaseUrl, string apiKey)
    {
        var baseUrl      = StripV1(proxyBaseUrl);
        var settingsPath = ExpandPath("~/.config/amp/settings.json");
        var secretsPath  = ExpandPath("~/.local/share/amp/secrets.json");
        var previews = new List<RawConfigPreview>
        {
            new("settings.json", settingsPath, $$"""
{
  "amp.url": "{{baseUrl}}"
}
""")
        };
        previews.Add(new RawConfigPreview("secrets.json", secretsPath, $$"""
{
  "apiKey@{{baseUrl}}": "{{(HasApiKey(apiKey) ? apiKey : "no-key")}}"
}
"""));
        return previews.ToArray();
    }

    // ── OpenCode ──────────────────────────────────────────────────────────────

    private const string OpenCodeCliProxyProviderKey   = "tunnel-agent-cliproxy";
    private const string OpenCodePerplexityProviderKey = "tunnel-agent-perplexity";

    private static JsonObject BuildOpenCodeProvidersBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, OpenRouterContextService.ModelInfo> modelInfoMap)
    {
        var cliproxy   = entries.Where(m => !IsPerplexityEntry(m)).ToList();
        var perplexity = entries.Where(IsPerplexityEntry).ToList();
        var providers  = new JsonObject();
        if (cliproxy.Count > 0)
            providers[OpenCodeCliProxyProviderKey]   = BuildOpenCodeProviderBlock(cliproxy, modelInfoMap);
        if (perplexity.Count > 0)
            providers[OpenCodePerplexityProviderKey] = BuildOpenCodeProviderBlock(perplexity, modelInfoMap);
        return providers;
    }

    private static JsonObject BuildOpenCodeProviderBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, OpenRouterContextService.ModelInfo> modelInfoMap)
    {
        var first   = entries[0];
        var options = new JsonObject
        {
            ["baseURL"]      = !string.IsNullOrEmpty(first.EngineBaseUrl) ? first.EngineBaseUrl : (string?)null,
            ["litellmProxy"] = true
        };
        options["apiKey"] = HasResolvedApiKey(first.ApiKey) ? $"{{env:{first.ApiKey}}}" : "no-key";

        var modelsObj = new JsonObject();
        foreach (var m in entries)
        {
            var modelEntry = new JsonObject();
            if (!string.IsNullOrEmpty(m.DisplayName))
                modelEntry["name"] = m.DisplayName;
            modelsObj[m.Id] = modelEntry;
        }

        return new JsonObject
        {
            ["name"]    = "Tunnel Agent",
            ["npm"]     = "@ai-sdk/openai-compatible",
            ["options"] = options,
            ["models"]  = modelsObj
        };
    }

    private static async Task<AgentConfigApplyResult> ApplyOpenCodeAsync(
        string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<ModelEntry>? modelEntries, CancellationToken ct)
    {
        var configPath = ExpandPath("~/.config/opencode/opencode.json");
        var dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        if (!root.ContainsKey("$schema"))
            root["$schema"] = "https://opencode.ai/config.json";

        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(configPath, backupPath, overwrite: false);
        }

        if (root["provider"] is not JsonObject providers)
        {
            providers = new JsonObject();
            root["provider"] = providers;
        }

        if (remove)
        {
            providers.Remove(OpenCodeCliProxyProviderKey);
            providers.Remove(OpenCodePerplexityProviderKey);
            if (providers.Count == 0) root.Remove("provider");
        }
        else if (modelEntries is { Count: > 0 })
        {
            var modelInfoMap = await BuildModelInfoMapAsync(modelEntries.Select(m => m.Id), ct).ConfigureAwait(false);
            foreach (var kvp in BuildOpenCodeProvidersBlock(modelEntries, modelInfoMap))
                providers[kvp.Key] = kvp.Value?.DeepClone();
        }

        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var modelCount = modelEntries?.Count ?? 0;
        var msg = remove
            ? "Removed Tunnel Agent providers. OpenCode will use its default providers."
            : $"Configuration written to {configPath}. {(modelCount > 0 ? $"{modelCount} model(s) registered. " : "")}Restart OpenCode for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, configPath, backupPath);
    }

    private static RawConfigPreview OpenCodeRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models)
    {
        var configPath = ExpandPath("~/.config/opencode/opencode.json");
        var preview    = new JsonObject
        {
            ["$schema"]  = "https://opencode.ai/config.json",
            ["provider"] = new JsonObject
            {
                [OpenCodeCliProxyProviderKey] = new JsonObject
                {
                    ["name"]    = "Tunnel Agent",
                    ["npm"]     = "@ai-sdk/openai-compatible",
                    ["options"] = new JsonObject
                    {
                        ["baseURL"]      = proxyBaseUrl,
                        ["litellmProxy"] = true,
                        ["apiKey"]       = HasApiKey(apiKey) ? "{env:TUNNEL_AGENT_CLIPROXY_API_KEY}" : "no-key"
                    }
                }
            }
        };
        return new RawConfigPreview("opencode.json", configPath,
            preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Pi ──────────────────────────────────────────────────────────────

    private const string PiCliProxyProviderKey   = "tunnel-agent-cliproxy";
    private const string PiCliProxyAnthropicProviderKey = "tunnel-agent-cliproxy-anthropic";
    private const string PiPerplexityProviderKey = "tunnel-agent-perplexity";

    private static bool IsPerplexityEntry(ModelEntry m) =>
        !string.IsNullOrEmpty(m.EngineBaseUrl) && m.EngineBaseUrl.Contains(":8327", StringComparison.Ordinal);

    private static bool IsAnthropicEntry(ModelEntry m) =>
        !string.IsNullOrEmpty(m.OwnedBy) && m.OwnedBy.Equals("anthropic", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, OpenRouterContextService.ModelInfo>> BuildModelInfoMapAsync(
        IEnumerable<string> modelIds, CancellationToken ct)
    {
        var map = new Dictionary<string, OpenRouterContextService.ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in modelIds)
        {
            var info = await OpenRouterContextService.Instance.GetModelInfoAsync(id, ct).ConfigureAwait(false);
            if (info is not null) map[id] = info;
        }
        return map;
    }

    private static JsonObject BuildPiProvidersBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, OpenRouterContextService.ModelInfo> modelInfoMap)
    {
        var cliproxy   = entries.Where(m => !IsAnthropicEntry(m) && !IsPerplexityEntry(m)).ToList();
        var cliproxyAnthropic = entries.Where(IsAnthropicEntry).ToList();
        var perplexity = entries.Where(IsPerplexityEntry).ToList();
        var providers  = new JsonObject();
        if (cliproxy.Count > 0)
            providers[PiCliProxyProviderKey]   = BuildPiProviderBlock(cliproxy, modelInfoMap, "openai-completions");
        if (cliproxyAnthropic.Count > 0)
            providers[PiCliProxyAnthropicProviderKey] = BuildPiProviderBlock(cliproxyAnthropic, modelInfoMap, "anthropic-messages");
        if (perplexity.Count > 0)
            providers[PiPerplexityProviderKey] = BuildPiProviderBlock(perplexity, modelInfoMap);
        return providers;
    }

    private static JsonObject BuildPiProviderBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, OpenRouterContextService.ModelInfo> modelInfoMap,
        string api = "openai-completions")
    {
        var first    = entries[0];
        var baseUrl  = !string.IsNullOrEmpty(first.EngineBaseUrl) ? first.EngineBaseUrl : (string?)null;
        if (api == "anthropic-messages" && !string.IsNullOrEmpty(baseUrl))
            baseUrl = StripV1(baseUrl);
        var provider = new JsonObject
        {
            ["baseUrl"] = baseUrl,
            ["api"]     = api
        };
        provider["apiKey"] = HasResolvedApiKey(first.ApiKey) ? $"${{{first.ApiKey}}}" : "no-key";
        provider["models"] = new JsonArray(
            entries.Select(m =>
            {
                var entry = new JsonObject { ["id"] = m.Id };
                if (!string.IsNullOrEmpty(m.DisplayName))
                    entry["name"] = m.DisplayName;
                if (modelInfoMap.TryGetValue(m.Id, out var info))
                {
                    if (info.ContextLength > 0)
                        entry["contextWindow"] = info.ContextLength;
                    entry["input"] = new JsonArray(info.SupportsImage
                        ? new JsonNode[] { "text", "image" }
                        : new JsonNode[] { "text" });
                    if (info.SupportsReasoning)
                        entry["reasoning"] = true;
                }
                return (JsonNode?)entry;
            }).ToArray());
        return provider;
    }

    private static async Task<AgentConfigApplyResult> ApplyPiAsync(bool remove, IReadOnlyList<ModelEntry>? modelEntries, CancellationToken ct)
    {
        var configPath = ExpandPath("~/.pi/agent/models.json");
        var dir        = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(configPath, backupPath, overwrite: false);
        }

        if (root["providers"] is not JsonObject providers)
        {
            providers = new JsonObject();
            root["providers"] = providers;
        }

        if (remove)
        {
            providers.Remove(PiCliProxyProviderKey);
            providers.Remove(PiCliProxyAnthropicProviderKey);
            providers.Remove(PiPerplexityProviderKey);
            if (providers.Count == 0) root.Remove("providers");
        }
        else if (modelEntries is { Count: > 0 })
        {
            var modelInfoMap = await BuildModelInfoMapAsync(modelEntries.Select(m => m.Id), ct).ConfigureAwait(false);
            foreach (var kvp in BuildPiProvidersBlock(modelEntries, modelInfoMap))
                providers[kvp.Key] = kvp.Value?.DeepClone();
        }

        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var modelCount = modelEntries?.Count ?? 0;
        var msg = remove
            ? "Removed Tunnel Agent providers from Pi models.json."
            : $"Configuration written to {configPath}.{(modelCount > 0 ? $" {modelCount} model(s) registered." : "")} Restart Pi for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, configPath, backupPath);
    }

    private static RawConfigPreview PiRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models)
    {
        var configPath = ExpandPath("~/.pi/agent/models.json");
        var preview    = new JsonObject
        {
            ["providers"] = new JsonObject { [PiCliProxyProviderKey] = new JsonObject
            {
                ["baseUrl"] = proxyBaseUrl,
                ["api"]     = "openai-completions",
                ["apiKey"]  = HasApiKey(apiKey) ? "${TUNNEL_AGENT_CLIPROXY_API_KEY}" : "no-key"
            }}
        };
        return new RawConfigPreview("models.json", configPath,
            preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Factory Droid ─────────────────────────────────────────────────────────

    private static AgentConfigApplyResult ApplyFactoryDroid(string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<ModelEntry>? models)
    {
        var configPath = ExpandPath("~/.factory/settings.json");
        var dir = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var root = File.Exists(configPath)
            ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() ?? new JsonObject()
            : new JsonObject();

        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(configPath, backupPath, overwrite: false);
        }

        if (root["customModels"] is not JsonArray existing)
        {
            existing = new JsonArray();
            root["customModels"] = existing;
        }

        // Remove only entries managed for the current Tunnel Agent proxy URL.
        // Do not delete unrelated local Factory Droid models that also use localhost.
        for (int i = existing.Count - 1; i >= 0; i--)
        {
            var url = existing[i]?["baseUrl"]?.GetValue<string>() ?? "";
            if (string.Equals(url.TrimEnd('/'), proxyBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                existing.RemoveAt(i);
        }

        if (!remove)
        {
            var modelEntries2 = models?.Count > 0
                ? models
                : (IEnumerable<ModelEntry>)new[] { new ModelEntry("tunnel-agent", "", "", "TUNNEL_AGENT_CLIPROXY_API_KEY") };
            foreach (var m in modelEntries2)
                existing.Add(BuildFactoryDroidEntry(m, proxyBaseUrl, apiKey));
        }

        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var modelCount = models?.Count ?? 0;
        var msg = remove
            ? "Removed proxy models from Factory Droid config."
            : $"Configuration written to {configPath}. {(modelCount > 0 ? $"{modelCount} model(s) registered. " : "")}Restart Factory Droid for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, configPath, backupPath);
    }

    private static (string provider, string baseUrl) InferFactoryDroidProvider(ModelEntry model, string proxyBaseUrl)
    {
        // Use the model's own engine base URL if provided (e.g. Perplexity endpoint)
        var effectiveBase = !string.IsNullOrEmpty(model.EngineBaseUrl) ? model.EngineBaseUrl : proxyBaseUrl;
        var owner = model.OwnedBy.ToLowerInvariant();
        if (owner == "anthropic")
            return ("anthropic", StripV1(effectiveBase));
        if (owner == "openai")
            return ("openai", effectiveBase);
        return ("generic-chat-completion-api", effectiveBase);
    }

    private static JsonObject BuildFactoryDroidEntry(ModelEntry model, string proxyBaseUrl, string apiKey)
    {
        var (provider, baseUrl) = InferFactoryDroidProvider(model, proxyBaseUrl);
        var entry = new JsonObject
        {
            ["model"]       = model.Id,
            ["displayName"] = !string.IsNullOrEmpty(model.DisplayName) ? model.DisplayName : model.Id,
            ["baseUrl"]     = baseUrl,
            ["provider"]    = provider
        };
        entry["apiKey"] = HasApiKey(model.ApiKey) ? $"${{{model.ApiKey}}}" : HasApiKey(apiKey) ? apiKey : "no-key";
        return entry;
    }

    private static RawConfigPreview FactoryDroidRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<ModelEntry>? models)
    {
        var configPath = ExpandPath("~/.factory/settings.json");
        var modelEntries = models?.Count > 0 ? models : (IEnumerable<ModelEntry>)new[] { new ModelEntry("tunnel-agent", "", "", "TUNNEL_AGENT_CLIPROXY_API_KEY") };
        var entries    = new JsonArray(modelEntries
            .Select(m => (JsonNode?)BuildFactoryDroidEntry(m, proxyBaseUrl, apiKey))
            .ToArray());
        var content    = new JsonObject { ["customModels"] = entries }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new RawConfigPreview("config.json", configPath, content);
    }

    // ── Grok Build ──────────────────────────────────────────────────────────
    //
    // Grok rewrites ~/.grok/config.toml itself (e.g. when it persists [ui] or
    // reasoning settings) and drops comment lines when it does, so a comment
    // "managed block" cannot survive. Instead we treat the file as TOML tables
    // and merge in place: a single [models] table plus one [model."<id>"] table
    // per model. Our own model tables (identified by the "(Tunnel Agent)" name
    // suffix or a localhost base_url) are replaced on every run, while the
    // user's own tables (e.g. [ui]) and [models] keys are preserved.

    private const string GrokConfigPath = "~/.grok/config.toml";

    // Synchronous entry point used only for revert (remove) — no model info needed.
    private AgentConfigApplyResult ApplyGrokBuild(string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<ModelEntry>? models)
        => WriteGrokConfig(GrokEntries(models), null, apiKey, proxyBaseUrl, remove);

    private async Task<AgentConfigApplyResult> ApplyGrokBuildAsync(
        string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<ModelEntry>? models, CancellationToken ct)
    {
        var entries = GrokEntries(models);
        var modelInfoMap = !remove
            ? await BuildModelInfoMapAsync(entries.Select(m => m.Id), ct).ConfigureAwait(false)
            : null;
        return WriteGrokConfig(entries, modelInfoMap, apiKey, proxyBaseUrl, remove);
    }

    private static IReadOnlyList<ModelEntry> GrokEntries(IReadOnlyList<ModelEntry>? models)
    {
        if (models is not { Count: > 0 }) return System.Array.Empty<ModelEntry>();
        // De-duplicate by model id to avoid duplicate [model."<id>"] tables.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return models.Where(m => seen.Add(m.Id)).ToList();
    }

    private AgentConfigApplyResult WriteGrokConfig(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, OpenRouterContextService.ModelInfo>? modelInfoMap,
        string apiKey, string proxyBaseUrl, bool remove)
    {
        var configPath = ExpandPath(GrokConfigPath);
        var dir        = Path.GetDirectoryName(configPath)!;
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.WriteAllText(backupPath, existing, Utf8NoBom);
        }

        var content = MergeGrokConfig(existing, entries, modelInfoMap, apiKey, proxyBaseUrl, remove);
        File.WriteAllText(configPath, content, Utf8NoBom);

        if (remove)
            return AgentConfigApplyResult.Ok($"Removed Tunnel Agent models from {configPath}.", configPath, backupPath);

        var modelCount = entries.Count;
        return AgentConfigApplyResult.Ok(
            $"Configuration written to {configPath}. {(modelCount > 0 ? $"{modelCount} model(s) registered. " : "")}Restart Grok for changes to take effect.",
            configPath, backupPath);
    }

    private static RawConfigPreview GrokBuildRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<ModelEntry>? models)
    {
        var configPath = ExpandPath(GrokConfigPath);
        var existing = File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty;
        var content  = MergeGrokConfig(existing, GrokEntries(models), null, apiKey, proxyBaseUrl, remove: false);
        return new RawConfigPreview("config.toml", configPath, content);
    }

    // ── Grok TOML merge ───────────────────────────────────────────────────────

    private sealed class TomlSection
    {
        public string? Header;             // trimmed header, e.g. [models] or [model."x"]; null = preamble
        public readonly List<string> Lines = new();
    }

    internal static string MergeGrokConfig(
        string existing,
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, OpenRouterContextService.ModelInfo>? modelInfoMap,
        string apiKey, string proxyBaseUrl, bool remove)
    {
        // Nothing selected and not reverting: leave the file untouched rather
        // than writing a placeholder model or wiping existing configuration.
        if (!remove && entries.Count == 0) return existing;

        var newIds  = entries.Select(e => e.Id).ToList();
        var newIdSet = new HashSet<string>(newIds, StringComparer.Ordinal);

        var sections   = SplitTomlSections(existing);
        var preamble   = new List<string>();
        var kept       = new List<List<string>>();
        List<string>? modelsBody = null;   // preserved [models] keys, excluding default
        string? oldDefault = null;
        var removedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var s in sections)
        {
            if (s.Header is null)
            {
                preamble.AddRange(s.Lines.Where(l => !IsGrokMarker(l)));
                continue;
            }
            if (s.Header == "[models]")
            {
                if (modelsBody is null)
                {
                    modelsBody = new List<string>();
                    foreach (var l in s.Lines.Skip(1))
                    {
                        var t = l.TrimStart();
                        if (t.StartsWith("default") && TomlKey(t) == "default")
                        {
                            oldDefault = TomlValue(t);
                            continue;
                        }
                        if (!IsGrokMarker(l)) modelsBody.Add(l);
                    }
                }
                continue; // re-emitted below as a single table
            }
            if (s.Header.StartsWith("[model."))
            {
                var id = ExtractGrokModelId(s.Header);
                var isReplacing = id != null && newIdSet.Contains(id);
                if (isReplacing || IsManagedGrokModel(s.Lines))
                {
                    if (id != null) removedIds.Add(id);
                    continue; // drop (replaced or Tunnel Agent-managed)
                }
            }
            kept.Add(s.Lines.Where(l => !IsGrokMarker(l)).ToList());
        }

        var outp = new List<string>();
        TrimTrailingBlanks(preamble);
        if (preamble.Count > 0) { outp.AddRange(preamble); outp.Add(string.Empty); }

        foreach (var sec in kept)
        {
            TrimTrailingBlanks(sec);
            if (sec.Count == 0) continue;
            outp.AddRange(sec);
            outp.Add(string.Empty);
        }

        // [models] table — keep a single one, preserving user keys.
        var modelsLines = new List<string>();
        if (!remove)
        {
            modelsLines.Add($"default = \"{EscapeToml(newIds[0])}\"");
        }
        else if (oldDefault != null && !removedIds.Contains(oldDefault))
        {
            modelsLines.Add($"default = \"{EscapeToml(oldDefault)}\"");
        }
        if (modelsBody != null)
            foreach (var l in modelsBody)
                if (!string.IsNullOrWhiteSpace(l)) modelsLines.Add(l);
        if (modelsLines.Count > 0)
        {
            outp.Add("[models]");
            outp.AddRange(modelsLines);
            outp.Add(string.Empty);
        }

        if (!remove)
        {
            foreach (var m in entries)
            {
                outp.AddRange(BuildGrokModelTable(m, modelInfoMap, apiKey, proxyBaseUrl));
                outp.Add(string.Empty);
            }
        }

        return string.Join(Environment.NewLine, outp).TrimEnd() + Environment.NewLine;
    }

    private static IEnumerable<string> BuildGrokModelTable(
        ModelEntry m,
        Dictionary<string, OpenRouterContextService.ModelInfo>? modelInfoMap,
        string apiKey, string proxyBaseUrl)
    {
        // Anthropic models only expose reasoning/thinking through their native
        // Messages API, so route them via api_backend = "messages". Everything
        // else goes through the OpenAI-compatible chat_completions endpoint.
        // In both cases base_url keeps its "/v1" suffix: Grok appends "/messages"
        // or "/chat/completions", yielding "{base}/v1/messages" and
        // "{base}/v1/chat/completions" respectively (both served by CLIProxyAPI).
        var isAnthropic = m.OwnedBy.Equals("anthropic", StringComparison.OrdinalIgnoreCase);
        var baseUrl = !string.IsNullOrEmpty(m.EngineBaseUrl) ? m.EngineBaseUrl : proxyBaseUrl;
        var lines = new List<string>
        {
            $"[model.\"{EscapeToml(m.Id)}\"]",
            $"model = \"{EscapeToml(m.Id)}\""
        };
        if (!string.IsNullOrEmpty(m.DisplayName))
            lines.Add($"name = \"{EscapeToml(m.DisplayName)}\"");
        lines.Add($"base_url = \"{EscapeToml(baseUrl)}\"");
        if (HasResolvedApiKey(m.ApiKey))
            lines.Add($"env_key = \"{EscapeToml(m.ApiKey)}\"");
        else if (string.IsNullOrEmpty(m.ApiKey) || m.ApiKey == "TUNNEL_AGENT_CLIPROXY_API_KEY")
            lines.Add($"api_key = \"{(HasApiKey(apiKey) ? EscapeToml(apiKey) : "no-key")}\"");
        else
            lines.Add("api_key = \"no-key\"");
        lines.Add(isAnthropic ? "api_backend = \"messages\"" : "api_backend = \"chat_completions\"");

        // Enable Grok's /effort command for models that report reasoning support
        // (resolved from OpenRouter), mirroring Pi's reasoning flag. We only
        // declare support and let the user pick the level per session via /effort.
        if (modelInfoMap is not null && modelInfoMap.TryGetValue(m.Id, out var info) && info.SupportsReasoning)
            lines.Add("supports_reasoning_effort = true");
        return lines;
    }

    private static List<TomlSection> SplitTomlSections(string text)
    {
        var result = new List<TomlSection>();
        var current = new TomlSection();
        foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                result.Add(current);
                current = new TomlSection { Header = raw.Trim() };
            }
            current.Lines.Add(raw);
        }
        result.Add(current);
        return result;
    }

    // Legacy files written with the old comment-marker approach may still contain
    // these lines after Grok rewrote them; strip them so they don't linger.
    private static bool IsGrokMarker(string line) =>
        line.Contains("Managed by Tunnel Agent", StringComparison.Ordinal) ||
        line.Contains("End Tunnel Agent block", StringComparison.Ordinal);

    private static bool IsManagedGrokModel(IEnumerable<string> lines)
    {
        var name    = ExtractTomlString(lines, "name");
        var baseUrl = ExtractTomlString(lines, "base_url");
        if (name != null && name.Contains("(Tunnel Agent", StringComparison.OrdinalIgnoreCase))
            return true;
        if (baseUrl != null && (baseUrl.Contains("127.0.0.1", StringComparison.Ordinal) ||
                                baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static string? ExtractGrokModelId(string header)
    {
        var h = header.Trim();
        if (!h.StartsWith("[model.", StringComparison.Ordinal) || !h.EndsWith("]", StringComparison.Ordinal))
            return null;
        var inner = h["[model.".Length..^1].Trim();
        if (inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"')
            inner = inner[1..^1];
        return inner;
    }

    private static string? ExtractTomlString(IEnumerable<string> lines, string key)
    {
        foreach (var l in lines)
        {
            var t = l.TrimStart();
            if (TomlKey(t) == key)
                return TomlValue(t);
        }
        return null;
    }

    private static string? TomlKey(string trimmedLine)
    {
        var eq = trimmedLine.IndexOf('=');
        if (eq < 0) return null;
        return trimmedLine[..eq].Trim();
    }

    private static string? TomlValue(string trimmedLine)
    {
        var eq = trimmedLine.IndexOf('=');
        if (eq < 0) return null;
        var v = trimmedLine[(eq + 1)..].Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') return v[1..^1];
        return v.Trim('"');
    }

    private static void TrimTrailingBlanks(List<string> lines)
    {
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasApiKey(string apiKey) => !string.IsNullOrWhiteSpace(apiKey);

    // ModelEntry.ApiKey holds the env var *name* (e.g. TUNNEL_AGENT_CLIPROXY_API_KEY),
    // which is never empty. Resolve it to its actual value so that, when the variable
    // does not exist (and is therefore absent from proxy-config.yaml), we emit
    // "no-key" instead of a placeholder that would expand to an empty apiKey and error.
    private static bool HasResolvedApiKey(string varName) =>
        HasApiKey(varName) &&
        !string.IsNullOrWhiteSpace(TunnelAgent.Infrastructure.Services.UserEnvironmentService.Get(varName));

    private static string StripV1(string url) =>
        url.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? url[..^3] : url;

    internal static string ExpandPath(string path)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(home, path[2..].Replace('/', Path.DirectorySeparatorChar));
        return path;
    }

    private string StripManagedBlock(string content)
    {
        var lines = content.Split('\n');
        var result = new List<string>();
        var inBlock = false;
        foreach (var line in lines)
        {
            if (line.TrimEnd() == ManagedBanner) { inBlock = true; continue; }
            if (line.TrimEnd() == ManagedEnd)    { inBlock = false; continue; }
            if (!inBlock) result.Add(line);
        }
        // Trim trailing blank lines
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);
        return string.Join('\n', result);
    }

    private static string EscapeToml(string value) =>
        value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
