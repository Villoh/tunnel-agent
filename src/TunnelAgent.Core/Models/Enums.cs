// ViewModels/Enums.cs
namespace TunnelAgent.ViewModels;

public enum ServerState { Stopped, Starting, Running, Error }

public enum SectionKey { Providers, Agents, Configuration }

public enum EngineState { NotInstalled, Downloading, Installing, Stopped, Starting, Running, Error }

/// <summary>Strategy for selecting which API key to use when multiple accounts are configured for a provider.</summary>
public enum RoutingStrategy { RoundRobin, FillFirst }
