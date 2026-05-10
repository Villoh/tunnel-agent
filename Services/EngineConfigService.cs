using System.IO;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Generates and writes the config.yaml for CLIProxyAPI.
/// Does not know how to download or run the binary.
/// </summary>
public sealed class EngineConfigService
{
    private static readonly IPlatformInfo Platform = IPlatformInfo.Current;

    private readonly SettingsService _settings;

    public string ConfigPath { get; } = Path.Combine(
        IPlatformInfo.Current.SettingsDirectory, "config.yaml");

    public EngineConfigService(SettingsService settings) => _settings = settings;

    /// <summary>
    /// Writes config.yaml from current AppSettings. Must be called before starting the process.
    /// </summary>
    public async Task WriteConfigAsync()
    {
        var s = _settings.Current;

        // auth-dir must be an absolute path — CLIProxyAPI does not expand ~ on all platforms
        var authDir = Platform.AuthDirectory.Replace('\\', '/');

        var yaml = $"""
            host: "127.0.0.1"
            port: {s.Port}
            auth-dir: "{authDir}"
            api-keys: []
            debug: {(s.LogLevel == "debug" ? "true" : "false")}
            """;

        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        await File.WriteAllTextAsync(ConfigPath, yaml);
    }
}
