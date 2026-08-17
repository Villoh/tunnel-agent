using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace TunnelAgent.Infrastructure.Engine;

/// <summary>
/// Windows job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>. Child processes
/// assigned to the job are terminated when the last handle is closed — including when
/// Tunnel Agent crashes or is killed, because the OS closes the handle on process exit.
/// </summary>
/// <remarks>
/// Intentionally not a <see cref="SafeHandle"/>: a GC finalizer would close the handle and
/// kill the engine while Tunnel Agent is still running. The owning <c>ProcessService</c>
/// keeps this instance alive and disposes it only on an explicit stop.
/// </remarks>
internal sealed class KillOnCloseJob : IDisposable
{
    private IntPtr _handle;

    private KillOnCloseJob(IntPtr handle) => _handle = handle;

    /// <summary>
    /// Creates an unnamed kill-on-close job on Windows. Returns <see langword="null"/> on
    /// other platforms or if the job cannot be created.
    /// </summary>
    public static KillOnCloseJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        return TryCreateWindows();
    }

    [SupportedOSPlatform("windows")]
    private static KillOnCloseJob? TryCreateWindows()
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return null;

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!NativeMethods.SetInformationJobObject(
                handle,
                NativeMethods.JobObjectExtendedLimitInformation,
                ref info,
                size))
        {
            NativeMethods.CloseHandle(handle);
            return null;
        }

        return new KillOnCloseJob(handle);
    }

    /// <summary>
    /// Assigns <paramref name="process"/> to this job so it (and its children) die with the job.
    /// Returns <see langword="false"/> when assignment fails (for example nested-job limits).
    /// </summary>
    public bool TryAssign(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows() || _handle == IntPtr.Zero)
            return false;

        return TryAssignWindows(process);
    }

    [SupportedOSPlatform("windows")]
    private bool TryAssignWindows(Process process)
    {
        try
        {
            return NativeMethods.AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_handle == IntPtr.Zero)
            return;

        if (OperatingSystem.IsWindows())
            NativeMethods.CloseHandle(_handle);

        _handle = IntPtr.Zero;
        GC.SuppressFinalize(this);
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        public const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        public const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetInformationJobObject(
            IntPtr hJob,
            int jobObjectInformationClass,
            ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
            int cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint PeakProcessMemoryUsed;
            public nuint PeakJobMemoryUsed;
        }
    }
}
