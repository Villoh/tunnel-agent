using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using TunnelAgent.ViewModels;

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
