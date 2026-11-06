using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ComputerNeighbour.Platform;

/// <summary>
/// Owns native inference/audio children for the lifetime of this process. Windows closes the
/// non-inheritable job handle even after a console close, crash, or forced parent termination.
/// </summary>
public static class ChildProcessJob
{
    private static readonly Lazy<SafeJobHandle> Job = new(CreateJob, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Attach immediately after Process.Start, before awaiting child output. If a host policy
    /// prevents nested jobs, kill the just-started child and fail clearly instead of orphaning it.
    /// </summary>
    public static void Attach(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Child process ownership requires Windows.");
        try
        {
            var handle = Job.Value;
            if (AssignProcessToJobObject(handle, process.SafeHandle)) return;
            var error = Marshal.GetLastWin32Error();
            // A small utility can finish before assignment; there is no orphan to contain.
            if (process.HasExited) return;
            throw new Win32Exception(error, "Windows denied child-process ownership. This host may prohibit nested job objects. Start ComputerNeighbour directly from a normal Windows console.");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            throw;
        }
    }

    private static SafeJobHandle CreateJob()
    {
        // No name and no SECURITY_ATTRIBUTES: other processes cannot keep this handle alive,
        // and a child does not inherit the handle that controls its own lifetime.
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the child-process job.");
        var information = new ExtendedLimitInformation { Basic = new BasicLimitInformation { LimitFlags = 0x00002000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref information, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error(); handle.Dispose();
            throw new Win32Exception(error, "Cannot enable automatic child-process cleanup.");
        }
        return handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }
    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass, ref ExtendedLimitInformation information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
