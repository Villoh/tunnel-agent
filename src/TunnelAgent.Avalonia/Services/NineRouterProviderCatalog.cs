using System;
using System.Collections.Generic;
using System.Linq;

namespace TunnelAgent.Services;

/// <summary>Specifies credential modes exposed by a 9Router provider.</summary>
[Flags]
public enum NineRouterAuthModes
{
    /// <summary>Specifies no credential mode.</summary>
    None = 0,
    /// <summary>Specifies an API-key credential.</summary>
    ApiKey = 1,
    /// <summary>Specifies an OAuth credential.</summary>
    OAuth = 2,
    /// <summary>Specifies a browser-session cookie credential.</summary>
    Cookie = 4,
    /// <summary>Specifies a provider that requires no user credential.</summary>
    NoAuth = 8,
}

/// <summary>Specifies how Tunnel Agent starts a 9Router OAuth provider.</summary>
public enum NineRouterOAuthFlow
{
    /// <summary>Specifies that the provider has no OAuth flow.</summary>
    None,
    /// <summary>Specifies an authorization-code flow completed by pasting the callback URL.</summary>
    Browser,
    /// <summary>Specifies an OAuth device-code flow.</summary>
    DeviceCode,
    /// <summary>Specifies a flow that 9Router completes through its local dashboard proxy.</summary>
    Dashboard,
}

