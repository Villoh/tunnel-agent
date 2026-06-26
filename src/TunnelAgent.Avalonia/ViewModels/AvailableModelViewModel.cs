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

    public AvailableModelGroupViewModel(string providerName, string providerId, bool isExpanded = false)
    {
        ProviderName = providerName;
        ProviderId   = providerId;
        IsExpanded   = isExpanded;
    }

    public string ProviderName { get; }
    public string ProviderId   { get; }

    private TunnelAgent.Services.ProviderIconRegistry.ProviderIconDisplay Icon =>
        TunnelAgent.Services.ProviderIconRegistry.GetDisplay(ProviderId, ProviderName);
    public IconPacks.Avalonia.SimpleIcons.PackIconSimpleIconsKind IconKind => Icon.IconKind;
    public string LogoColor       => Icon.LogoColor;
    public string? CustomIconData => Icon.CustomIconData;
    public bool HasCustomIcon     => Icon.HasCustomIcon;
    public bool ShowSimpleIcon    => Icon.ShowSimpleIcon;
    public bool UseMonogram       => Icon.UseMonogram;
    public string Monogram        => Icon.Monogram;
    public ObservableCollection<AvailableModelViewModel> Models { get; } = new();
    public int ModelCount => Models.Count;
    public int HiddenModelCount => ModelCount > 3 ? ModelCount - 3 : 0;
}
