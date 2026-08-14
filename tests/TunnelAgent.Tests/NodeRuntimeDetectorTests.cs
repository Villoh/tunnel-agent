using TunnelAgent.Infrastructure.Engine.NineRouter;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class NodeRuntimeDetectorTests
{
    [Fact]
    public void TryParseVersion_V18_20_4_ReturnsParsedVersion()
    {
        var parsed = NodeRuntimeDetector.TryParseVersion("v18.20.4", out var version);

        Assert.True(parsed);
        Assert.Equal(new Version(18, 20, 4), version);
        Assert.True(NodeRuntimeDetector.IsSupported(version));
    }

    [Fact]
    public void TryParseVersion_V22_1_0_ReturnsParsedVersion()
    {
        var parsed = NodeRuntimeDetector.TryParseVersion("v22.1.0", out var version);

        Assert.True(parsed);
        Assert.Equal(new Version(22, 1, 0), version);
        Assert.True(NodeRuntimeDetector.IsSupported(version));
    }

    [Fact]
    public void TryParseVersion_Unprefixed18_0_0_ReturnsParsedVersion()
    {
        var parsed = NodeRuntimeDetector.TryParseVersion("18.0.0", out var version);

        Assert.True(parsed);
        Assert.Equal(new Version(18, 0, 0), version);
        Assert.True(NodeRuntimeDetector.IsSupported(version));
    }

    [Fact]
    public void TryParseVersion_V16_20_0_ReturnsUnsupportedVersion()
    {
        var parsed = NodeRuntimeDetector.TryParseVersion("v16.20.0", out var version);

        Assert.True(parsed);
        Assert.Equal(new Version(16, 20, 0), version);
        Assert.False(NodeRuntimeDetector.IsSupported(version));
    }

    [Fact]
    public void TryParseVersion_JunkInput_ReturnsFalse()
    {
        var parsed = NodeRuntimeDetector.TryParseVersion("not-a-version", out var version);

        Assert.False(parsed);
        Assert.Equal(new Version(0, 0), version);
    }

    [Fact]
    public void IsSupported_Major18OrHigher_ReturnsTrue()
    {
        Assert.True(NodeRuntimeDetector.IsSupported(new Version(18, 0, 0)));
        Assert.True(NodeRuntimeDetector.IsSupported(new Version(22, 0, 0)));
    }

    [Fact]
    public void IsSupported_Major17_ReturnsFalse()
    {
        Assert.False(NodeRuntimeDetector.IsSupported(new Version(17, 9, 0)));
    }

    [Fact]
    public void Detect_InjectedSupportedCandidate_ReturnsExecutableAndVersion()
    {
        var detector = new NodeRuntimeDetector(
            ["C:\\tools\\node.exe"],
            _ => "v18.20.4");

        var runtime = detector.Detect();

        Assert.NotNull(runtime);
        Assert.Equal("C:\\tools\\node.exe", runtime.ExecutablePath);
        Assert.Equal(new Version(18, 20, 4), runtime.Version);
    }

    [Fact]
    public void Detect_UnsupportedThenSupported_ReturnsFirstSupported()
    {
        var detector = new NodeRuntimeDetector(
            ["old-node", "new-node"],
            path => path == "old-node" ? "v16.20.0" : "v22.1.0");

        var runtime = detector.Detect();

        Assert.NotNull(runtime);
        Assert.Equal("new-node", runtime.ExecutablePath);
        Assert.Equal(new Version(22, 1, 0), runtime.Version);
    }
}
