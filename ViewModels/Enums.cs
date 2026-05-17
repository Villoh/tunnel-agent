// ViewModels/Enums.cs
namespace TunnelAgent.ViewModels;

public enum ServerState { Stopped, Starting, Running, Error }

public enum SectionKey { Providers, Agents, Configuration }

public enum EngineState { NotInstalled, Downloading, Installing, Stopped, Starting, Running, Error }
