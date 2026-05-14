using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TunnelAgent.ViewModels;

public sealed class AvailableModelViewModel
{
    public AvailableModelViewModel(string name, string authKind, string context, string provider)
    {
        Name = name;
        AuthKind = authKind;
        Context = context;
        Provider = provider;
    }

    public string Name { get; }
    public string AuthKind { get; }
    public string Context { get; }
    public string Provider { get; }
}

public partial class AvailableModelGroupViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isExpanded;

    public AvailableModelGroupViewModel(string providerName, string providerId,
        IconPacks.Avalonia.SimpleIcons.PackIconSimpleIconsKind iconKind,
        string logoColor, string? customIconData = null, bool isExpanded = false)
    {
        ProviderName   = providerName;
        ProviderId     = providerId;
        IconKind       = iconKind;
        LogoColor      = logoColor;
        CustomIconData = customIconData;
        IsExpanded     = isExpanded;
    }

    public string ProviderName   { get; }
    public string ProviderId     { get; }
    public IconPacks.Avalonia.SimpleIcons.PackIconSimpleIconsKind IconKind { get; }
    public string LogoColor      { get; }
    public string? CustomIconData { get; }
    public bool HasCustomIcon => CustomIconData is not null;
    public ObservableCollection<AvailableModelViewModel> Models { get; } = new();
    public int ModelCount => Models.Count;
    public int HiddenModelCount => ModelCount > 3 ? ModelCount - 3 : 0;
}
