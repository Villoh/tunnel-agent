namespace TunnelAgent.Core.Engine;

/// <summary>Lifecycle state for managed engines.</summary>
public enum EngineState
{
    NotInstalled,
    Downloading,
    Installing,
    Stopped,
    Starting,
    Running,
    Error
}