/// <summary>Describes one credential option from 9Router's provider registry.</summary>
/// <param name="Id">9Router's provider identifier.</param>
/// <param name="Name">The provider display name.</param>
/// <param name="AuthModes">Credential modes declared by the provider registry.</param>
/// <param name="OAuthFlow">The OAuth flow implemented by 9Router, if any.</param>
public sealed record NineRouterProviderOption(
    string Id,
    string Name,
    NineRouterAuthModes AuthModes,
    NineRouterOAuthFlow OAuthFlow)
{
    /// <summary>Gets whether this provider accepts an API key.</summary>
    public bool SupportsApiKey => AuthModes.HasFlag(NineRouterAuthModes.ApiKey);

    /// <summary>Gets whether this provider accepts a browser cookie.</summary>
    public bool SupportsCookie => AuthModes.HasFlag(NineRouterAuthModes.Cookie);

    /// <summary>Gets whether this provider has OAuth support.</summary>
    public bool SupportsOAuth => AuthModes.HasFlag(NineRouterAuthModes.OAuth);

    /// <summary>Gets whether this provider requires no user credential.</summary>
    public bool SupportsNoAuth => AuthModes.HasFlag(NineRouterAuthModes.NoAuth);

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>Provides the provider and credential catalog shipped by 9Router 0.5.55.</summary>
/// <remarks>
/// The catalog is copied from 9Router's <c>open-sse/providers/registry</c> and
/// <c>src/lib/oauth/providers</c> at commit <c>699edac</c>. It is intentionally
/// static: the 9Router management API does not expose this registry.
/// </remarks>
public static class NineRouterProviderCatalog
{
    private static readonly NineRouterProviderOption[] Options =
    [
        new("antigravity", "Antigravity", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("claude", "Claude Code", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("cline", "Cline", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("clinepass", "ClinePass", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("codebuddy-intl", "CodeBuddy", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("codebuddy-cn", "CodeBuddy CN", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("cursor", "Cursor IDE", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("gemini-cli", "Gemini CLI", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("github", "GitHub Copilot", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("gitlab", "GitLab Duo", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("grok-cli", "Grok CLI (Grok Build)", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("iflow", "iFlow AI", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("kilocode", "Kilo Code", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("kimchi", "Kimchi", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Browser),
        new("kimi", "Kimi", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("kiro", "Kiro AI", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("codex", "OpenAI Codex", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("qoder", "Qoder", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.DeviceCode),
        new("trae", "Trae", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("windsurf", "Windsurf", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("xai", "xAI (Grok)", NineRouterAuthModes.ApiKey | NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("zed", "Zed", NineRouterAuthModes.OAuth, NineRouterOAuthFlow.Dashboard),
        new("alicode", "Alibaba", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("alicode-intl", "Alibaba Coding", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("alims-intl", "Alibaba Studio", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("alitp-intl", "Alibaba Token Plan", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("anthropic", "Anthropic", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("api-airforce", "API.airforce", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("assemblyai", "AssemblyAI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("aws-polly", "AWS Polly", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("azure", "Azure OpenAI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("baidu", "Baidu Qianfan", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("bazaarlink", "Bazaarlink", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("black-forest-labs", "Black Forest Labs", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("blackbox", "Blackbox AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("bluesminds", "BluesMinds", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("brave-search", "Brave Search", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("cartesia", "Cartesia", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("cerebras", "Cerebras", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("chutes", "Chutes AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("cloudflare-ai", "Cloudflare", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("cohere", "Cohere", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("comfyui", "ComfyUI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("commandcode", "Command Code", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("deepgram", "Deepgram", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("deepseek", "DeepSeek", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("elevenlabs", "ElevenLabs", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("exa", "Exa", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("fal-ai", "Fal.ai", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("featherless", "Featherless", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("firecrawl", "Firecrawl", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("fireworks", "Fireworks AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("fish-audio", "Fish Audio", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("gemini", "Gemini", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("glm-cn", "GLM (China)", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("glm", "GLM Coding", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("google-pse", "Google PSE", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("groq", "Groq", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("huggingface", "HuggingFace", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("hyperbolic", "Hyperbolic", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("inworld", "Inworld TTS", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("jina-ai", "Jina AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("jina-reader", "Jina Reader", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("kilo-gateway", "Kilo Gateway", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("linkup", "Linkup", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("llm7", "LLM7", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("minimax-cn", "Minimax (China)", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("minimax", "Minimax Coding", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("mistral", "Mistral", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("mmf", "MMF", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("morph", "Morph", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("nanobanana", "NanoBanana API", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("nebius", "Nebius AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("nvidia", "NVIDIA NIM", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("ollama", "Ollama Cloud", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("ollama-local", "Ollama Local", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("openai", "OpenAI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("opencode-go", "OpenCode Go", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("openrouter", "OpenRouter", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("perplexity", "Perplexity", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("perplexity-agent", "Perplexity Agent", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("playht", "PlayHT", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("poolside", "Poolside", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("recraft", "Recraft", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("runwayml", "Runway ML", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("sambanova", "SambaNova", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("sdwebui", "SD WebUI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("searchapi", "SearchAPI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("selfhosted-embedding", "Self-hosted Embedding", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("selfhosted-stt", "Self-hosted STT", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("selfhosted-tts", "Self-hosted TTS", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("serper", "Serper", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("siliconflow", "SiliconFlow", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("stability-ai", "Stability AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("tavily", "Tavily", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("tencent", "Tencent Hunyuan", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("together", "Together AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("tokenrouter", "TokenRouter", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("topaz", "Topaz", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("venice", "Venice AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("vercel-ai-gateway", "Vercel AI Gateway", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("vertex-partner", "Vertex Partner", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("volcengine-ark", "Volcengine Ark", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("voyage-ai", "Voyage AI", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("xiaomi-mimo", "Xiaomi MiMo", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("xiaomi-tokenplan", "Xiaomi MiMo (Token Plan)", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("youcom", "You.com Search", NineRouterAuthModes.ApiKey, NineRouterOAuthFlow.None),
        new("byteplus", "BytePlus ModelArk", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("coqui", "Coqui TTS", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("devin-cli", "Devin CLI", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("edge-tts", "Edge TTS", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("google-tts", "Google TTS", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("grok-web", "Grok Web (Subscription)", NineRouterAuthModes.Cookie, NineRouterOAuthFlow.None),
        new("local-device", "Local Device", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("mimo-free", "MiMo Code Free", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("opencode", "OpenCode Free", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("perplexity-web", "Perplexity Web (Pro/Max)", NineRouterAuthModes.Cookie, NineRouterOAuthFlow.None),
        new("searxng", "SearXNG", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("tortoise", "Tortoise TTS", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
        new("vertex", "Vertex AI", NineRouterAuthModes.NoAuth, NineRouterOAuthFlow.None),
    ];

    /// <summary>Gets every provider declared by the 9Router registry.</summary>
    public static IReadOnlyList<NineRouterProviderOption> All => Options;

    /// <summary>Finds a provider by its 9Router id.</summary>
    /// <param name="id">The 9Router provider identifier.</param>
    /// <returns>The matching provider, or <see langword="null"/> when unknown.</returns>
    public static NineRouterProviderOption? Find(string? id) =>
        Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));
}
