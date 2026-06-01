using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Svg.Skia;
using IconPacks.Avalonia.Lucide;
using TunnelAgent.Services;
using TunnelAgent.ViewModels;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.Converters;

public sealed class ServerStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ServerState s ? s switch
        {
            ServerState.Running  => "Running",
            ServerState.Stopped  => "Stopped",
            ServerState.Starting => "Starting…",
            ServerState.Error    => "Error",
            _ => ""
        } : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class ServerStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark ||
                     Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;

        var color = value is ServerState s ? s switch
        {
            ServerState.Running  => isDark ? Color.Parse("#46C788") : Color.Parse("#1F8A5B"),
            ServerState.Stopped  => isDark ? Color.Parse("#E96B57") : Color.Parse("#C84B36"),
            ServerState.Starting => isDark ? Color.Parse("#E2A52E") : Color.Parse("#C98A0B"),
            ServerState.Error    => isDark ? Color.Parse("#E96B57") : Color.Parse("#C84B36"),
            _ => isDark ? Color.Parse("#717784") : Color.Parse("#9097A2")
        } : isDark ? Color.Parse("#717784") : Color.Parse("#9097A2");

        return new SolidColorBrush(color);
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class ServerStateRunningConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var running = value is ServerState s && s == ServerState.Running;
        if (parameter is string p && p.Equals("Invert", StringComparison.OrdinalIgnoreCase))
            return !running;
        return running;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && Color.TryParse(s, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class SvgImageConverter : IValueConverter
{
    private static readonly Dictionary<string, SvgImage?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path)) return null;
        var normalized = NormalizeAssetPath(path);
        if (_cache.TryGetValue(normalized, out var cached)) return cached;
        var source = SvgSource.Load(normalized, null);
        var image = source is null ? null : new SvgImage { Source = source };
        _cache[normalized] = image;
        return image;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();

    internal static string NormalizeAssetPath(string path) =>
        path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase)
            ? path
            : "avares://TunnelAgent" + (path.StartsWith('/') ? path : "/" + path);
}

public sealed class SectionEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SectionKey s && parameter is string p && Enum.TryParse<SectionKey>(p, out var pk) && s == pk;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class SidebarWidthConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var collapsed = value is true;
        return new GridLength(collapsed ? 56 : 176);
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Converts RoutingStrategy enum to a user-friendly display string.</summary>
public sealed class RoutingStrategyToStringConverter : IValueConverter
{
    public static readonly RoutingStrategyToStringConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is RoutingStrategy s ? s switch
        {
            RoutingStrategy.RoundRobin => "Round Robin (even distribution)",
            RoutingStrategy.FillFirst => "Fill First (use first account until limit)",
            _ => s.ToString()
        } : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Returns "Disable" for true, "Enable" for false — used for provider toggle tooltips.</summary>
public sealed class BoolToEnableDisableTextConverter : IValueConverter
{
    public static readonly BoolToEnableDisableTextConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "Disable" : "Enable";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Returns 1.0 for true, 0.4 for false — used to dim disabled provider logos.</summary>
public sealed class BoolToOpacityConverter : IValueConverter
{
    public static readonly BoolToOpacityConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : 0.4;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Returns true when a string is non-null and non-empty.</summary>
public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrEmpty(s);
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class SecretPasswordCharConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? '\0' : '●';
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public sealed class BoolToEyeIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? PackIconLucideKind.EyeOff : PackIconLucideKind.Eye;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Inverts a bool value. Usable as a markup extension via InvertBoolConverter.</summary>
public sealed class InvertBoolConverter : IValueConverter
{
    public static readonly InvertBoolConverter Instance = new();
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b ? !b : value;
}

/// <summary>MultiBinding AND: returns true only when ALL bound bool values are true.</summary>
public sealed class MultiBoolAndConverter : IMultiValueConverter
{
    public static readonly MultiBoolAndConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true);
}

// Converter used in ConfigurationView to show/hide the download progress bar.
// ConverterParameter="IsDownloading" returns true when state is Downloading or Installing.
public sealed class EngineStateToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter is string p && p == "IsDownloading")
            return value is EngineState s &&
                   (s == EngineState.Downloading ||
                    s == EngineState.Installing);
        return value is EngineState state ? state.ToString() : "";
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Returns true when the bound integer is greater than zero. Used to show/hide sections.</summary>
public sealed class IntGreaterThanZeroConverter : IValueConverter
{
    public static readonly IntGreaterThanZeroConverter Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && i > 0;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}
