using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MultiCat.Service.Rigctld;

/// <summary>
/// Ties spawned rigctld processes to this one, so they cannot outlive it.
/// <para>
/// Disposing a supervisor kills its child, but that only covers an orderly
/// shutdown. If the service crashes or is force-killed, no cleanup runs and the
/// children survive — still holding their connections to the radio. A radio with a
/// limited number of CAT connections then refuses the next start, so the symptom is
/// a radio that will not connect and a process list full of rigctld.
/// </para>
/// A job object with kill-on-close fixes that at the OS level: when this process
/// ends by any means, Windows closes its handles, the job's last handle goes with
/// them, and everything in the job is terminated.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ChildProcessJob
{
    private const uint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lock Gate = new();
    private static IntPtr _job = IntPtr.Zero;
    private static bool _unavailable;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job, uint infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
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
    private struct JobObjectExtendedLimitInfo
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    /// <summary>
    /// Puts a freshly started process in the job. Failure is not fatal — the
    /// supervisor still kills its child on an orderly shutdown — so this only
    /// degrades crash cleanup, and says so once rather than on every spawn.
    /// </summary>
    public static bool TryAssign(Process process, ILogger logger)
    {
        lock (Gate)
        {
            if (_unavailable)
            {
                return false;
            }

            if (_job == IntPtr.Zero && !TryCreate(logger))
            {
                return false;
            }

            if (AssignProcessToJobObject(_job, process.Handle))
            {
                return true;
            }

            logger.LogWarning(
                "Could not tie rigctld (pid {Pid}) to this process; a crash could leave it running (error {Error})",
                process.Id, Marshal.GetLastWin32Error());
            return false;
        }
    }

    private static bool TryCreate(ILogger logger)
    {
        // Unnamed, so each service instance gets its own job and never adopts
        // another instance's children.
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            logger.LogWarning("Could not create a job object; rigctld will not be cleaned up after a crash");
            _unavailable = true;
            return false;
        }

        var info = new JobObjectExtendedLimitInfo
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };

        var size = Marshal.SizeOf<JobObjectExtendedLimitInfo>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                logger.LogWarning(
                    "Could not set kill-on-close on the job object (error {Error}); rigctld will not be cleaned up after a crash",
                    Marshal.GetLastWin32Error());
                _unavailable = true;
                return false;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _job = job;     // deliberately never closed: closing it would kill the children
        return true;
    }
}
