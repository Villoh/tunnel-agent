using System;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace TunnelAgent.Services;

/// <summary>
/// XAML markup extension for localized text: <c>Text="{l:Loc App_Title}"</c>.
/// Binds to <see cref="LocalizationService.CurrentCulture"/> through a converter that resolves the
/// key, so every usage re-reads its string when the language changes.
/// </summary>
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LocalizationService.CurrentCulture))
        {
            Source = LocalizationService.Instance,
            Mode = BindingMode.OneWay,
            Converter = new LocConverter(Key),
        };
}

/// <summary>Resolves a localization key to its current translation. The bound value (the culture) is ignored.</summary>
public sealed class LocConverter(string key) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        LocalizationService.Instance.GetString(key);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
