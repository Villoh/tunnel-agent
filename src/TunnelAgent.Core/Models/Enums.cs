// ViewModels/Enums.cs
namespace TunnelAgent.ViewModels;

public enum ServerState { Stopped, Starting, Running, Error }

public enum SectionKey { Home, Providers, Quota, Fallback, NineRouterCombos, Agents, Logs, Configuration, ConfigGeneral, ConfigCliProxy, ConfigPerplexity, ConfigNineRouter }

/// <summary>Strategy for selecting which API key to use when multiple accounts are configured for a provider.</summary>
public enum RoutingStrategy { RoundRobin, FillFirst }
