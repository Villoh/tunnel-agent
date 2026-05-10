// Services/AppSettings.cs
namespace TunnelAgent.Services;

public sealed class AppSettings
{
    public int Port { get; set; } = 8317;
    public bool LaunchAtLogin { get; set; } = true;
    public bool IsDark { get; set; }
    public string LogLevel { get; set; } = "info";
    public bool AutoCheckForUpdates { get; set; } = true;
    public bool AutoUpdate { get; set; } = false;


}
