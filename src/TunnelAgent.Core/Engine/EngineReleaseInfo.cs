using System;

namespace TunnelAgent.Core.Engine;

public sealed record EngineReleaseInfo(
    string TagName,
    string DisplayName,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt);
