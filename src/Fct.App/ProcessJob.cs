using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Fct.App;

// Ties child processes to the host's lifetime via a Windows Job Object configured with
// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Every process assigned to the job — and any process it
// later spawns (e.g. OverlayPlugin's CEF subprocesses) — is terminated by the OS as soon as the
// last handle to the job closes. The host holds that handle for its whole lifetime, so when the
// host exits (cleanly, by crash, or by force-kill) the OS closes the handle and kills the
// satellite. This is what prevents orphaned Fct.LegacyHost processes.
//
// Self-contained (no Avalonia / Fct.App dependencies) so it can be linked into the test project.
internal sealed class ProcessJob : IDisposable
{
    private readonly SafeJobHandle _job;

    private ProcessJob(SafeJobHandle job) => _job = job;

    public bool IsValid => !_job.IsInvalid && !_job.IsClosed;

    // Create a kill-on-close job, or null on a non-Windows OS or if the OS calls fail.
    public static ProcessJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return new ProcessJob(CreateKillOnCloseJob());
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    public void AddProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        AddProcess(process.Handle);
    }

    [SupportedOSPlatform("windows")]
    public void AddProcess(IntPtr processHandle)
    {
        if (_job.IsInvalid) return;
        if (!AssignProcessToJobObject(_job, processHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    public void Dispose() => _job.Dispose();

    [SupportedOSPlatform("windows")]
    private static SafeJobHandle CreateKillOnCloseJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr buffer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!SetInformationJobObject(job, JobObjectInfoClass.ExtendedLimitInformation, buffer, (uint)length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return job;
    }

    // --- interop ---

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9,
    }

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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob, JobObjectInfoClass infoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private sealed class SafeJobHandle : SafeHandle
    {
        public SafeJobHandle() : base(IntPtr.Zero, ownsHandle: true) { }
        public override bool IsInvalid => handle == IntPtr.Zero;
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
