using System;
using TunnelAgent.Services;

namespace TunnelAgent.Infrastructure.Services;

/// <summary>
/// Static facade over <see cref="IUserEnvironmentService"/> — preserves all
/// existing call sites while delegating to the correct OS implementation.
/// <para>
/// On startup, call <see cref="Initialize"/> once so that the Unix app-owned
/// store is loaded into the current process environment before any <see cref="Get"/>
/// calls are made.
/// </para>
/// </summary>
public static class UserEnvironmentService
{
    private static IUserEnvironmentService _impl = CreateImpl();

    private static IUserEnvironmentService CreateImpl()
    {
        if (OperatingSystem.IsWindows())
            return new WindowsUserEnvironmentService();
        return new UnixUserEnvironmentService();
    }

    /// <summary>
    /// Seeds the current process environment from the Unix app-owned store so
    /// that <see cref="Get"/> returns persisted values immediately after startup.
    /// No-op on Windows (the registry is already the source of truth).
    /// </summary>
    public static void Initialize()
    {
        if (OperatingSystem.IsWindows()) return;
        // Warm up the store: reading every persisted key via Get() propagates
        // its value into EnvironmentVariableTarget.Process via UnixUserEnvironmentService.
        // We rely on the fact that UnixUserEnvironmentService.Get reads the app store
        // first; by calling Set for each found value we ensure the process env is seeded.
        var unix = (UnixUserEnvironmentService)_impl;
        unix.SeedProcessEnvironment();
    }

    public static string? Get(string name) => _impl.Get(name);

    public static void Set(string name, string value) => _impl.Set(name, value);

    public static void Remove(string name) => _impl.Remove(name);
}
