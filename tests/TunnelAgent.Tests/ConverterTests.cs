using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using TunnelAgent.Converters;
using TunnelAgent.ViewModels;
using Xunit;

namespace TunnelAgent.Tests;

public sealed class ConverterTests
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(ServerState.Running, "Running")]
    [InlineData(ServerState.Stopped, "Stopped")]
    [InlineData(ServerState.Starting, "Starting…")]
    [InlineData(ServerState.Error, "Error")]
    public void ServerStateToTextConverter_Convert_ReturnsExpectedText(ServerState state, string expected)
    {
        var converter = new ServerStateToTextConverter();

        var result = converter.Convert(state, typeof(string), null, Culture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(ServerState.Running, true)]
    [InlineData(ServerState.Stopped, false)]
    public void ServerStateRunningConverter_Convert_RespectsInvertParameter(ServerState state, bool expectedRunning)
    {
        var converter = new ServerStateRunningConverter();

        Assert.Equal(expectedRunning, converter.Convert(state, typeof(bool), null, Culture));
        Assert.Equal(!expectedRunning, converter.Convert(state, typeof(bool), "Invert", Culture));
    }

    [Fact]
    public void HexToBrushConverter_ValidAndInvalidValues_ReturnBrushes()
    {
        var converter = new HexToBrushConverter();

        var brush = Assert.IsType<SolidColorBrush>(converter.Convert("#112233", typeof(IBrush), null, Culture));
        var transparent = Assert.IsAssignableFrom<ISolidColorBrush>(converter.Convert("bad", typeof(IBrush), null, Culture));

        Assert.Equal(Color.Parse("#112233"), brush.Color);
        Assert.Equal(Colors.Transparent, transparent.Color);
    }

    [Theory]
    [InlineData(SectionKey.Providers, "Providers", true)]
    [InlineData(SectionKey.Providers, "Agents", false)]
    [InlineData(SectionKey.Configuration, "Configuration", true)]
    public void SectionEqualsConverter_Convert_ParsesParameter(SectionKey section, string parameter, bool expected)
    {
        var converter = new SectionEqualsConverter();

        var result = converter.Convert(section, typeof(bool), parameter, Culture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(true, 56)]
    [InlineData(false, 176)]
    public void SidebarWidthConverter_Convert_ReturnsExpectedGridLength(bool collapsed, double expected)
    {
        var converter = new SidebarWidthConverter();

        var result = Assert.IsType<GridLength>(converter.Convert(collapsed, typeof(GridLength), null, Culture));

        Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData(true, "Disable")]
    [InlineData(false, "Enable")]
    public void BoolToEnableDisableTextConverter_Convert_ReturnsExpectedText(bool value, string expected)
    {
        Assert.Equal(expected, BoolToEnableDisableTextConverter.Instance.Convert(value, typeof(string), null, Culture));
    }

    [Theory]
    [InlineData(true, 1.0)]
    [InlineData(false, 0.4)]
    public void BoolToOpacityConverter_Convert_ReturnsExpectedOpacity(bool value, double expected)
    {
        Assert.Equal(expected, BoolToOpacityConverter.Instance.Convert(value, typeof(double), null, Culture));
    }

    [Theory]
    [InlineData("text", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void StringNotEmptyConverter_Convert_ReturnsExpectedVisibility(string? value, bool expected)
    {
        Assert.Equal(expected, StringNotEmptyConverter.Instance.Convert(value, typeof(bool), null, Culture));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void InvertBoolConverter_ConvertAndConvertBack_InvertBooleans(bool value, bool expected)
    {
        Assert.Equal(expected, InvertBoolConverter.Instance.Convert(value, typeof(bool), null, Culture));
        Assert.Equal(expected, InvertBoolConverter.Instance.ConvertBack(value, typeof(bool), null, Culture));
    }

    [Theory]
    [InlineData(EngineState.Downloading, null, "Downloading")]
    [InlineData(EngineState.Installing, null, "Installing")]
    [InlineData(EngineState.Running, null, "Running")]
    public void EngineStateToTextConverter_WithoutParameter_ReturnsStateName(EngineState state, string? parameter, string expected)
    {
        var converter = new EngineStateToTextConverter();

        var result = converter.Convert(state, typeof(string), parameter, Culture);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(EngineState.Downloading, true)]
    [InlineData(EngineState.Installing, true)]
    [InlineData(EngineState.Running, false)]
    public void EngineStateToTextConverter_IsDownloadingParameter_ReturnsBusyFlag(EngineState state, bool expected)
    {
        var converter = new EngineStateToTextConverter();

        var result = converter.Convert(state, typeof(bool), "IsDownloading", Culture);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void MultiBoolAndConverter_Convert_ReturnsTrueOnlyWhenAllValuesAreTrue()
    {
        Assert.True((bool)MultiBoolAndConverter.Instance.Convert([true, true], typeof(bool), null, Culture)!);
        Assert.False((bool)MultiBoolAndConverter.Instance.Convert([true, false], typeof(bool), null, Culture)!);
        Assert.False((bool)MultiBoolAndConverter.Instance.Convert([true, BindingOperations.DoNothing], typeof(bool), null, Culture)!);
    }

    [Fact]
    public void ConvertBack_Methods_ThrowWhenUnsupported()
    {
        Assert.Throws<NotImplementedException>(() => new ServerStateToTextConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => new ServerStateToBrushConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => new ServerStateRunningConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => new HexToBrushConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => new SectionEqualsConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => new SidebarWidthConverter().ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => BoolToEnableDisableTextConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => BoolToOpacityConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => StringNotEmptyConverter.Instance.ConvertBack(null, typeof(object), null, Culture));
        Assert.Throws<NotImplementedException>(() => new EngineStateToTextConverter().ConvertBack(null, typeof(object), null, Culture));
    }
}
