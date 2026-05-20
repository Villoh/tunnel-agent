namespace TunnelAgent.Core.Engine;

/// <summary>Describes one runnable engine managed by TunnelAgent.</summary>
public sealed record EngineDefinition(
    string Id,
    string DisplayName,
    string Description,
    string RepositoryOwner,
    string RepositoryName,
    int DefaultPort);
