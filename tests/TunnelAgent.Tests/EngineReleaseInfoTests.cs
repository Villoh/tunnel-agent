using TunnelAgent.Services;
using Xunit;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Tests;

public sealed class EngineReleaseInfoTests
{
    [Fact]
    public void Constructor_MapsAllProperties()
    {
        var publishedAt = new DateTimeOffset(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);
        var info = new EngineReleaseInfo("v1.0.0", "Version 1.0", true, publishedAt);

        Assert.Equal("v1.0.0", info.TagName);
        Assert.Equal("Version 1.0", info.DisplayName);
        Assert.True(info.IsPrerelease);
        Assert.Equal(publishedAt, info.PublishedAt);
    }

    [Fact]
    public void Default_Values_AreNullOrDefault()
    {
        var info = new EngineReleaseInfo("", "", false, null);

        Assert.Equal("", info.TagName);
        Assert.Equal("", info.DisplayName);
        Assert.False(info.IsPrerelease);
        Assert.Null(info.PublishedAt);
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new EngineReleaseInfo("v2.0.0", "Two", true, DateTimeOffset.UnixEpoch);
        var b = new EngineReleaseInfo("v2.0.0", "Two", true, DateTimeOffset.UnixEpoch);

        Assert.Equal(a, b);
        Assert.True(a == b);
    }

    [Fact]
    public void Equality_DifferentTagName_AreNotEqual()
    {
        var a = new EngineReleaseInfo("v1.0.0", "One", false, null);
        var b = new EngineReleaseInfo("v2.0.0", "One", false, null);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
    }

    [Fact]
    public void Equality_DifferentIsPrerelease_AreNotEqual()
    {
        var a = new EngineReleaseInfo("v1.0.0", "One", true, null);
        var b = new EngineReleaseInfo("v1.0.0", "One", false, null);

        Assert.NotEqual(a, b);
    }
}
