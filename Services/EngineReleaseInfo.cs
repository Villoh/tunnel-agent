using System;

namespace TunnelAgent.Services;

public sealed record EngineReleaseInfo(
    string TagName,
    string DisplayName,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt);
