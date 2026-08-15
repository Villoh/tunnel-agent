using System.Linq;
using TunnelAgent.Infrastructure.Services;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

[Collection("UserEnvironment")]
public sealed class AgentConfigurationServiceTests : IDisposable
{
    private const string Proxy = "http://127.0.0.1:8317/v1";
    private const string NineRouterProxy = "http://127.0.0.1:20128/v1";

    private readonly InMemoryUserEnvironmentService _env = new();
    private readonly IUserEnvironmentService _previousEnv;

    public AgentConfigurationServiceTests()
    {
        _previousEnv = UserEnvironmentService.SetImplementation(_env);
        _env.Set(NineRouterClientKeyService.EnvVarName, "9router-key");
    }

    public void Dispose() =>
        UserEnvironmentService.SetImplementation(_previousEnv);

    private static ModelEntry Model(string id, string display) =>
        new(id, "", Proxy, "", display);

    [Fact]
    public void MergeGrokConfig_FreshFile_WritesDocumentedTomlModelEntries()
    {
        var content = AgentConfigurationService.MergeGrokConfig(
            existing: "",
            entries: new[] { Model("xai/grok-code-fast-1", "Grok Code Fast (Tunnel Agent)") },
            modelInfoMap: null, apiKey: "secret", proxyBaseUrl: Proxy, remove: false);

        Assert.Contains("[models]", content);
        Assert.Contains("default = \"xai/grok-code-fast-1\"", content);
        Assert.Contains("[model.\"xai/grok-code-fast-1\"]", content);
        Assert.Contains("base_url = \"http://127.0.0.1:8317/v1\"", content);
        Assert.Contains("api_backend = \"chat_completions\"", content);
    }

    [Fact]
    public void MergeGrokConfig_AnthropicModel_UsesMessagesBackendKeepingV1()
    {
        var content = AgentConfigurationService.MergeGrokConfig(
            existing: "",
            entries: new[] { new ModelEntry("claude-opus-4-8", "anthropic", Proxy, "", "Claude Opus 4.8 (Tunnel Agent)") },
            modelInfoMap: null, apiKey: "secret", proxyBaseUrl: Proxy, remove: false);

        Assert.Contains("api_backend = \"messages\"", content);
        Assert.DoesNotContain("chat_completions", content);
        // base_url keeps /v1 so Grok resolves {base}/v1/messages.
        Assert.Contains("base_url = \"http://127.0.0.1:8317/v1\"", content);
    }

    [Fact]
    public void MergeGrokConfig_ExistingConfig_DoesNotDuplicateModelsTable()
    {
        // Reproduces the reported bug: a prior Tunnel Agent model plus the user's
        // own [ui] table, then a second reconfigure with different models.
        var existing = """
[models]
default = "claude-opus-4-8"
default_reasoning_effort = "medium"

[model.claude-opus-4-8]
model = "claude-opus-4-8"
name = "Claude Opus 4.8 (Tunnel Agent)"
base_url = "http://127.0.0.1:8317/v1"
api_key = "no-key"
api_backend = "chat_completions"
supports_reasoning_effort = true

[ui]
max_thoughts_width = 120
yolo = false
""";

        var content = AgentConfigurationService.MergeGrokConfig(
            existing,
            entries: new[] { Model("gpt-5.5", "GPT-5.5 (Tunnel Agent)") },
            modelInfoMap: null, apiKey: "secret", proxyBaseUrl: Proxy, remove: false);

        // Exactly one [models] table and one [model.*] table survive.
        Assert.Equal(1, CountOccurrences(content, "[models]"));
        Assert.Equal(1, content.Split('\n').Count(l => l.TrimStart().StartsWith("[model.")));

        // Old managed model is gone; the new one and the default are set.
        Assert.DoesNotContain("claude-opus-4-8", content);
        Assert.Contains("[model.\"gpt-5.5\"]", content);
        Assert.Contains("default = \"gpt-5.5\"", content);

        // User-owned content is preserved.
        Assert.Contains("[ui]", content);
        Assert.Contains("default_reasoning_effort = \"medium\"", content);
    }

