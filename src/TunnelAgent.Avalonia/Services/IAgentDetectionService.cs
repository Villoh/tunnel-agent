using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

public sealed record AgentDetectionResult(
    string AgentId,
    bool Installed,
    bool Configured,
    string? BinaryPath,
    string? Version)
{
    public static AgentDetectionResult NotFound(string agentId) =>
        new(agentId, false, false, null, null);
}

public interface IAgentDetectionService
{
    Task<IReadOnlyList<AgentDetectionResult>> DetectAllAsync(CancellationToken ct = default);
    AgentDetectionResult GetCached(string agentId);
}
