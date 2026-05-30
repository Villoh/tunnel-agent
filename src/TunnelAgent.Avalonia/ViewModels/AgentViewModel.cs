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
    public bool HasIcon => !string.IsNullOrEmpty(IconAssetPath);
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
    }

    public void ApplyDetection(AgentDetectionResult result)
    {
        Installed = result.Installed;
        BinaryPath = result.BinaryPath;
        Version = result.Version;
        Configured = result.Configured;
    }
}
