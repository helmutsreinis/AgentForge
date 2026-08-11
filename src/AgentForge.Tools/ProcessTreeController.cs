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

    public static void TerminateManagedTree(Process process)
    {
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
        ProcessTreeController.TerminateManagedTree(process);
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
    private const uint ToolhelpSnapshotProcesses = 0x00000002;
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
        return _attached && AttachEscapedDescendants(process.Id);
    }

    public void Terminate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_attached)
        {
            // A fast descendant can start between Process.Start and Job attachment.
            // Kill the managed tree before the Job so such a descendant is not
            // orphaned when its attached parent exits.
            ProcessTreeController.TerminateManagedTree(process);
            _ = TerminateJobObject(_job, 1);
            return;
        }

        ProcessTreeController.TerminateManagedTree(process);
    }

    public void Dispose() => _job.Dispose();

    private bool AttachEscapedDescendants(int rootProcessId)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            var descendants = CaptureDescendants(rootProcessId);
            if (descendants is null)
            {
                return false;
            }

            var attachedAny = false;
            foreach (var processId in descendants)
            {
                Process? descendant = null;
                try
                {
                    descendant = Process.GetProcessById(processId);
                    if (descendant.HasExited)
                    {
                        continue;
                    }

                    if (!IsProcessInJob(descendant.Handle, _job, out var isInJob))
                    {
                        if (descendant.HasExited)
                        {
                            continue;
                        }

                        return false;
                    }

                    if (!isInJob)
                    {
                        if (!AssignProcessToJobObject(_job, descendant.Handle))
                        {
                            if (descendant.HasExited)
                            {
                                continue;
                            }

                            return false;
                        }

                        attachedAny = true;
                    }
                }
                catch (ArgumentException)
                {
                    // The snapshot entry exited before its process handle was opened.
                }
                catch (InvalidOperationException)
                {
                    // The process exited while its state or handle was queried.
                }
                catch (Win32Exception)
                {
                    return false;
                }
                finally
                {
                    descendant?.Dispose();
                }
            }

            if (!attachedAny)
            {
                return true;
            }
        }

        return false;
    }

    private static List<int>? CaptureDescendants(int rootProcessId)
    {
        using var snapshot = CreateToolhelp32Snapshot(ToolhelpSnapshotProcesses, 0);
        if (snapshot.IsInvalid)
        {
            return null;
        }

        var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
        if (!Process32First(snapshot, ref entry))
        {
            return null;
        }

        var children = new Dictionary<uint, List<uint>>();
        do
        {
            if (!children.TryGetValue(entry.ParentProcessId, out var childIds))
            {
                childIds = [];
                children[entry.ParentProcessId] = childIds;
            }

            childIds.Add(entry.ProcessId);
            entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
        }
        while (Process32Next(snapshot, ref entry));

        var descendants = new List<int>();
        var pending = new Queue<uint>();
        var visited = new HashSet<uint>();
        pending.Enqueue((uint)rootProcessId);
        while (pending.TryDequeue(out var parentId))
        {
            if (!visited.Add(parentId))
            {
                continue;
            }

            if (!children.TryGetValue(parentId, out var childIds))
            {
                continue;
            }

            foreach (var childId in childIds)
            {
                if (childId > int.MaxValue)
                {
                    continue;
                }

                descendants.Add((int)childId);
                pending.Enqueue(childId);
            }
        }

        return descendants;
    }

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
    private static partial bool IsProcessInJob(
        IntPtr process,
        SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32First(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32Next(SafeFileHandle snapshot, ref ProcessEntry32 entry);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private unsafe struct ProcessEntry32
    {
        public uint Size;
        public uint UsageCount;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint ThreadCount;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        public fixed char ExecutableFile[260];
    }

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
