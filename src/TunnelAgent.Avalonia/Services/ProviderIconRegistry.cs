using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.Services;

/// <summary>
/// Centralised icon/colour metadata for known providers.
/// Used by both ProviderCatalogService and ModelFetchService.
/// </summary>
public static class ProviderIconRegistry
{
    private static readonly string KimiIconData =
        "M21.846 0a1.923 1.923 0 110 3.846H20.15a.226.226 0 01-.227-.226V1.923C19.923.861 20.784 0 21.846 0z " +
        "M11.065 11.199l7.257-7.2c.137-.136.06-.41-.116-.41H14.3a.164.164 0 00-.117.051l-7.82 7.756c-.122.12-.302.013-.302-.179V3.82c0-.127-.083-.23-.185-.23H3.186c-.103 0-.186.103-.186.23V19.77c0 .128.083.23.186.23h2.69c.103 0 .186-.102.186-.23v-3.25c0-.069.025-.135.069-.178l2.424-2.406a.158.158 0 01.205-.023l6.484 4.772a7.677 7.677 0 003.453 1.283c.108.012.2-.095.2-.23v-3.06c0-.117-.07-.212-.164-.227a5.028 5.028 0 01-2.027-.807l-5.613-4.064c-.117-.078-.132-.279-.028-.381z";

    public record ProviderIcon(
        PackIconSimpleIconsKind IconKind,
        string LogoColor,
        string? CustomIconData = null);

    /// <summary>Lookup by provider ID or owned_by value (case-insensitive).</summary>
    public static ProviderIcon Get(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "claude"                        => new(PackIconSimpleIconsKind.Claude,       "#D97757"),
            "anthropic"                     => new(PackIconSimpleIconsKind.Anthropic,    "#D97757"),
            "perplexity"                     => new(PackIconSimpleIconsKind.Perplexity,   "#1FB8CD"),
            "codex"  or "openai"            => new(PackIconSimpleIconsKind.OpenAi,       "#23262E"),
            "gemini-cli" or "google"        => new(PackIconSimpleIconsKind.GoogleGemini, "#4285F4"),
            "github-copilot"                => new(PackIconSimpleIconsKind.GitHub,       "#24292E"),
            "antigravity"                   => new(PackIconSimpleIconsKind.OpenAi,       "#7C3AED"),
            "alibaba"                       => new(PackIconSimpleIconsKind.AlibabaCloud, "#FF6A00"),
            "kimi"  or "moonshot"           => new(PackIconSimpleIconsKind.OpenAi,       "#000000", KimiIconData),
            _                               => new(PackIconSimpleIconsKind.OpenAi,       "#555555"),
        };
}
