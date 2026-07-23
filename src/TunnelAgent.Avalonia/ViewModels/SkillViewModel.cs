using CommunityToolkit.Mvvm.ComponentModel;
using TunnelAgent.Core.Skills;

namespace TunnelAgent.ViewModels;

public sealed partial class SkillViewModel(SkillSummary model) : ObservableObject
{
    public SkillSummary Model { get; } = model;
    public string Name => Model.Name;
    public string Description => Model.Description;
    public string Version => Model.Version;
    public string Source => Model.Source;
    public string Path => Model.Path ?? "";
    public string Scope => Model.Scope ?? "";
    public bool Installed => Model.Installed;
    [ObservableProperty] private string _auditVerdict = "";
}
