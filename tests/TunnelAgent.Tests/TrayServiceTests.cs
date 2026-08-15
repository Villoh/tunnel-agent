using Avalonia;
using TunnelAgent.Services;

namespace TunnelAgent.Tests;

public sealed class TrayServiceTests
{
    [Theory]
    [InlineData(1900, 16, 1448, 24)]
    [InlineData(1900, 1064, 1448, 516)]
    public void PositionNearCursor_AnchorsBelowTopAndAboveBottomTray(int cursorX, int cursorY, int expectedX, int expectedY)
    {
        var position = TrayService.PositionNearCursor(
            new PixelRect(0, 0, 1920, 1080),
            new PixelPoint(cursorX, cursorY),
            460,
            540,
            8);

        Assert.Equal(new PixelPoint(expectedX, expectedY), position);
    }
}
