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

public sealed record ModelEntry(string Id, string OwnedBy);

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

    /// <summary>Apply proxy configuration. Backs up existing files before writing.</summary>
    public async Task<AgentConfigApplyResult> ApplyAsync(AgentDefinition agent, string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models = null, IReadOnlyList<ModelEntry>? modelEntries = null, CancellationToken ct = default)
    {
        try
        {
            if (agent.Id == "pi")
                return await ApplyPiAsync(proxyBaseUrl, apiKey, remove: false, models, ct).ConfigureAwait(false);
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
            "gemini-cli"     => new[] { EnvExportRaw("gemini-cli", GeminiEnv(proxyBaseUrl, apiKey)) },
            "amp"            => AmpRaw(proxyBaseUrl, apiKey),
            "opencode"       => new[] { OpenCodeRaw(proxyBaseUrl, apiKey, models) },
            "pi"             => new[] { PiRaw(proxyBaseUrl, apiKey, models) },
            "factory-droid"  => new[] { FactoryDroidRaw(proxyBaseUrl, apiKey, modelEntries) },
            "cursor-agent"   => new[] { EnvExportRaw("cursor-agent", CursorEnv(proxyBaseUrl, apiKey)) },
            "aider"          => new[] { EnvExportRaw("aider", AiderEnv(proxyBaseUrl, apiKey)) },
            _                => Array.Empty<RawConfigPreview>()
        };

    // ── Apply / revert ───────────────────────────────────────────────────────

    private static AgentConfigApplyResult WriteConfigSync(
        AgentDefinition agent, string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<string>? models, IReadOnlyList<ModelEntry>? modelEntries)
    {
        try
        {
            return agent.Id switch
            {
                "claude-code"  => ApplyClaudeCode(proxyBaseUrl, apiKey, remove),
                "codex"        => ApplyCodex(proxyBaseUrl, apiKey, remove),
                "gemini-cli"   => AgentConfigApplyResult.Ok(
                    "Gemini CLI uses environment variables. Copy the shell export and add it to your shell profile.",
                    raw: new[] { EnvExportRaw("gemini-cli", GeminiEnv(proxyBaseUrl, apiKey)) }),
                "amp"          => ApplyAmp(proxyBaseUrl, apiKey, remove),
                "opencode"     => ApplyOpenCode(proxyBaseUrl, apiKey, remove, models),
                "factory-droid"=> ApplyFactoryDroid(proxyBaseUrl, apiKey, remove, modelEntries),
                "cursor-agent" => AgentConfigApplyResult.Ok(
                    "Cursor Agent uses environment variables. Copy the shell export and add it to your shell profile.",
                    raw: new[] { EnvExportRaw("cursor-agent", CursorEnv(proxyBaseUrl, apiKey)) }),
                "aider"        => AgentConfigApplyResult.Ok(
                    "Aider uses environment variables. Copy the shell export and add it to your shell profile.",
                    raw: new[] { EnvExportRaw("aider", AiderEnv(proxyBaseUrl, apiKey)) }),
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
            if (env.Count == 0) root.Remove("env");
        }
        else
        {
            env["ANTHROPIC_BASE_URL"] = proxyBaseUrl;
            if (HasApiKey(apiKey)) env["ANTHROPIC_AUTH_TOKEN"] = apiKey;
            else env.Remove("ANTHROPIC_AUTH_TOKEN");
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
        var env = new JsonObject { ["ANTHROPIC_BASE_URL"] = proxyBaseUrl };
        if (HasApiKey(apiKey)) env["ANTHROPIC_AUTH_TOKEN"] = apiKey;
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
        }
        else
        {
            var block = BuildCodexManagedBlock(proxyBaseUrl, apiKey);
            File.WriteAllText(configPath, stripped.Trim() + "\n\n" + block + "\n", Utf8NoBom);

            if (HasApiKey(apiKey))
            {
                // auth.json stores the API key
                var authJson = new JsonObject { ["OPENAI_API_KEY"] = apiKey }
                    .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(authPath, authJson, Utf8NoBom);
            }
        }

        var instructions = remove
            ? $"Removed proxy configuration from {configPath}."
            : $"Written {configPath} and {authPath}. Restart Codex CLI for changes to take effect.";

        return AgentConfigApplyResult.Ok(instructions, configPath, backupPath);
    }

    private RawConfigPreview[] CodexRaw(string proxyBaseUrl, string apiKey)
    {
        var configPath = ExpandPath("~/.codex/config.toml");
        var authPath   = ExpandPath("~/.codex/auth.json");
        var previews = new List<RawConfigPreview>
        {
            new("config.toml", configPath, BuildCodexManagedBlock(proxyBaseUrl, apiKey))
        };
        if (HasApiKey(apiKey))
            previews.Add(new RawConfigPreview("auth.json", authPath, $$"""
{
  "OPENAI_API_KEY": "{{apiKey}}"
}
"""));
        return previews.ToArray();
    }

    private string BuildCodexManagedBlock(string proxyBaseUrl, string apiKey)
    {
        var lines = new[]
        {
            ManagedBanner,
            "[inference]",
            "model_provider = \"cliproxyapi\"",
            string.Empty,
            "[model_providers.cliproxyapi]",
            "name = \"CLIProxyAPI (Tunnel Agent)\"",
            $"base_url = \"{EscapeToml(proxyBaseUrl)}\"",
            HasApiKey(apiKey) ? "api" + $"_key = \"{EscapeToml(apiKey)}\"" : null,
            ManagedEnd,
        };
        return string.Join(Environment.NewLine, lines.Where(l => l is not null));
    }

    // ── Gemini CLI ────────────────────────────────────────────────────────────

    private static string[] GeminiEnv(string proxyBaseUrl, string apiKey) =>
        HasApiKey(apiKey)
            ? ["CODE_ASSIST_ENDPOINT" + "=" + proxyBaseUrl, "GEMINI" + "_API_KEY" + "=" + apiKey]
            : ["CODE_ASSIST_ENDPOINT" + "=" + proxyBaseUrl];

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
            if (HasApiKey(apiKey)) secrets[$"apiKey@{baseUrl}"] = apiKey;
            else secrets.Remove($"apiKey@{baseUrl}");
            File.WriteAllText(secretsPath,
                secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
        }

        File.WriteAllText(settingsPath,
            settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var msg = remove
            ? "Removed proxy config from Amp CLI settings."
            : $"Written {settingsPath} and {secretsPath}. Restart Amp CLI for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, settingsPath, backupPath);
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
        if (HasApiKey(apiKey))
            previews.Add(new RawConfigPreview("secrets.json", secretsPath, $$"""
{
  "apiKey@{{baseUrl}}": "{{apiKey}}"
}
"""));
        return previews.ToArray();
    }

    // ── OpenCode ──────────────────────────────────────────────────────────────

    private static AgentConfigApplyResult ApplyOpenCode(string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<string>? models)
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
            providers.Remove("tunnel-agent");
            if (providers.Count == 0) root.Remove("provider");
        }
        else
        {
            providers["tunnel-agent"] = BuildOpenCodeProvider(proxyBaseUrl, apiKey, models);
        }

        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var modelCount = models?.Count ?? 0;
        var msg = remove
            ? "Removed Tunnel Agent provider. OpenCode will use its default providers."
            : $"Configuration written to {configPath}. {(modelCount > 0 ? $"{modelCount} model(s) registered. " : "")}Restart OpenCode for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, configPath, backupPath);
    }

    private static JsonObject BuildOpenCodeProvider(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models)
    {
        var options = new JsonObject
        {
            ["baseURL"]     = proxyBaseUrl,
            ["litellmProxy"] = true
        };
        options["apiKey"] = HasApiKey(apiKey) ? apiKey : "no-key";
        var provider = new JsonObject
        {
            ["name"] = "Tunnel Agent",
            ["npm"]  = "@ai-sdk/openai",
            ["options"] = options
        };

        if (models is { Count: > 0 })
        {
            var modelsObj = new JsonObject();
            foreach (var id in models)
            {
                var display = string.Join(" ",
                    id.Split('-', '_').Select(w => w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
                modelsObj[id] = new JsonObject { ["name"] = display };
            }
            provider["models"] = modelsObj;
        }

        return provider;
    }

    private static RawConfigPreview OpenCodeRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models)
    {
        var configPath = ExpandPath("~/.config/opencode/opencode.json");
        var provider   = BuildOpenCodeProvider(proxyBaseUrl, apiKey, models);
        var preview    = new JsonObject
        {
            ["$schema"]  = "https://opencode.ai/config.json",
            ["provider"] = new JsonObject { ["tunnel-agent"] = provider }
        };
        return new RawConfigPreview("opencode.json", configPath,
            preview.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── Pi ──────────────────────────────────────────────────────────────

    private static JsonObject BuildPiProviderBlock(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models, Dictionary<string, int>? contextMap = null)
    {
        var provider = new JsonObject
        {
            ["baseUrl"] = proxyBaseUrl,
            ["api"]     = "openai-completions"
        };
        provider["apiKey"] = HasApiKey(apiKey) ? apiKey : "no-key";
        if (models is { Count: > 0 })
        {
            provider["models"] = new JsonArray(
                models.Select(id =>
                {
                    var entry = new JsonObject { ["id"] = id };
                    if (contextMap is not null && contextMap.TryGetValue(id, out var ctx))
                        entry["contextWindow"] = ctx;
                    return (JsonNode?)entry;
                }).ToArray());
        }
        return provider;
    }

    private static async Task<AgentConfigApplyResult> ApplyPiAsync(string proxyBaseUrl, string apiKey, bool remove, IReadOnlyList<string>? models, CancellationToken ct)
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
            providers.Remove("tunnel-agent");
            if (providers.Count == 0) root.Remove("providers");
        }
        else
        {
            Dictionary<string, int>? contextMap = null;
            if (models is { Count: > 0 })
            {
                contextMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in models)
                {
                    var ctx = await OpenRouterContextService.Instance.GetContextLengthAsync(id, ct).ConfigureAwait(false);
                    if (ctx.HasValue) contextMap[id] = ctx.Value;
                }
            }
            providers["tunnel-agent"] = BuildPiProviderBlock(proxyBaseUrl, apiKey, models, contextMap);
        }

        File.WriteAllText(configPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);

        var modelCount = models?.Count ?? 0;
        var msg = remove
            ? "Removed tunnel-agent provider from Pi models.json."
            : $"Configuration written to {configPath}.{(modelCount > 0 ? $" {modelCount} model(s) registered." : "")} Restart Pi for changes to take effect.";
        return AgentConfigApplyResult.Ok(msg, configPath, backupPath);
    }

    private static RawConfigPreview PiRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<string>? models)
    {
        var configPath = ExpandPath("~/.pi/agent/models.json");
        var preview    = new JsonObject
        {
            ["providers"] = new JsonObject { ["tunnel-agent"] = BuildPiProviderBlock(proxyBaseUrl, apiKey, models) }
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
                : (IEnumerable<ModelEntry>)new[] { new ModelEntry("tunnel-agent", "") };
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
        var owner = model.OwnedBy.ToLowerInvariant();
        if (owner == "anthropic")
            return ("anthropic", StripV1(proxyBaseUrl));
        if (owner == "openai")
            return ("openai", proxyBaseUrl);
        return ("generic-chat-completion-api", proxyBaseUrl);
    }

    private static JsonObject BuildFactoryDroidEntry(ModelEntry model, string proxyBaseUrl, string apiKey)
    {
        var (provider, baseUrl) = InferFactoryDroidProvider(model, proxyBaseUrl);
        var entry = new JsonObject
        {
            ["model"]       = model.Id,
            ["displayName"] = model.Id,
            ["baseUrl"]     = baseUrl,
            ["provider"]    = provider
        };
        entry["apiKey"] = HasApiKey(apiKey) ? apiKey : "no-key";
        return entry;
    }

    private static RawConfigPreview FactoryDroidRaw(string proxyBaseUrl, string apiKey, IReadOnlyList<ModelEntry>? models)
    {
        var configPath = ExpandPath("~/.factory/settings.json");
        var modelEntries = models?.Count > 0 ? models : (IEnumerable<ModelEntry>)new[] { new ModelEntry("tunnel-agent", "") };
        var entries    = new JsonArray(modelEntries
            .Select(m => (JsonNode?)BuildFactoryDroidEntry(m, proxyBaseUrl, apiKey))
            .ToArray());
        var content    = new JsonObject { ["customModels"] = entries }
            .ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        return new RawConfigPreview("config.json", configPath, content);
    }

    // ── Env-var agents (Cursor / Aider) ──────────────────────────────────────

    private static string[] CursorEnv(string proxyBaseUrl, string apiKey) =>
        HasApiKey(apiKey)
            ? ["ANTHROPIC_BASE_URL" + "=" + proxyBaseUrl, "ANTHROPIC_AUTH_TOKEN" + "=" + apiKey]
            : ["ANTHROPIC_BASE_URL" + "=" + proxyBaseUrl];

    private static string[] AiderEnv(string proxyBaseUrl, string apiKey) =>
        HasApiKey(apiKey)
            ? ["OPENAI_API_BASE" + "=" + proxyBaseUrl, "OPENAI_API_KEY" + "=" + apiKey]
            : ["OPENAI_API_BASE" + "=" + proxyBaseUrl];

    private static RawConfigPreview EnvExportRaw(string agentId, string[] vars)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Add to your shell profile (~/.zshrc or ~/.bashrc):");
        foreach (var v in vars)
            sb.AppendLine($"export {v}");
        return new RawConfigPreview($"{agentId}-env.sh", "Shell profile", sb.ToString().TrimEnd());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool HasApiKey(string apiKey) => !string.IsNullOrWhiteSpace(apiKey);

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
