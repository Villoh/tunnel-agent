using IconPacks.Avalonia.SimpleIcons;
using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class ProviderIconRegistryTests
{
    [Theory]
    [InlineData("claude", PackIconSimpleIconsKind.Claude, "#D97757")]
    [InlineData("anthropic", PackIconSimpleIconsKind.Anthropic, "#D97757")]
    [InlineData("codex", PackIconSimpleIconsKind.OpenAi, "#23262E")]
    [InlineData("openai", PackIconSimpleIconsKind.OpenAi, "#23262E")]
    [InlineData("gemini-cli", PackIconSimpleIconsKind.GoogleGemini, "#4285F4")]
    [InlineData("google", PackIconSimpleIconsKind.GoogleGemini, "#4285F4")]
    [InlineData("github-copilot", PackIconSimpleIconsKind.GitHub, "#24292E")]
    [InlineData("antigravity", PackIconSimpleIconsKind.OpenAi, "#7C3AED")]
    [InlineData("alibaba", PackIconSimpleIconsKind.AlibabaCloud, "#FF6A00")]
    [InlineData("moonshot", PackIconSimpleIconsKind.OpenAi, "#000000")]
    [InlineData("kiro", PackIconSimpleIconsKind.OpenAi, "#9046FF")]
    [InlineData("trae", PackIconSimpleIconsKind.OpenAi, "#32F08C")]
    public void KnownProviders_ReturnExpectedIconAndColor(string providerId, PackIconSimpleIconsKind expectedIcon, string expectedColor)
    {
        var icon = ProviderIconRegistry.Get(providerId);

        Assert.Equal(expectedIcon, icon.IconKind);
        Assert.Equal(expectedColor, icon.LogoColor);
    }

    [Theory]
    [InlineData("kimi")]
    [InlineData("moonshot")]
    [InlineData("antigravity")]
    [InlineData("kiro")]
    [InlineData("trae")]
    public void ProvidersWithSvgPaths_HaveCustomIconData(string providerId)
    {
        var icon = ProviderIconRegistry.Get(providerId);

        Assert.NotNull(icon.CustomIconData);
        Assert.NotEmpty(icon.CustomIconData);
    }

    [Theory]
    [InlineData("unknown-provider")]
    [InlineData("local-ai")]
    [InlineData("")]
    [InlineData("random-string")]
    public void UnknownProviders_ReturnDefaultIcon(string providerId)
    {
        var icon = ProviderIconRegistry.Get(providerId);

        Assert.Equal(PackIconSimpleIconsKind.OpenAi, icon.IconKind);
        Assert.Equal("#555555", icon.LogoColor);
        Assert.Null(icon.CustomIconData);
    }

    [Fact]
    public void CaseInsensitivity_MatchesCorrectly()
    {
        Assert.Equal("#D97757", ProviderIconRegistry.Get("CLAUDE").LogoColor);
        Assert.Equal("#D97757", ProviderIconRegistry.Get("Claude").LogoColor);
        Assert.Equal("#23262E", ProviderIconRegistry.Get("OPENAI").LogoColor);
        Assert.Equal("#4285F4", ProviderIconRegistry.Get("GoOgLe").LogoColor);
    }
}
