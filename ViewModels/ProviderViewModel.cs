using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IconPacks.Avalonia.SimpleIcons;

namespace TunnelAgent.ViewModels;

public partial class ProviderViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public PackIconSimpleIconsKind IconKind { get; }
    public string LogoColor { get; }
    public string Description { get; }

    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string _account = "";
    [ObservableProperty] private string _model = "";

    public ObservableCollection<double> Spark { get; } = new();

    public ProviderViewModel(string id, string name, PackIconSimpleIconsKind iconKind, string logoColor, string description)
    {
        Id = id;
        Name = name;
        IconKind = iconKind;
        LogoColor = logoColor;
        Description = description;
    }
}
