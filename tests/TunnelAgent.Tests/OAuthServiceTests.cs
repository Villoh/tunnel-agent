using TunnelAgent.Services;

using TunnelAgent.Infrastructure.Engine.CliProxy;
namespace TunnelAgent.Tests;

public sealed class OAuthServiceTests
{
    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("kimi", true)]
    [InlineData("antigravity", true)]
    [InlineData("local-ai", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void IsOAuthProvider_RecognizesKnownProviders(string providerId, bool expected)
    {
        Assert.Equal(expected, OAuthService.IsOAuthProvider(providerId));
    }

    [Fact]
    public void ConnectAsync_KnownProvider_IsNotExercisedInUnitTests()
    {
        // ConnectAsync for a known provider starts the real CLIProxyAPI binary when it is installed,
        // which can launch a browser. Keep unit tests side-effect free and cover unsupported-provider
        // behavior plus provider recognition instead.
        Assert.True(OAuthService.IsOAuthProvider("claude"));
    }

    [Fact]
    public async Task ConnectAsync_UnknownProvider_ReturnsExplicitMessage()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        using var service = new OAuthService(config);

        var result = await service.ConnectAsync("local-ai");

        Assert.False(result.Success);
        Assert.Equal(OAuthConnectStatus.NotSupported, result.Status);
    }

    [Fact]
    public async Task Dispose_DoesNotThrow_WhenCalledMultipleTimes()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        var service = new OAuthService(config);

        service.Dispose();
        service.Dispose(); // second dispose should not throw
    }

    [Fact]
    public async Task CancelPreviousAuth_WithoutActiveAuth_DoesNotThrow()
    {
        using var temp = new TestTempDirectory();
        var settings = new SettingsService(temp.File("settings.json"));
        await settings.LoadAsync();
        var config = new ConfigService(settings, temp.File("proxy-config.yaml"), temp.File("auth"));
        var service = new OAuthService(config);

        service.CancelPreviousAuth(); // should not throw
        service.CancelPreviousAuth(); // multiple calls OK
    }
}
