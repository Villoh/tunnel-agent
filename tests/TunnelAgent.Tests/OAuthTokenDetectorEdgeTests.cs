using System.Text.Json.Nodes;
using TunnelAgent.Services;
using Xunit;

using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class OAuthTokenDetectorEdgeTests
{
    [Fact]
    public void GetAccounts_EmptyDirectory_ReturnsEmpty()
    {
        using var temp = new TestTempDirectory();
        var detector = new OAuthTokenDetector(temp.Path);

        var accounts = detector.GetAccounts();

        Assert.Empty(accounts);
    }

    [Fact]
    public void GetAccounts_EmailWithoutPlanSuffix_ExtractsEmailOnly()
    {
        using var temp = new TestTempDirectory();
        // Filename: codex-user@example.com.json (no plan suffix)
        File.WriteAllText(temp.File("codex-user@example.com.json"), new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var accounts = detector.GetAccounts();

        var account = Assert.Single(accounts["codex"]);
        Assert.Equal("user@example.com", account.Email);
        Assert.Equal("", account.Plan);
    }

    [Fact]
    public void GetAccounts_EmailWithMultipleDashes_ExtractsCorrectly()
    {
        using var temp = new TestTempDirectory();
        // Filename: codex-name-with-dashes@example.com-pro.json
        File.WriteAllText(temp.File("codex-name-with-dashes@example.com-pro.json"), new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var accounts = detector.GetAccounts();

        var account = Assert.Single(accounts["codex"]);
        Assert.Equal("name-with-dashes@example.com", account.Email);
        Assert.Equal("Pro", account.Plan);
    }

    [Fact]
    public void GetAccounts_PlanInJsonOverridesFilenamePlan()
    {
        using var temp = new TestTempDirectory();
        // Filename says "pro" but JSON says "plus"
        File.WriteAllText(temp.File("codex-user@example.com-pro.json"), new JsonObject
        {
            ["access_token"] = "token",
            ["plan"] = "plus"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var account = Assert.Single(detector.GetAccounts()["codex"]);
        Assert.Equal("Plus", account.Plan);
    }

    [Fact]
    public void GetAccounts_DisabledFieldInJson_ParsesCorrectly()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("claude-me@example.com-pro.json"), new JsonObject
        {
            ["access_token"] = "token",
            ["disabled"] = true
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var account = Assert.Single(detector.GetAccounts()["claude"]);
        Assert.True(account.IsDisabled);
    }

    [Fact]
    public void GetAccounts_FileWithoutAuthFields_ReturnsNull()
    {
        using var temp = new TestTempDirectory();
        // Empty JSON object - no auth fields
        File.WriteAllText(temp.File("codex-empty.json"), new JsonObject().ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        Assert.Empty(detector.GetAccounts());
    }

    [Fact]
    public void SetDisabled_NonExistentProvider_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var detector = new OAuthTokenDetector(temp.Path);

        detector.SetDisabled("nonexistent", "email@test.com", true);
        // Should not throw
    }

    [Fact]
    public void SetDisabled_NonExistentEmail_DoesNothing()
    {
        using var temp = new TestTempDirectory();
        var detector = new OAuthTokenDetector(temp.Path);

        detector.SetDisabled("claude", "nonexistent@test.com", true);

        Assert.Empty(detector.GetAccounts());
    }

    [Fact]
    public void GetConnectedProviderIds_MultipleProviders_OnlyReturnsActive()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("codex-active@test.com-pro.json"), new JsonObject
        {
            ["access_token"] = "token"
        }.ToJsonString());
        File.WriteAllText(temp.File("claude-disabled@test.com-pro.json"), new JsonObject
        {
            ["access_token"] = "token",
            ["disabled"] = true
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        var connected = detector.GetConnectedProviderIds();

        Assert.Contains("codex", connected);
        Assert.DoesNotContain("claude", connected);
    }

    [Fact]
    public void GetAccounts_FileWithApiKeyField_CountsAsValid()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("codex-user@example.com-pro.json"), new JsonObject
        {
            ["api_key"] = "sk-test"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        Assert.Single(detector.GetAccounts());
    }

    [Fact]
    public void GetAccounts_FileWithCredentialsField_CountsAsValid()
    {
        using var temp = new TestTempDirectory();
        File.WriteAllText(temp.File("codex-user@example.com-pro.json"), new JsonObject
        {
            ["credentials"] = "some-value"
        }.ToJsonString());
        var detector = new OAuthTokenDetector(temp.Path);

        Assert.Single(detector.GetAccounts());
    }
}
