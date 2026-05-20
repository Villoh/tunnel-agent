using System.Text.Json.Nodes;
using TunnelAgent.Services;
using Xunit;

using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class OAuthTokenDetectorTests
{
    [Fact]
    public void GetAccounts_ParsesEmailAndPlanFromJsonFields()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("codex-user@example.com-plus.json"), new JsonObject
        {
            ["access_token"] = "token",
            ["email"] = "json@example.com",
            ["plan"] = "pro"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var account = Assert.Single(detector.GetAccounts()["codex"]);

        Assert.Equal("codex", account.ProviderId);
        Assert.Equal("json@example.com", account.Email);
        Assert.Equal("PRO", account.Plan);
        Assert.False(account.IsDisabled);
    }

    [Fact]
    public void GetAccounts_WhenJsonFieldsMissing_ParsesEmailAndPlanFromFilename()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("codex-user@example.com-plus.json"), new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var account = Assert.Single(detector.GetAccounts()["codex"]);

        Assert.Equal("user@example.com", account.Email);
        Assert.Equal("PLUS", account.Plan);
    }

    [Fact]
    public void GetAccounts_IgnoresOpenAiCompatAndInvalidFiles()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("openai-compat-codex-user.json"), new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        File.WriteAllText(temp.File("codex-empty.json"), new JsonObject().ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        Assert.Empty(detector.GetAccounts());
    }

    [Fact]
    public void SetDisabled_MatchingAccount_PatchesDisabledField()
    {
        using var temp = new TestTempDirectory();
        var file = temp.File("codex-user@example.com-plus.json");
        File.WriteAllText(file, new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        detector.SetDisabled("codex", "user@example.com", true);

        var account = Assert.Single(detector.GetAccounts()["codex"]);
        Assert.True(account.IsDisabled);
        Assert.Empty(detector.GetConnectedProviderIds());
    }

    [Fact]
    public void GetConnectedProviderIds_OnlyReturnsProvidersWithActiveAccounts()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("codex-user@example.com-plus.json"), new JsonObject
        {
            ["access_token"] = "token",
            ["disabled"] = true
        }.ToJsonString());
        File.WriteAllText(temp.File("claude-me@example.com-pro.json"), new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var connected = detector.GetConnectedProviderIds();

        Assert.Contains("claude", connected);
        Assert.DoesNotContain("codex", connected);
    }
}
