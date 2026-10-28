using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PackCheck.Servers;

/// <summary>
/// Windows Job Object: every process assigned to it (and its children) is killed when the job closes,
/// and optional memory / CPU-time limits apply to the whole tree.
/// </summary>
public sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;
    private const uint LimitJobMemory = 0x200;
    private const uint LimitActiveProcess = 0x8;
    private const uint LimitJobTime = 0x4;

    private nint _handle;

    public JobObject(long? memoryLimitBytes = null, int? maxProcesses = null, TimeSpan? cpuTime = null)
    {
        _handle = CreateJobObjectW(0, null);
        if (_handle == 0)
        {
            throw new Win32Exception();
        }

        var info = new ExtendedLimitInformation();
        info.Basic.LimitFlags = KillOnJobClose;
        if (memoryLimitBytes is { } mem)
        {
            info.Basic.LimitFlags |= LimitJobMemory;
            info.JobMemoryLimit = (nuint)mem;
        }

        if (maxProcesses is { } mp)
        {
            info.Basic.LimitFlags |= LimitActiveProcess;
            info.Basic.ActiveProcessLimit = (uint)mp;
        }

        if (cpuTime is { } t)
        {
            info.Basic.LimitFlags |= LimitJobTime;
            info.Basic.PerJobUserTimeLimit = t.Ticks;
        }

        if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            throw new Win32Exception();
        }
    }

    public void Assign(System.Diagnostics.Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Kills every process in the job.</summary>
    public void Terminate() => TerminateJobObject(_handle, 1);

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
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
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
