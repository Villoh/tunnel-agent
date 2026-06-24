namespace TunnelAgent.Core.Engine;

/// <summary>
/// Structured reason for an engine failure, so the UI can present a
/// localized message instead of a raw English string.
/// </summary>
public enum EngineErrorKind
{
    None,
    PortInUse,
    Timeout,
    LaunchFailed,
    Crashed
}
