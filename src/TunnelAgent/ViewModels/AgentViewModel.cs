using CommunityToolkit.Mvvm.ComponentModel;

namespace TunnelAgent.ViewModels;

public partial class AgentViewModel : ViewModelBase
{
    public string Id { get; }
    public string Name { get; }
    public string Binary { get; }
    public string IconKey { get; }
    public bool Installed { get; }
    public string? Hint { get; }

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _routeProviderId = "";

    public AgentViewModel(string id, string name, string binary, string iconKey,
                          bool installed, string? hint = null)
    {
        Id = id; Name = name; Binary = binary; IconKey = iconKey;
        Installed = installed; Hint = hint;
    }
}
