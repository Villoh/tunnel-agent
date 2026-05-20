using System;
using TunnelAgent.Services;

using TunnelAgent.Core.Engine;
namespace TunnelAgent.ViewModels;

public sealed class EngineReleaseViewModel
{
    public EngineReleaseViewModel(EngineReleaseInfo release)
    {
        TagName = release.TagName;
        DisplayName = release.DisplayName;
        IsPrerelease = release.IsPrerelease;
        PublishedAt = release.PublishedAt;
    }

    public string TagName { get; }
    public string DisplayName { get; }
    public bool IsPrerelease { get; }
    public DateTimeOffset? PublishedAt { get; }

    public string DisplayText => IsPrerelease ? $"{TagName} pre" : TagName;

    public override string ToString() => DisplayText;
}
