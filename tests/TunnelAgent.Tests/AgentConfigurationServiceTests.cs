using System.Linq;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

public sealed class AgentConfigurationServiceTests
{
    private const string Proxy = "http://127.0.0.1:8317/v1";

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

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }
}