    [Fact]
    public void MergeGrokConfig_Remove_KeepsUserTablesAndDropsManagedModels()
    {
        var existing = """
[models]
default = "gpt-5.5"

[model."gpt-5.5"]
model = "gpt-5.5"
name = "GPT-5.5 (Tunnel Agent)"
base_url = "http://127.0.0.1:8317/v1"
api_backend = "chat_completions"

[ui]
yolo = true
""";

        var content = AgentConfigurationService.MergeGrokConfig(
            existing, entries: System.Array.Empty<ModelEntry>(),
            modelInfoMap: null, apiKey: "", proxyBaseUrl: Proxy, remove: true);

        Assert.DoesNotContain("[model.", content);
        Assert.DoesNotContain("gpt-5.5", content);   // default referencing removed model dropped
        Assert.Contains("[ui]", content);
    }

    [Fact]
    public void MergeGrokConfig_Remove_KeepsUnmanagedLocalhostModel()
    {
        var existing = """
[models]
default = "local-model"

[model."local-model"]
model = "local-model"
name = "My local model"
base_url = "http://localhost:11434/v1"
api_backend = "chat_completions"
""";

        var content = AgentConfigurationService.MergeGrokConfig(
            existing, entries: System.Array.Empty<ModelEntry>(),
            modelInfoMap: null, apiKey: "", proxyBaseUrl: "", remove: true,
            managedPorts: new[] { 9000, 9001 });

        Assert.Contains("[model.\"local-model\"]", content);
        Assert.Contains("default = \"local-model\"", content);
    }

    [Fact]
    public void MergeGrokConfig_Remove_DropsLegacyModelOnManagedPort()
    {
        var existing = """
[model."legacy-model"]
model = "legacy-model"
name = "Legacy model"
base_url = "http://127.0.0.1:9000/v1"
api_backend = "chat_completions"
""";

        var content = AgentConfigurationService.MergeGrokConfig(
            existing, entries: System.Array.Empty<ModelEntry>(),
            modelInfoMap: null, apiKey: "", proxyBaseUrl: "", remove: true,
            managedPorts: new[] { 9000, 9001 });

        Assert.DoesNotContain("legacy-model", content);
    }

    [Fact]
    public void AgentCatalog_ContainsOhMyPiDefinition()
    {
        var omp = Assert.Single(AgentCatalog.All, agent => agent.Id == "omp");

        Assert.Equal("Oh My Pi (OMP)", omp.DisplayName);
        Assert.Contains("omp", omp.BinaryNames);
        Assert.Contains("omp.cmd", omp.BinaryNames);
        Assert.Contains("omp.exe", omp.BinaryNames);
        Assert.Contains("~/.omp/agent/models.yml", omp.ConfigPaths);
    }

    [Fact]
    public async System.Threading.Tasks.Task PreviewAsync_Omp_ReturnsModelsYaml()
    {
        var agent = new AgentDefinition(
            "omp", "Oh My Pi (OMP)", "", new[] { "omp" },
            new[] { "~/.omp/agent/models.yml" }, "https://omp.sh", "#FF7A00");
        var service = new AgentConfigurationService();

        var previews = await service.PreviewAsync(
            agent, Proxy, "", modelEntries: new[] { Model("gpt-5.6-sol", "GPT-5.6 Sol") });

        var preview = Assert.Single(previews);
        Assert.Equal("models.yml", preview.Filename);
        Assert.EndsWith(System.IO.Path.Combine(".omp", "agent", "models.yml"), preview.TargetPath);
        Assert.Contains("tunnel-agent-cliproxy:", preview.Content);
    }

    [Fact]
    public void MergeOmpModelsYaml_FreshFile_WritesOpenAiAndAnthropicProviders()
    {
        var entries = new[]
        {
            Model("gpt-5.6-sol", "GPT-5.6 Sol"),
            new ModelEntry("claude-sonnet-5", "anthropic", Proxy, "", "Claude Sonnet 5")
        };
        var metadata = new System.Collections.Generic.Dictionary<string, ModelsDevService.ModelInfo>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.6-sol"] = new(1_050_000, true, true),
            ["claude-sonnet-5"] = new(1_000_000, true, true)
        };

        var content = AgentConfigurationService.MergeOmpModelsYaml("", entries, metadata, remove: false);

