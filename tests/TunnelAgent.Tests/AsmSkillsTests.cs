using System.Text.Json;
using TunnelAgent.Core.Skills;
using TunnelAgent.Infrastructure.Skills;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

public sealed class AsmSkillsTests
{
    [Theory]
    [InlineData("v18.0.0", 18)]
    [InlineData("24.18.0", 24)]
    [InlineData("11.16.0\r\n", 11)]
    public void ParseVersion_HandlesCliOutput(string value, int major) =>
        Assert.Equal(major, AsmProvisionService.ParseVersion(value)!.Major);

    [Fact]
    public void ParseSearchFixture_MapsAvailableSkillSource()
    {
        var json = File.ReadAllText(Fixture("search-code-review.json"));
        var skills = AsmCliService.ParseJson<IReadOnlyList<SkillSummary>>(json);
        var skill = Assert.Single(skills, x => x.Name == "code-review" && x.Repo == "luongnv89/skills");
        Assert.Equal("github:luongnv89/skills:skills/code-review", skill.Source);
        Assert.False(skill.Installed);
    }

    [Fact]
    public void ParseListFixture_MapsInstalledSkill()
    {
        var json = File.ReadAllText(Fixture("list-installed-code-review.json"));
        var skill = Assert.Single(AsmCliService.ParseJson<IReadOnlyList<SkillSummary>>(json));
        Assert.True(skill.Installed);
        Assert.Equal("agents", skill.Provider);
        Assert.Equal("global", skill.Scope);
    }

    [Fact]
    public void ParseAuditFixture_MapsSecurityVerdict()
    {
        var json = File.ReadAllText(Fixture("audit-code-review.json"));
        var audit = AsmCliService.ParseJson<SkillAudit>(json);
        Assert.Equal("dangerous", audit.Verdict);
        Assert.NotEmpty(audit.CodeScans);
        Assert.NotEmpty(audit.Permissions);
    }

    [Fact]
    public void ParseInstallOutput_SkipsProgressPrefix()
    {
        var output = File.ReadAllText(Fixture("install-code-review.txt"));
        var result = AsmCliService.ParseJson<InstallResult>(output);
        Assert.True(result.Success);
        Assert.Equal("agents", result.Provider, ignoreCase: true);
    }

    [Fact]
    public void Settings_RoundTripSkillsBlock()
    {
        var settings = new AppSettings();
        settings.Skills.Scope = "project";
        settings.Skills.PreferredVersion = "2.14.0";
        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<AppSettings>(json)!;
        Assert.Equal("project", restored.Skills.Scope);
        Assert.True(restored.Skills.AutoCheckForUpdates);
    }

    private static string Fixture(string name) => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "plans", "asm-samples", name));
}
