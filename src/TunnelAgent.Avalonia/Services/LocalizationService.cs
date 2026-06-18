using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace TunnelAgent.Services;

/// <summary>
/// Runtime localization service. Exposes translated strings through a string
/// indexer so XAML bindings (see <c>LocExtension</c>) refresh automatically when
/// the language changes. There is a single shared <see cref="Instance"/> used by
/// the markup extension and the view model.
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    public static LocalizationService Instance { get; } = new();

    private static readonly ResourceManager ResourceManager =
        new("TunnelAgent.Resources.Strings", typeof(LocalizationService).Assembly);

    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CultureInfo CurrentCulture => _currentCulture;

    /// <summary>Indexer used by XAML bindings: <c>{l:Loc App_Title}</c> binds to <c>this["App_Title"]</c>.</summary>
    public string this[string key] => GetString(key);

    public const string SystemLanguageCode = "";

    /// <summary>Languages that ship with a translation. Code is a culture name, Display is shown in the UI.</summary>
    public static IReadOnlyList<LanguageOption> SupportedLanguages { get; } =
    [
        new LanguageOption(SystemLanguageCode, "System default"),
        new LanguageOption("en-US", "English"),
        new LanguageOption("es-ES", "Español"),
        new LanguageOption("pt-PT", "Português"),
        new LanguageOption("it-IT", "Italiano"),
        new LanguageOption("fr-FR", "Français"),
        new LanguageOption("de-DE", "Deutsch"),
        new LanguageOption("zh-CN", "简体中文"),
        new LanguageOption("ja-JP", "日本語"),
        new LanguageOption("ar-SA", "العربية"),
        new LanguageOption("uk-UA", "Українська"),
        new LanguageOption("ru-RU", "Русский"),
        new LanguageOption("hi-IN", "हिन्दी"),
        new LanguageOption("ko-KR", "한국어"),
        new LanguageOption("tr-TR", "Türkçe"),
    ];

    public static IReadOnlyList<ThemeModeOption> SupportedThemeModes { get; } =
    [
        new ThemeModeOption("system", "ConfigView_General_Theme_System"),
        new ThemeModeOption("light", "ConfigView_General_Theme_Light"),
        new ThemeModeOption("dark", "ConfigView_General_Theme_Dark"),
    ];

    /// <summary>Returns the localized string for <paramref name="key"/>, or <c>[key]</c> when missing.</summary>
    public string GetString(string key)
    {
        try
        {
            return ResourceManager.GetString(key, _currentCulture) ?? $"[{key}]";
        }
        catch (Exception)
        {
            return $"[{key}]";
        }
    }

    /// <summary>Returns the localized string for <paramref name="key"/> formatted with <paramref name="args"/>.</summary>
    public string GetString(string key, params object[] args)
    {
        var format = GetString(key);
        try
        {
            return string.Format(_currentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    /// <summary>
    /// Switches the active culture and notifies every indexer binding so the UI re-reads its strings.
    /// Falls back to en-US for unknown cultures.
    /// </summary>
    public void SetCulture(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
            return;

        CultureInfo culture;
        try
        {
            culture = new CultureInfo(cultureName);
        }
        catch (CultureNotFoundException)
        {
            culture = new CultureInfo("en-US");
        }

        if (string.Equals(culture.Name, _currentCulture.Name, StringComparison.OrdinalIgnoreCase))
            return;

        _currentCulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Empty name forces all bindings (including the string indexer) on this source to re-evaluate.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentCulture)));
    }
}

/// <summary>A selectable UI language. <see cref="ToString"/> returns the display name for combo boxes.</summary>
public sealed record LanguageOption(string Code, string Display)
{
    public override string ToString() => Display;
}

public sealed class ThemeModeOption(string value, string displayKey) : INotifyPropertyChanged
{
    public string Value { get; } = value;
    public string Display => LocalizationService.Instance.GetString(displayKey);

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));

    public override string ToString() => Display;
}
