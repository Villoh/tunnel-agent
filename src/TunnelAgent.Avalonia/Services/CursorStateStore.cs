using System;
using System.Collections.Generic;
using System.IO;

namespace TunnelAgent.Services;

/// <summary>
/// Locates Cursor's <c>state.vscdb</c>. Official installs use ApplicationData;
/// Scoop/portable builds keep user-data next to the app, which can leave a stale
/// AppData database from an older install.
/// </summary>
internal static class CursorStateStore
{
    internal static string? ResolveStateDbPath() =>
        ResolveStateDbPath(EnumerateCandidatePaths());

    internal static string? ResolveStateDbPath(IEnumerable<string> candidates)
    {
        string? best = null;
        var bestTime = DateTime.MinValue;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;

            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }

            if (!seen.Add(full)) continue;
            if (!File.Exists(full)) continue;

            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(full); }
            catch { continue; }

            if (best is null || mtime > bestTime)
            {
                best = full;
                bestTime = mtime;
            }
        }

        return best;
    }

    internal static IEnumerable<string> EnumerateCandidatePaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Path.Combine(appData, "Cursor", "User", "globalStorage", "state.vscdb");

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localAppData, "Cursor", "User", "globalStorage", "state.vscdb");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "scoop", "apps", "cursor", "current", "data", "user-data",
            "User", "globalStorage", "state.vscdb");
        yield return Path.Combine(home, "scoop", "persist", "cursor", "data", "user-data",
            "User", "globalStorage", "state.vscdb");
    }
}
