using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TunnelAgent.Infrastructure.Services;

/// <summary>
/// Windows implementation: persists variables in the user registry hive
/// (HKCU\Environment) and broadcasts WM_SETTINGCHANGE so newly spawned
/// processes pick them up without requiring a logoff.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsUserEnvironmentService : TunnelAgent.Services.IUserEnvironmentService
{
    public string? Get(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable(name);

    public void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);
        BroadcastSettingChange();
    }

    public void Remove(string name)
    {
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, null, EnvironmentVariableTarget.Process);
        BroadcastSettingChange();
    }

    private static void BroadcastSettingChange() =>
        SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment",
            SendMessageTimeoutFlags.SMTO_ABORTIFHUNG, 1000, out _);

    [Flags]
    private enum SendMessageTimeoutFlags : uint { SMTO_ABORTIFHUNG = 0x0002 }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        SendMessageTimeoutFlags flags, uint timeout, out IntPtr result);
}
