using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentForge.Tools;

internal interface IProcessTreeController : IDisposable
{
    bool Attach(Process process);

    void Terminate(Process process);
}

internal static class ProcessTreeController
{
    public static IProcessTreeController Create() => OperatingSystem.IsWindows()
        ? WindowsJobProcessTreeController.Create()
        : new ManagedProcessTreeController();
}

internal sealed class ManagedProcessTreeController : IProcessTreeController
{
    private Process? _process;

    public bool Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _process = process;
        return true;
    }

    public void Terminate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process may have exited between the state check and termination.
        }
    }

    public void Dispose()
    {
        if (_process is not null)
        {
            Terminate(_process);
        }
    }
}

internal sealed partial class WindowsJobProcessTreeController : IProcessTreeController
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _job;
    private bool _attached;

    private WindowsJobProcessTreeController(SafeFileHandle job)
    {
        _job = job;
    }

    public static WindowsJobProcessTreeController Create()
    {
        var job = CreateJobObject(IntPtr.Zero, IntPtr.Zero);
        if (job.IsInvalid)
        {
            job.Dispose();
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var pointer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(information, pointer, fDeleteOld: false);
            if (!SetInformationJobObject(job, 9, pointer, (uint)size))
            {
                var error = Marshal.GetLastPInvokeError();
                job.Dispose();
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return new WindowsJobProcessTreeController(job);
    }

    public bool Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        _attached = AssignProcessToJobObject(_job, process.Handle);
        return _attached;
    }

    public void Terminate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_attached)
        {
            _ = TerminateJobObject(_job, 1);
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or Win32Exception)
        {
            // The process may have exited between the state check and termination.
        }
    }

    public void Dispose() => _job.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)]
    private static partial SafeFileHandle CreateJobObject(IntPtr jobAttributes, IntPtr name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        IntPtr information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
