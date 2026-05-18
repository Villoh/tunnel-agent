using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace TunnelAgent.Services;

/// <summary>
/// Shared Unix-only helpers used by platform implementations.
/// </summary>
internal static class UnixHelper
{
    internal static async Task ChmodExecutableAsync(string path)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("chmod")
            {
                ArgumentList = { "+x", path },
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new Exception($"chmod exited with code {proc.ExitCode}");
    }

    internal static async Task ExtractTarGzAsync(string archivePath, string destDir)
    {
        System.IO.Directory.CreateDirectory(destDir);
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo("tar")
            {
                ArgumentList = { "-xzf", archivePath, "-C", destDir },
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        proc.Start();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new Exception($"tar exited with code {proc.ExitCode}");
    }
}
