using System;
using System.Diagnostics;
using System.IO;

namespace TunnelAgent.Services;

public sealed class FolderOpenService : IFolderOpenService
{
    public void OpenFolder(string directory)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            var startInfo = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
            startInfo.ArgumentList.Add(directory);
            Process.Start(startInfo);
            return;
        }

        var linuxStartInfo = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        linuxStartInfo.ArgumentList.Add(directory);
        Process.Start(linuxStartInfo);
    }
}
