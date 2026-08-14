using System;
using System.Collections.Generic;
using System.Linq;

using TunnelAgent.Core.Engine;
using TunnelAgent.Services;
using CliProxyEngineService = TunnelAgent.Infrastructure.Engine.CliProxy.EngineService;
using PerplexityEngineService = TunnelAgent.Infrastructure.Engine.Perplexity.EngineService;
using NineRouterEngineService = TunnelAgent.Infrastructure.Engine.NineRouter.EngineService;

namespace TunnelAgent.Infrastructure.Engine;

/// <summary>Creates and exposes all managed engines for current app session.</summary>
public sealed class EngineRegistryService
{
    private readonly Dictionary<string, IManagedEngine> _engines;

    public EngineRegistryService(SettingsService settings)
    {
        var engines = new IManagedEngine[]
        {
            new CliProxyEngineService(settings),
            new PerplexityEngineService(settings),
            new NineRouterEngineService(settings)
        };

        _engines = engines.ToDictionary(e => e.Definition.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IManagedEngine> Engines => _engines.Values;

    public IManagedEngine Get(string engineId) => _engines[engineId];
}
