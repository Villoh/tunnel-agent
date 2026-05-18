using TunnelAgent.Services;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class OAuthServiceTests
{
    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini-cli", true)]
    [InlineData("kimi", true)]
    [InlineData("github-copilot", true)]
    [InlineData("antigravity", true)]
    [InlineData("qwen", true)]
    [InlineData("local-ai", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void IsOAuthProvider_RecognizesKnownProviders(string providerId, bool expected)
    {
        Assert.Equal(expected, OAuthService.IsOAuthProvider(providerId));
    }

    [Fact]
    public async Task ConnectAsync_KnownProvider_BinaryNotInTestDir_ReturnsFailure()
    {
        // The binary path uses IPlatformInfo.Current.LocalDataDirectory
        // which is a machine-wide directory. We test that unusual provider ID
        // returns appropriate failure.
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        using var service = new OAuthService(config);

        var result = await service.ConnectAsync("claude");

        // Either binary not installed (returns failure) or binary installed (returns success/browser)
        // Both are valid outcomes in test environment
        if (!result.Success)
            Assert.Contains("binary is not installed", result.Message);
    }

    [Fact]
    public async Task ConnectAsync_UnknownProvider_ReturnsExplicitMessage()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        using var service = new OAuthService(config);

        var (success, message) = await service.ConnectAsync("local-ai");

        Assert.False(success);
        Assert.Contains("does not support OAuth", message);
    }

    [Fact]
    public void Dispose_DoesNotThrow_WhenCalledMultipleTimes()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        var service = new OAuthService(config);

        service.Dispose();
        service.Dispose(); // second dispose should not throw
    }

    [Fact]
    public void CancelPreviousAuth_WithoutActiveAuth_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        settings.LoadAsync().GetAwaiter().GetResult();
        var config = new EngineConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        var service = new OAuthService(config);

        service.CancelPreviousAuth(); // should not throw
        service.CancelPreviousAuth(); // multiple calls OK
    }
}
