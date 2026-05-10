// Services/AppSettings.cs
using System.Text.Json.Serialization;

namespace TunnelAgent.Services;

public sealed class AppSettings
{
    public int Port { get; set; } = 8317;
    public string BindAddress { get; set; } = "127.0.0.1";
    public bool LaunchAtLogin { get; set; } = true;
    public bool IsDark { get; set; }
    public string LogLevel { get; set; } = "info";
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = false;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InstalledEngineVersion { get; set; }
}