        Assert.Contains("tunnel-agent-cliproxy:", content);
        Assert.Contains("baseUrl: http://127.0.0.1:8317/v1", content);
        Assert.Contains("api: openai-completions", content);
        Assert.Contains("authHeader: true", content);
        Assert.Contains("tunnel-agent-cliproxy-anthropic:", content);
        Assert.Contains("baseUrl: http://127.0.0.1:8317", content);
        Assert.Contains("api: anthropic-messages", content);
        Assert.Contains("disableStrictTools: true", content);
        Assert.Contains("contextWindow: 1050000", content);
        Assert.Contains("reasoning: true", content);
        Assert.Contains("- image", content);
    }

    [Fact]
    public void MergeOmpModelsYaml_ExistingConfig_PreservesUserDataAndReplacesManagedProviders()
    {
        var existing = """
theme: dark
providers:
  custom:
    baseUrl: https://example.test/v1
  tunnel-agent-cliproxy:
    baseUrl: http://old.test/v1
    models:
      - id: old-model
""";

        var content = AgentConfigurationService.MergeOmpModelsYaml(
            existing,
            new[] { Model("gpt-5.6-sol", "GPT-5.6 Sol") },
            modelInfoMap: null,
            remove: false);

        Assert.Contains("theme: dark", content);
        Assert.Contains("custom:", content);
        Assert.Contains("https://example.test/v1", content);
        Assert.DoesNotContain("old-model", content);
        Assert.Equal(1, CountOccurrences(content, "tunnel-agent-cliproxy:"));
    }

    [Fact]
    public void MergeOmpModelsYaml_Remove_DropsOnlyManagedProviders()
    {
        var existing = """
providers:
  custom:
    baseUrl: https://example.test/v1
  tunnel-agent-cliproxy:
    models:
      - id: gpt-5.6-sol
  tunnel-agent-cliproxy-anthropic:
    models:
      - id: claude-sonnet-5
""";

        var content = AgentConfigurationService.MergeOmpModelsYaml(
            existing, System.Array.Empty<ModelEntry>(), modelInfoMap: null, remove: true);

        Assert.Contains("custom:", content);
        Assert.DoesNotContain("tunnel-agent-cliproxy:", content);
        Assert.DoesNotContain("tunnel-agent-cliproxy-anthropic:", content);
        Assert.DoesNotContain("...", content);
    }

    [Fact]
    public void MergeOmpModelsYaml_RemoveLastProviders_ReturnsEmptyFile()
    {
        var existing = """
providers:
  tunnel-agent-cliproxy:
    models:
      - id: gpt-5.6-sol
""";

        var content = AgentConfigurationService.MergeOmpModelsYaml(
            existing, System.Array.Empty<ModelEntry>(), modelInfoMap: null, remove: true);

        Assert.Equal(string.Empty, content);
    }

    [Fact]
    public void MergeAmpConfig_Remove_DropsManagedUrlAndMatchingSecretOnly()
    {
        var settings = """
{
  "amp.url": "http://127.0.0.1:8317",
  "theme": "dark"
}
""";
        var secrets = """
{
  "apiKey@http://127.0.0.1:8317": "managed-key",
  "apiKey@https://api.example.com": "user-key"
}
""";

        var result = AgentConfigurationService.MergeAmpConfig(
            settings, secrets, baseUrl: "", apiKey: "", remove: true);

        Assert.DoesNotContain("amp.url", result.Settings);
        Assert.Contains("\"theme\": \"dark\"", result.Settings);
        Assert.DoesNotContain("managed-key", result.Secrets);
        Assert.Contains("user-key", result.Secrets);
    }

    [Fact]
    public void MergeAmpConfig_RemoveWithoutConfiguredUrl_KeepsSecrets()
    {
        var result = AgentConfigurationService.MergeAmpConfig(
            "{ \"theme\": \"dark\" }",
            "{ \"apiKey@https://api.example.com\": \"user-key\" }",
            baseUrl: "", apiKey: "", remove: true);

        Assert.Contains("user-key", result.Secrets);
    }

    [Fact]
    public void MergeFactoryDroidSettings_Remove_DropsManagedModelsAndKeepsUserSettings()
    {
        var existing = """
{
  "theme": "dark",
  "customModels": [
    {
      "model": "gpt-5.6-sol",
      "displayName": "GPT-5.6 Sol (Tunnel Agent)",
      "baseUrl": "http://127.0.0.1:8317/v1",
      "apiKey": "${TUNNEL_AGENT_CLIPROXY_API_KEY}"
    },
    {
      "model": "local-model",
      "displayName": "Local model",
      "baseUrl": "http://localhost:1234/v1",
      "apiKey": "local-key"
    }
  ]
}
""";

        var content = AgentConfigurationService.MergeFactoryDroidSettings(
            existing, proxyBaseUrl: "", apiKey: "", remove: true, models: null);

        Assert.DoesNotContain("gpt-5.6-sol", content);
        Assert.Contains("local-model", content);
        Assert.Contains("\"theme\": \"dark\"", content);
    }

    [Fact]
    public void MergeFactoryDroidSettings_RemoveLastModel_DropsCustomModelsProperty()
    {
        var existing = """
{
  "customModels": [
    {
      "model": "claude-sonnet",
      "displayName": "Claude Sonnet (Tunnel Agent - Perplexity)",
      "baseUrl": "http://127.0.0.1:8318/v1",
      "apiKey": "${PERPLEXITY_API_KEY}"
    }
  ]
}
""";

        var content = AgentConfigurationService.MergeFactoryDroidSettings(
            existing, proxyBaseUrl: "", apiKey: "", remove: true, models: null);

        Assert.Equal("{}", content);
    }

    [Fact]
    public async System.Threading.Tasks.Task PreviewAsync_OpenCode_WritesNineRouterProviderAndEnvVar()
    {
        var agent = Assert.Single(AgentCatalog.All, a => a.Id == "opencode");
        var service = new AgentConfigurationService();
        var entries = new[]
        {
            new ModelEntry(
                "gpt-4o",
                "openai",
                NineRouterProxy,
                NineRouterClientKeyService.EnvVarName,
                "GPT-4o (Tunnel Agent - 9Router)")
        };

        var preview = Assert.Single(await service.PreviewAsync(agent, Proxy, "", modelEntries: entries));

        Assert.Equal("opencode.json", preview.Filename);
        Assert.Contains("\"tunnel-agent-9router\"", preview.Content);
        Assert.Contains("\"baseURL\": \"http://127.0.0.1:20128/v1\"", preview.Content);
        Assert.Contains($"\"apiKey\": \"{{env:{NineRouterClientKeyService.EnvVarName}}}\"", preview.Content);
        Assert.DoesNotContain("tunnel-agent-cliproxy", preview.Content);
    }

    [Fact]
    public async System.Threading.Tasks.Task PreviewAsync_Pi_WritesNineRouterProviderAndEnvVar()
    {
        var agent = Assert.Single(AgentCatalog.All, a => a.Id == "pi");
        var service = new AgentConfigurationService();
        var entries = new[]
        {
            new ModelEntry(
                "claude-sonnet-4",
                "anthropic",
                NineRouterProxy,
                NineRouterClientKeyService.EnvVarName,
                "Claude Sonnet 4 (Tunnel Agent - 9Router)")
        };

        var preview = Assert.Single(await service.PreviewAsync(agent, Proxy, "", modelEntries: entries));

        Assert.Equal("models.json", preview.Filename);
        Assert.Contains("\"tunnel-agent-9router\"", preview.Content);
        Assert.Contains("\"baseUrl\": \"http://127.0.0.1:20128/v1\"", preview.Content);
        Assert.Contains($"\"apiKey\": \"${{{NineRouterClientKeyService.EnvVarName}}}\"", preview.Content);
        Assert.DoesNotContain("tunnel-agent-cliproxy-anthropic", preview.Content);
    }

    [Fact]
    public void MergeOmpModelsYaml_NineRouterModels_WritesNineRouterProvider()
    {
        var entries = new[]
        {
            new ModelEntry(
                "gpt-4o",
                "openai",
                NineRouterProxy,
                NineRouterClientKeyService.EnvVarName,
                "GPT-4o (Tunnel Agent - 9Router)")
        };

        var content = AgentConfigurationService.MergeOmpModelsYaml("", entries, modelInfoMap: null, remove: false);

        Assert.Contains("tunnel-agent-9router:", content);
        Assert.Contains("baseUrl: http://127.0.0.1:20128/v1", content);
        Assert.Contains(NineRouterClientKeyService.EnvVarName, content);
        Assert.DoesNotContain("tunnel-agent-cliproxy:", content);
    }

    [Theory]
    [InlineData("providers: [")]
    [InlineData("- item")]
    public void MergeOmpModelsYaml_InvalidDocument_Throws(string existing)
    {
        Assert.Throws<System.IO.InvalidDataException>(() =>
            AgentConfigurationService.MergeOmpModelsYaml(
                existing, System.Array.Empty<ModelEntry>(), modelInfoMap: null, remove: true));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    private sealed class InMemoryUserEnvironmentService : IUserEnvironmentService
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;
        public void Set(string name, string value) => _values[name] = value;
        public void Remove(string name) => _values.Remove(name);
    }
}
