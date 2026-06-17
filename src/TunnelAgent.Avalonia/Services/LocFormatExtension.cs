using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace TunnelAgent.Services;

/// <summary>
/// XAML markup extension for localized text with placeholders:
/// <c>Text="{l:LocFormat Key=ConfigView_CLIProxy_Engine_Installed, Path=CliProxyInstalledVersion}"</c>.
/// Builds a <see cref="MultiBinding"/> whose first value is the active culture (so the text refreshes on
/// language change) followed by the bound argument(s) fed into <see cref="string.Format(IFormatProvider, string, object?[])"/>.
/// Supports up to two arguments (<see cref="Path"/> and <see cref="Path2"/>).
/// </summary>
public sealed class LocFormatExtension : MarkupExtension
{
    public LocFormatExtension()
    {
    }

    public LocFormatExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    /// <summary>Binding path for the first format argument (<c>{0}</c>).</summary>
    public string? Path { get; set; }

    /// <summary>Binding path for the second format argument (<c>{1}</c>), if any.</summary>
    public string? Path2 { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var multi = new MultiBinding
        {
            Mode = BindingMode.OneWay,
            Converter = new LocFormatConverter(Key),
        };

        // First value drives re-evaluation when the culture changes; it is not used as a format arg.
        multi.Bindings.Add(new Binding(nameof(LocalizationService.CurrentCulture))
        {
            Source = LocalizationService.Instance,
        });

        if (!string.IsNullOrEmpty(Path))
            multi.Bindings.Add(new Binding(Path));
        if (!string.IsNullOrEmpty(Path2))
            multi.Bindings.Add(new Binding(Path2));

        return multi;
    }
}

/// <summary>Formats a localized template (looked up by key) with the bound argument values.</summary>
public sealed class LocFormatConverter(string key) : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var format = LocalizationService.Instance.GetString(key);
        // Skip the first value (the culture trigger).
        var args = values.Skip(1).ToArray();
        try
        {
            return string.Format(LocalizationService.Instance.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}
