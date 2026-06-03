using System;
using System.Threading.Tasks;
using Avalonia.Svg.Skia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using TunnelAgent.Services;

namespace TunnelAgent.ViewModels;

public partial class AgentViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public string BinaryName { get; }
    public string Description { get; }
    public string? DocsUrl { get; }
    public string AccentHex { get; }
    public string? IconAssetPath { get; }
    public bool IconNeedsDarkBg { get; }
    public bool HasIcon => Icon is not null;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private SvgImage? _icon;
    public string Initials => Name.Length >= 2 ? Name[..2] : Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledOnly))]
    private bool _installed;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _routeProviderId = "";
    [ObservableProperty] private string? _binaryPath;
    [ObservableProperty] private string? _version;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalledOnly))]
    [NotifyPropertyChangedFor(nameof(ConfigureButtonLabel))]
    private bool _configured;

    /// <summary>True when installed but not yet configured through the proxy.</summary>
    public bool IsInstalledOnly => Installed && !Configured;

    public string ConfigureButtonLabel => Configured ? "Reconfigure" : "Configure";

    /// <summary>Checked in the Configure dialog to pick which agents to configure at once.</summary>
    [ObservableProperty] private bool _isSelectedForConfig;

    public AgentViewModel(AgentDefinition def)
    {
        Id = def.Id;
        Name = def.DisplayName;
        BinaryName = def.BinaryNames.Length > 0 ? def.BinaryNames[0] : def.Id;
        Description = def.Description;
        DocsUrl = def.DocsUrl;
        AccentHex = def.AccentHex;
        IconAssetPath = def.IconAssetPath;
        IconNeedsDarkBg = def.IconNeedsDarkBg;
        if (!string.IsNullOrEmpty(def.IconAssetPath))
        {
            _ = LoadIconAsync(def.IconAssetPath);
            if (def.IconNeedsDarkBg)
                SubscribeToThemeChanges(def.IconAssetPath);
        }
    }

    private void SubscribeToThemeChanges(string path)
    {
        if (Avalonia.Application.Current is null) return;
        Avalonia.Application.Current.ActualThemeVariantChanged += (_, _) => _ = LoadIconAsync(path);
    }

    private async Task LoadIconAsync(string path)
    {
        var normalized = Converters.SvgImageConverter.NormalizeAssetPath(path);
        var css = GetThemeCss();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            var source = SvgSource.Load(normalized, null);
            Icon = source is null ? null : new SvgImage { Source = source, Css = css };
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    private string? GetThemeCss()
    {
        if (!IconNeedsDarkBg) return null;
        var isDark = Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Dark
                  || Avalonia.Application.Current?.RequestedThemeVariant == ThemeVariant.Dark;
        return isDark ? "path { fill: #ffffff; }" : "path { fill: #000000; }";
    }

    public void ApplyDetection(AgentDetectionResult result)
    {
        Installed = result.Installed;
        BinaryPath = result.BinaryPath;
        Version = result.Version;
        Configured = result.Configured;
    }
}
