using CommunityToolkit.Mvvm.ComponentModel;

namespace TunnelAgent.ViewModels;

public partial class SelectableModelViewModel : ViewModelBase
{
    public string Name     { get; }
    public string Provider { get; }
    public string EngineId { get; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private bool _isVisible = true;

    public SelectableModelViewModel(string name, string provider, string engineId = "")
    {
        Name     = name;
        Provider = provider;
        EngineId = engineId;
    }

    public bool Matches(string query) =>
        string.IsNullOrWhiteSpace(query) ||
        Name.Contains(query, System.StringComparison.OrdinalIgnoreCase) ||
        Provider.Contains(query, System.StringComparison.OrdinalIgnoreCase);
}
