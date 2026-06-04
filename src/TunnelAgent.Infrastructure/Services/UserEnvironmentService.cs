using System;
using System.Runtime.InteropServices;

namespace TunnelAgent.Infrastructure.Services;

/// <summary>
/// Sets and removes persistent user-level environment variables.
/// On Windows, broadcasts WM_SETTINGCHANGE so newly spawned processes pick up the change without logoff.
/// </summary>
public static class UserEnvironmentService
{
    public static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name);

    public static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        BroadcastSettingChange();
    }

    public static void Remove(string name)
    {
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        BroadcastSettingChange();
    }

    private static void BroadcastSettingChange()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;
        // Notify the system that user environment variables have changed.
        // HWND_BROADCAST = 0xFFFF, WM_SETTINGCHANGE = 0x001A, "Environment"
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment",
            SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);
    }

    [Flags]
    private enum SendMessageTimeoutFlags : uint { SMTO_ABORTIFHUNG = 0x0002 }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        SendMessageTimeoutFlags flags, uint timeout, out IntPtr result);
}
