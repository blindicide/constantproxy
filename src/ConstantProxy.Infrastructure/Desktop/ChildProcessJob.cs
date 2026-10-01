using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ConstantProxy.Infrastructure.Desktop;

/// <summary>
/// Windows job object that kills the ssh processes constantproxy started if constantproxy itself dies
/// (crash, Task Manager, power loss of the session). Only processes explicitly assigned here are affected, never
/// any other ssh.exe (SPEC §36, §82). On other platforms it does nothing.
/// </summary>
public static class ChildProcessJob
{
    private static readonly object Gate = new();
    private static IntPtr jobHandle;
    private static bool failed;

    /// <summary>Returns true if the process is now covered by the kill-on-close job.</summary>
    public static bool TryAssign(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return AssignOnWindows(process);
    }

    [SupportedOSPlatform("windows")]
    private static bool AssignOnWindows(Process process)
    {
        lock (Gate)
        {
            if (failed)
            {
                return false;
            }

            if (jobHandle == IntPtr.Zero && !CreateJob())
            {
                failed = true;
                return false;
            }

            try
            {
                return AssignProcessToJobObject(jobHandle, process.Handle);
            }
            catch (InvalidOperationException)
            {
                return false; // the process already exited
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool CreateJob()
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, pointer, (uint)size))
            {
                CloseHandle(handle);
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        jobHandle = handle; // intentionally never closed: closing it (at process exit) is what kills the children
        return true;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
