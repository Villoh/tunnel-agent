namespace TunnelAgent.Services;

/// <summary>
/// Reads and writes persistent user-level environment variables.
/// Implementations must ensure variables survive process restarts and are
/// visible to newly spawned child processes (e.g. CLI agents).
/// </summary>
public interface IUserEnvironmentService
{
    /// <summary>
    /// Returns the value of <paramref name="name"/> from the persistent user
    /// environment, falling back to the current process environment.
    /// </summary>
    string? Get(string name);

    /// <summary>
    /// Persists <paramref name="value"/> as a user-level environment variable
    /// and propagates the change to the current process immediately.
    /// </summary>
    void Set(string name, string value);

    /// <summary>
    /// Removes the user-level environment variable and clears it from the
    /// current process.
    /// </summary>
    void Remove(string name);
}
