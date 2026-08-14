using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

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
        if (agent.Id == "omp" && modelEntries is { Count: > 0 })
        {
            var modelInfoMap = await BuildModelInfoMapAsync(modelEntries.Select(m => m.Id), ct).ConfigureAwait(false);
            var configPath = ExpandPath("~/.omp/agent/models.yml");
            var existing = File.Exists(configPath) ? await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false) : string.Empty;
            var content = MergeOmpModelsYaml(existing, modelEntries, modelInfoMap, remove: false);
            return new[] { new RawConfigPreview("models.yml", configPath, content) };
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
            if (agent.Id == "omp")
                return await ApplyOmpAsync(remove: false, modelEntries, ct).ConfigureAwait(false);
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
    public AgentConfigApplyResult Revert(AgentDefinition agent, IReadOnlyCollection<int>? managedPorts = null)
    {
        if (agent.Id == "pi")
            return ApplyPiAsync(remove: true, modelEntries: null, CancellationToken.None).GetAwaiter().GetResult();
        if (agent.Id == "omp")
            return ApplyOmpAsync(remove: true, modelEntries: null, CancellationToken.None).GetAwaiter().GetResult();
        if (agent.Id == "opencode")
            return ApplyOpenCodeAsync(string.Empty, string.Empty, remove: true, modelEntries: null, CancellationToken.None).GetAwaiter().GetResult();
        if (agent.Id == "grok-build")
            return WriteGrokConfig(Array.Empty<ModelEntry>(), null, string.Empty, string.Empty, remove: true, managedPorts);
        return WriteConfigSync(agent, string.Empty, string.Empty, remove: true, null, null);
    }

    // ── Config generation ────────────────────────────────────────────────────

    private IReadOnlyList<RawConfigPreview> GenerateRaw(AgentDefinition agent, string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models, IReadOnlyList<ModelEntry>? modelEntries = null) =>
        agent.Id switch
        {
            "claude-code"    => new[] { ClaudeCodeRaw(proxyBaseUrl, apiKey) },
            "codex"          => CodexRaw(proxyBaseUrl, apiKey),
            "amp"            => AmpRaw(proxyBaseUrl, apiKey),
            "opencode"       => new[] { OpenCodeRaw(proxyBaseUrl, apiKey, models) },
            "pi"             => new[] { PiRaw(proxyBaseUrl, apiKey, models) },
            "omp"            => new[] { OmpRaw(proxyBaseUrl, apiKey, models) },
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
                "omp"          => AgentConfigApplyResult.Failure("Oh My Pi requires async apply."),
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

        var (settingsContent, secretsContent) = MergeAmpConfig(
            File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : string.Empty,
            File.Exists(secretsPath) ? File.ReadAllText(secretsPath) : string.Empty,
            baseUrl, apiKey, remove);

        string? backupPath = null;
        if (File.Exists(settingsPath))
        {
            backupPath = $"{settingsPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(settingsPath, backupPath, overwrite: false);
        }

        File.WriteAllText(settingsPath, settingsContent, Utf8NoBom);
        File.WriteAllText(secretsPath, secretsContent, Utf8NoBom);

        var msg = remove
            ? "Removed proxy config from Amp CLI settings."
            : $"Written {settingsPath} and {secretsPath}. Restart Amp CLI for changes to take effect.";
        var raw = remove ? Array.Empty<RawConfigPreview>() : new[] { new RawConfigPreview("secrets.json", secretsPath, "") };
        return AgentConfigApplyResult.Ok(msg, settingsPath, backupPath, raw);
    }

    internal static (string Settings, string Secrets) MergeAmpConfig(
        string settingsContent, string secretsContent, string baseUrl, string apiKey, bool remove)
    {
        var settings = string.IsNullOrWhiteSpace(settingsContent)
            ? new JsonObject()
            : JsonNode.Parse(settingsContent)?.AsObject() ?? new JsonObject();
        var secrets = string.IsNullOrWhiteSpace(secretsContent)
            ? new JsonObject()
            : JsonNode.Parse(secretsContent)?.AsObject() ?? new JsonObject();

        if (remove)
        {
            var configuredUrl = settings["amp.url"]?.GetValue<string>();
            settings.Remove("amp.url");
            if (!string.IsNullOrEmpty(configuredUrl))
                secrets.Remove($"apiKey@{configuredUrl}");
        }
        else
        {
            settings["amp.url"] = baseUrl;
            secrets[$"apiKey@{baseUrl}"] = HasApiKey(apiKey) ? apiKey : "no-key";
        }

        var options = new JsonSerializerOptions { WriteIndented = true };
        return (settings.ToJsonString(options), secrets.ToJsonString(options));
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
    private const string OpenCodeNineRouterProviderKey = "tunnel-agent-9router";

    private static JsonObject BuildOpenCodeProvidersBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, ModelsDevService.ModelInfo> modelInfoMap)
    {
        var cliproxy    = entries.Where(m => !IsPerplexityEntry(m) && !IsNineRouterEntry(m)).ToList();
        var perplexity  = entries.Where(IsPerplexityEntry).ToList();
        var nineRouter  = entries.Where(IsNineRouterEntry).ToList();
        var providers   = new JsonObject();
        if (cliproxy.Count > 0)
            providers[OpenCodeCliProxyProviderKey]   = BuildOpenCodeProviderBlock(cliproxy, modelInfoMap);
        if (perplexity.Count > 0)
            providers[OpenCodePerplexityProviderKey] = BuildOpenCodeProviderBlock(perplexity, modelInfoMap);
        if (nineRouter.Count > 0)
            providers[OpenCodeNineRouterProviderKey] = BuildOpenCodeProviderBlock(nineRouter, modelInfoMap);
        return providers;
    }

    private static JsonObject BuildOpenCodeProviderBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, ModelsDevService.ModelInfo> modelInfoMap)
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
            providers.Remove(OpenCodeNineRouterProviderKey);
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

    private const string CliProxyProviderKey = "tunnel-agent-cliproxy";
    private const string CliProxyAnthropicProviderKey = "tunnel-agent-cliproxy-anthropic";
    private const string PiCliProxyProviderKey = CliProxyProviderKey;
    private const string PiCliProxyAnthropicProviderKey = CliProxyAnthropicProviderKey;
    private const string PiPerplexityProviderKey = "tunnel-agent-perplexity";
    private const string PiNineRouterProviderKey = "tunnel-agent-9router";

    private static bool IsPerplexityEntry(ModelEntry m) =>
        string.Equals(m.ApiKey, PerplexityAccountCatalogService.EnvVarName, StringComparison.Ordinal) ||
        (!string.IsNullOrEmpty(m.EngineBaseUrl) && m.EngineBaseUrl.Contains(":8327", StringComparison.Ordinal));

    private static bool IsNineRouterEntry(ModelEntry m) =>
        string.Equals(m.ApiKey, NineRouterClientKeyService.EnvVarName, StringComparison.Ordinal) ||
        (!string.IsNullOrEmpty(m.EngineBaseUrl) && m.EngineBaseUrl.Contains(":20128", StringComparison.Ordinal));

    private static bool IsAnthropicEntry(ModelEntry m) =>
        !string.IsNullOrEmpty(m.OwnedBy) && m.OwnedBy.Equals("anthropic", StringComparison.OrdinalIgnoreCase);

    private static async Task<Dictionary<string, ModelsDevService.ModelInfo>> BuildModelInfoMapAsync(
        IEnumerable<string> modelIds, CancellationToken ct)
    {
        var map = new Dictionary<string, ModelsDevService.ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in modelIds)
        {
            var info = await ModelsDevService.Instance.GetModelInfoAsync(id, ct).ConfigureAwait(false);
            if (info is not null) map[id] = info;
        }
        return map;
    }

    private static JsonObject BuildPiProvidersBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, ModelsDevService.ModelInfo> modelInfoMap)
    {
        var cliproxy   = entries.Where(m => !IsAnthropicEntry(m) && !IsPerplexityEntry(m) && !IsNineRouterEntry(m)).ToList();
        var cliproxyAnthropic = entries.Where(m => IsAnthropicEntry(m) && !IsPerplexityEntry(m) && !IsNineRouterEntry(m)).ToList();
        var perplexity = entries.Where(IsPerplexityEntry).ToList();
        var nineRouter = entries.Where(IsNineRouterEntry).ToList();
        var providers  = new JsonObject();
        if (cliproxy.Count > 0)
            providers[PiCliProxyProviderKey]   = BuildPiProviderBlock(cliproxy, modelInfoMap, "openai-completions");
        if (cliproxyAnthropic.Count > 0)
            providers[PiCliProxyAnthropicProviderKey] = BuildPiProviderBlock(cliproxyAnthropic, modelInfoMap, "anthropic-messages");
        if (perplexity.Count > 0)
            providers[PiPerplexityProviderKey] = BuildPiProviderBlock(perplexity, modelInfoMap);
        if (nineRouter.Count > 0)
            providers[PiNineRouterProviderKey] = BuildPiProviderBlock(nineRouter, modelInfoMap);
        return providers;
    }

    private static JsonObject BuildPiProviderBlock(
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, ModelsDevService.ModelInfo> modelInfoMap,
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

    internal static string MergeOmpModelsYaml(
        string existing,
        IReadOnlyList<ModelEntry> entries,
        Dictionary<string, ModelsDevService.ModelInfo>? modelInfoMap,
        bool remove)
    {
        var stream = new YamlStream();
        try
        {
            if (!string.IsNullOrWhiteSpace(existing))
            {
                using var reader = new StringReader(existing);
                stream.Load(reader);
            }
        }
        catch (YamlException ex)
        {
            throw new InvalidDataException("OMP models.yml is not valid YAML.", ex);
        }

        if (stream.Documents.Count > 1)
            throw new InvalidDataException("OMP models.yml must contain a single YAML document.");

        YamlMappingNode root;
        if (stream.Documents.Count == 0)
        {
            root = new YamlMappingNode();
            stream.Add(new YamlDocument(root));
        }
        else if (stream.Documents[0].RootNode is YamlMappingNode mapping)
        {
            root = mapping;
        }
        else
        {
            throw new InvalidDataException("OMP models.yml root must be a mapping.");
        }

        var providersKey = new YamlScalarNode("providers");
        YamlMappingNode providers;
        if (root.Children.TryGetValue(providersKey, out var providersNode))
        {
            providers = providersNode as YamlMappingNode
                ?? throw new InvalidDataException("OMP models.yml providers must be a mapping.");
        }
        else
        {
            providers = new YamlMappingNode();
            root.Add(providersKey, providers);
        }

        providers.Children.Remove(new YamlScalarNode(CliProxyProviderKey));
        providers.Children.Remove(new YamlScalarNode(CliProxyAnthropicProviderKey));
        providers.Children.Remove(new YamlScalarNode(PiNineRouterProviderKey));

        if (!remove)
        {
            var metadata = modelInfoMap ?? new Dictionary<string, ModelsDevService.ModelInfo>(StringComparer.OrdinalIgnoreCase);
            var openAi = entries.Where(m => !IsAnthropicEntry(m) && !IsPerplexityEntry(m) && !IsNineRouterEntry(m)).ToList();
            var anthropic = entries.Where(m => IsAnthropicEntry(m) && !IsPerplexityEntry(m) && !IsNineRouterEntry(m)).ToList();
            var nineRouter = entries.Where(IsNineRouterEntry).ToList();
            if (openAi.Count > 0)
                providers.Add(CliProxyProviderKey, BuildOmpProviderBlock(openAi, metadata, "openai-completions"));
            if (anthropic.Count > 0)
                providers.Add(CliProxyAnthropicProviderKey, BuildOmpProviderBlock(anthropic, metadata, "anthropic-messages"));
            if (nineRouter.Count > 0)
                providers.Add(PiNineRouterProviderKey, BuildOmpProviderBlock(nineRouter, metadata, "openai-completions"));
        }

        if (providers.Children.Count == 0)
            root.Children.Remove(providersKey);

        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);
        var content = writer.ToString().TrimEnd();
        if (content.EndsWith("\n...", StringComparison.Ordinal))
            content = content[..^4].TrimEnd();
        return content == "{}" ? string.Empty : content + Environment.NewLine;
    }

    private static YamlMappingNode BuildOmpProviderBlock(
        IReadOnlyList<ModelEntry> entries,
        IReadOnlyDictionary<string, ModelsDevService.ModelInfo> modelInfoMap,
        string api)
    {
        var first = entries[0];
        var baseUrl = first.EngineBaseUrl;
        if (api == "anthropic-messages")
            baseUrl = StripV1(baseUrl);

        var provider = new YamlMappingNode
        {
            { "baseUrl", baseUrl },
            { "apiKey", HasResolvedApiKey(first.ApiKey) ? first.ApiKey : "no-key" },
            { "api", api }
        };
        if (api == "openai-completions")
            provider.Add("authHeader", "true");
        else
            provider.Add("disableStrictTools", "true");

        var models = new YamlSequenceNode();
        foreach (var model in entries)
        {
            var node = new YamlMappingNode { { "id", model.Id } };
            if (!string.IsNullOrWhiteSpace(model.DisplayName))
                node.Add("name", model.DisplayName);
            if (modelInfoMap.TryGetValue(model.Id, out var info))
            {
                if (info.SupportsReasoning)
                    node.Add("reasoning", "true");
                node.Add("input", new YamlSequenceNode(info.SupportsImage ? new[] { "text", "image" } : new[] { "text" }));
                if (info.ContextLength > 0)
                    node.Add("contextWindow", info.ContextLength.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            models.Add(node);
        }
        provider.Add("models", models);
        return provider;
    }

    private static async Task<AgentConfigApplyResult> ApplyOmpAsync(
        bool remove,
        IReadOnlyList<ModelEntry>? modelEntries,
        CancellationToken ct)
    {
        var configPath = ExpandPath("~/.omp/agent/models.yml");
        var directory = Path.GetDirectoryName(configPath)!;
        Directory.CreateDirectory(directory);

        var existing = File.Exists(configPath)
            ? await File.ReadAllTextAsync(configPath, ct).ConfigureAwait(false)
            : string.Empty;
        var metadata = remove || modelEntries is not { Count: > 0 }
            ? new Dictionary<string, ModelsDevService.ModelInfo>(StringComparer.OrdinalIgnoreCase)
            : await BuildModelInfoMapAsync(modelEntries.Select(m => m.Id), ct).ConfigureAwait(false);
        var content = MergeOmpModelsYaml(
            existing,
            modelEntries ?? Array.Empty<ModelEntry>(),
            metadata,
            remove);

        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(configPath, backupPath, overwrite: false);
        }

        await File.WriteAllTextAsync(configPath, content, Utf8NoBom, ct).ConfigureAwait(false);
        var modelCount = modelEntries?.Count ?? 0;
        var message = remove
            ? "Removed Tunnel Agent providers from Oh My Pi models.yml."
            : $"Configuration written to {configPath}.{(modelCount > 0 ? $" {modelCount} model(s) registered." : "")} Restart Oh My Pi for changes to take effect.";
        return AgentConfigApplyResult.Ok(message, configPath, backupPath);
    }

    private static RawConfigPreview OmpRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models)
    {
        var configPath = ExpandPath("~/.omp/agent/models.yml");
        var entries = (models ?? Array.Empty<string>())
            .Select(model => new ModelEntry(model, string.Empty, proxyBaseUrl, apiKey, model))
            .ToArray();
        var content = MergeOmpModelsYaml(string.Empty, entries, modelInfoMap: null, remove: false);
        return new RawConfigPreview("models.yml", configPath, content);
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
            providers.Remove(PiNineRouterProviderKey);
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

        var content = MergeFactoryDroidSettings(
            File.Exists(configPath) ? File.ReadAllText(configPath) : string.Empty,
            proxyBaseUrl, apiKey, remove, models);

        string? backupPath = null;
        if (File.Exists(configPath))
        {
            backupPath = $"{configPath}.backup.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            File.Copy(configPath, backupPath, overwrite: false);
        }

        File.WriteAllText(configPath, content, Utf8NoBom);

        var modelCount = models?.Count ?? 0;
        var msg = remove
            ? "Removed proxy models from Factory Droid config."
            : $"Configuration written to {configPath}. {(modelCount > 0 ? $"{modelCount} model(s) registered. " : "")}Restart Factory Droid for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, configPath, backupPath);
    }

    internal static string MergeFactoryDroidSettings(
        string existingContent, string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<ModelEntry>? models)
    {
        var root = string.IsNullOrWhiteSpace(existingContent)
            ? new JsonObject()
            : JsonNode.Parse(existingContent)?.AsObject() ?? new JsonObject();
        if (root["customModels"] is not JsonArray customModels)
        {
            customModels = new JsonArray();
            root["customModels"] = customModels;
        }

        for (int i = customModels.Count - 1; i >= 0; i--)
        {
            var model = customModels[i];
            var url = model?["baseUrl"]?.GetValue<string>() ?? "";
            var displayName = model?["displayName"]?.GetValue<string>() ?? "";
            var key = model?["apiKey"]?.GetValue<string>() ?? "";
            var managed = displayName.Contains("(Tunnel Agent", StringComparison.Ordinal) ||
                          key.Contains("TUNNEL_AGENT_", StringComparison.Ordinal);
            if (remove ? managed : string.Equals(url.TrimEnd('/'), proxyBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                customModels.RemoveAt(i);
        }

        if (!remove)
        {
            var entries = models?.Count > 0
                ? models
                : (IEnumerable<ModelEntry>)new[] { new ModelEntry("tunnel-agent", "", "", "TUNNEL_AGENT_CLIPROXY_API_KEY") };
            foreach (var model in entries)
                customModels.Add(BuildFactoryDroidEntry(model, proxyBaseUrl, apiKey));
        }

        if (customModels.Count == 0)
            root.Remove("customModels");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
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
        Dictionary<string, ModelsDevService.ModelInfo>? modelInfoMap,
        string apiKey, string proxyBaseUrl, bool remove, IReadOnlyCollection<int>? managedPorts = null)
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

        var content = MergeGrokConfig(existing, entries, modelInfoMap, apiKey, proxyBaseUrl, remove, managedPorts);
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
        Dictionary<string, ModelsDevService.ModelInfo>? modelInfoMap,
        string apiKey, string proxyBaseUrl, bool remove, IReadOnlyCollection<int>? managedPorts = null)
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
                if (isReplacing || IsManagedGrokModel(s.Lines, managedPorts))
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
        Dictionary<string, ModelsDevService.ModelInfo>? modelInfoMap,
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
        // (resolved from models.dev), mirroring Pi's reasoning flag. We only
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

    private static bool IsManagedGrokModel(IEnumerable<string> lines, IReadOnlyCollection<int>? managedPorts)
    {
        var name = ExtractTomlString(lines, "name");
        if (name != null && name.Contains("(Tunnel Agent", StringComparison.OrdinalIgnoreCase))
            return true;

        var baseUrl = ExtractTomlString(lines, "base_url");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Host is not ("127.0.0.1" or "localhost"))
            return false;

        return uri.Port is 8317 or 8327 or 20128 || managedPorts?.Contains(uri.Port) == true;
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
