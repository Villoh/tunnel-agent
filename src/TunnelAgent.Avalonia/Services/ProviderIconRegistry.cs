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

    private static readonly string AntigravityIconData =
        "M21.751 22.607c1.34 1.005 3.35.335 1.508-1.508C17.73 15.74 18.904 1 12.037 1 5.17 1 6.342 15.74.815 21.1c-2.01 2.009.167 2.511 1.507 1.506 5.192-3.517 4.857-9.714 9.715-9.714 4.857 0 4.522 6.197 9.714 9.715z";

    private static readonly string CursorIconData =
        "M22.106 5.68L12.5.135a.998.998 0 00-.998 0L1.893 5.68a.84.84 0 00-.419.726v11.186c0 .3.16.577.42.727l9.607 5.547a.999.999 0 00.998 0l9.608-5.547a.84.84 0 00.42-.727V6.407a.84.84 0 00-.42-.726zm-.603 1.176L12.228 22.92c-.063.108-.228.064-.228-.061V12.34a.59.59 0 00-.295-.51l-9.11-5.26c-.107-.062-.063-.228.062-.228h18.55c.264 0 .428.286.296.514z";

    private static readonly string KiroIconData =
        "M7.97 16.376c-1.644 3.642 1.86 4.556 4.443 2.424.76 2.39 3.608.607 4.631-1.247 2.251-4.084 1.342-8.249 1.108-9.108-1.6-5.859-9.6-5.869-10.976.03-.323 1.033-.328 2.206-.507 3.423-.09.617-.16 1.009-.393 1.655-.139.373-.323.7-.62 1.257-.458.865-.264 2.53 2.101 1.665l.224-.1h-.01l-.001.001z";

    private static readonly string XaiIconData =
        "M6.469 8.776L16.512 23h-4.464L2.005 8.776H6.47z" +
        "M6.465 16.676l2.233 3.164L6.467 23H2l4.465-6.324z" +
        "M22 2.582V23h-3.659V7.764L22 2.582z" +
        "M22 1l-9.952 14.095-2.233-3.163L17.533 1H22z";

    private static readonly string TraeIconData =
        "M24 20.541H3.428v-3.426H0V3.4h24V20.54z" +
        "M3.428 17.115h17.144V6.827H3.428v10.288z" +
        "m8.573-5.196l-2.425 2.424-2.424-2.424 2.424-2.424 2.425 2.424z" +
        "m6.857-.001l-2.424 2.423-2.425-2.423 2.425-2.425 2.424 2.425z";

    public record ProviderIcon(
        PackIconSimpleIconsKind IconKind,
        string LogoColor,
        string? CustomIconData = null,
        bool IsFallback = false);

    /// <summary>
    /// How a provider's icon should be rendered. Known providers use their brand glyph;
    /// unknown/custom providers (arbitrary user-chosen names) fall back to a monogram
    /// (first letter of the name) over a stable, name-derived accent colour.
    /// </summary>
    public sealed record ProviderIconDisplay(
        PackIconSimpleIconsKind IconKind,
        string LogoColor,
        string? CustomIconData,
        bool UseMonogram,
        string Monogram)
    {
        public bool HasCustomIcon  => CustomIconData is not null;
        public bool ShowSimpleIcon => !UseMonogram && !HasCustomIcon;
    }

    // Accent palette for unknown/custom providers. A name always maps to the same entry.
    private static readonly string[] FallbackPalette =
    {
        "#6366F1", "#8B5CF6", "#A855F7", "#EC4899", "#F43F5E",
        "#EF4444", "#F59E0B", "#10B981", "#14B8A6", "#0EA5E9",
        "#3B82F6", "#84CC16",
    };

    /// <summary>True when the provider id maps to a known brand icon (not the generic fallback).</summary>
    public static bool IsKnown(string providerId) => !Get(providerId).IsFallback;

    /// <summary>Single-character monogram derived from a provider's display name.</summary>
    public static string Monogram(string name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            foreach (var ch in name)
                if (char.IsLetterOrDigit(ch))
                    return char.ToUpperInvariant(ch).ToString();
        return "?";
    }

    /// <summary>Stable accent colour derived from a name (FNV-1a hash into <see cref="FallbackPalette"/>).</summary>
    public static string FallbackColor(string name)
    {
        if (string.IsNullOrEmpty(name)) return FallbackPalette[0];
        uint hash = 2166136261;
        foreach (var ch in name) { hash ^= ch; hash *= 16777619; }
        return FallbackPalette[hash % (uint)FallbackPalette.Length];
    }

    /// <summary>
    /// Resolves the full icon presentation for a provider, applying the monogram fallback
    /// (with a name-derived colour) when the provider id is not a known brand.
    /// </summary>
    public static ProviderIconDisplay GetDisplay(string providerId, string? displayName = null)
    {
        var icon = Get(providerId);
        if (!icon.IsFallback)
            return new(icon.IconKind, icon.LogoColor, icon.CustomIconData, UseMonogram: false, Monogram: "");

        var name = string.IsNullOrWhiteSpace(displayName) ? providerId : displayName!;
        return new(icon.IconKind, FallbackColor(name), CustomIconData: null, UseMonogram: true, Monogram(name));
    }

    /// <summary>Lookup by provider ID or owned_by value (case-insensitive).</summary>
    public static ProviderIcon Get(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "claude"                        => new(PackIconSimpleIconsKind.Claude,       "#D97757"),
            "anthropic"                     => new(PackIconSimpleIconsKind.Anthropic,    "#D97757"),
            "perplexity"                     => new(PackIconSimpleIconsKind.Perplexity,   "#1FB8CD"),
            "codex"  or "openai"            => new(PackIconSimpleIconsKind.OpenAi,       "#23262E"),
            "gemini-cli" or "google"        => new(PackIconSimpleIconsKind.GoogleGemini, "#4285F4"),
            "antigravity"                   => new(PackIconSimpleIconsKind.OpenAi,       "#7C3AED", AntigravityIconData),
            "alibaba"                       => new(PackIconSimpleIconsKind.AlibabaCloud, "#FF6A00"),
            "kimi"  or "moonshot"           => new(PackIconSimpleIconsKind.OpenAi,       "#000000", KimiIconData),
            "cursor"                        => new(PackIconSimpleIconsKind.OpenAi,        "#1D9BF0", CursorIconData),
            "kiro"                          => new(PackIconSimpleIconsKind.OpenAi,       "#9046FF", KiroIconData),
            "trae"                          => new(PackIconSimpleIconsKind.OpenAi,       "#32F08C", TraeIconData),
            "xai"  or "grok"               => new(PackIconSimpleIconsKind.OpenAi,       "#000000", XaiIconData),
            _                               => new(PackIconSimpleIconsKind.OpenAi,       "#555555", IsFallback: true),
        };
}
