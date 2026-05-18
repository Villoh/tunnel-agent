using System.Globalization;
using Avalonia.Media;
using TunnelAgent.Converters;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class ConverterEdgeTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Fact]
    public void InvertBoolConverter_ConvertBack_Inverts()
    {
        var converter = InvertBoolConverter.Instance;

        Assert.False((bool)converter.ConvertBack(true, typeof(bool), null, Culture)!);
        Assert.True((bool)converter.ConvertBack(false, typeof(bool), null, Culture)!);
    }

    [Fact]
    public void ServerStateToBrushConverter_Convert_Running_ReturnsExpectedColor()
    {
        var converter = new ServerStateToBrushConverter();
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(TunnelAgent.ViewModels.ServerState.Running, typeof(IBrush), null, Culture));
        // Light theme color (no Application.Current in tests)
        Assert.Equal(Color.Parse("#1F8A5B"), brush.Color);
    }

    [Fact]
    public void ServerStateToBrushConverter_Convert_Stopped_ReturnsExpectedColor()
    {
        var converter = new ServerStateToBrushConverter();
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(TunnelAgent.ViewModels.ServerState.Stopped, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#C84B36"), brush.Color);
    }

    [Fact]
    public void ServerStateToBrushConverter_Convert_Starting_ReturnsExpectedColor()
    {
        var converter = new ServerStateToBrushConverter();
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(TunnelAgent.ViewModels.ServerState.Starting, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#C98A0B"), brush.Color);
    }

    [Fact]
    public void ServerStateToBrushConverter_Convert_Error_ReturnsSameAsStopped()
    {
        var converter = new ServerStateToBrushConverter();
        var brush = Assert.IsType<SolidColorBrush>(
            converter.Convert(TunnelAgent.ViewModels.ServerState.Error, typeof(IBrush), null, Culture));
        Assert.Equal(Color.Parse("#C84B36"), brush.Color);
    }
}
