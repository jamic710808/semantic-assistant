using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SemanticAssistant.Adapters.Mcp;

// Limits only our schema helper, not the external MCP server. No filesystem/network sandbox claim.
internal sealed class SchemaWorkerJob : SafeHandleZeroOrMinusOneIsInvalid
{
    private SchemaWorkerJob() : base(true) { }
    public static SchemaWorkerJob? Attach(System.Diagnostics.Process process)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) { job.Dispose(); throw new Win32Exception(); }
        try
        {
            var limits = new ExtendedLimits
            {
                Basic = new BasicLimits
                {
                    // KILL_ON_JOB_CLOSE | PROCESS_MEMORY | ACTIVE_PROCESS | JOB_TIME
                    Flags = 0x2000 | 0x100 | 0x8 | 0x4,
                    ActiveProcesses = 1,
                    JobUserTime = TimeSpan.FromSeconds(2).Ticks
                },
                ProcessMemory = (UIntPtr)(256UL * 1024 * 1024)
            };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(job, process.Handle)) throw new Win32Exception();
            return job;
        }
        catch { job.Dispose(); throw; }
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessUserTime, JobUserTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcesses;
        public UIntPtr Affinity;
        public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SchemaWorkerJob CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SchemaWorkerJob job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SchemaWorkerJob job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
