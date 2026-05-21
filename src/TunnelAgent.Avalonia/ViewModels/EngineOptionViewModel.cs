using TunnelAgent.Core.Engine;

namespace TunnelAgent.ViewModels;

public sealed class EngineOptionViewModel
{
    public string EngineId { get; }
    public string Name { get; }
    public string Description { get; }
    public int DefaultPort { get; }

    public EngineOptionViewModel(EngineDefinition definition)
    {
        EngineId = definition.Id;
        Name = definition.DisplayName;
        Description = definition.Description;
        DefaultPort = definition.DefaultPort;
    }
}
