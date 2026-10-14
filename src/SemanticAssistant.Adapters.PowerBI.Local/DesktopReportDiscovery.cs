using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SemanticAssistant.Adapters.PowerBI.Local;

public sealed record DesktopReportCandidate(DesktopInstance Instance, string? ReportName, int? DesktopProcessId)
{
    public string DisplayName => ReportName ?? "未辨識報告名稱";
}

// Resolve display names from the engine's process ancestry, never from an
// arbitrary nearby window. No TOM connection, catalog or PBIX read is involved.
public static class DesktopReportDiscovery
{
    public static ImmutableArray<DesktopReportCandidate> Describe(ImmutableArray<DesktopInstance> instances)
    {
        if (instances.IsEmpty) return [];
        var parents = ReadProcessParents();
        return instances.Select(instance => Describe(instance, parents)).ToImmutableArray();
    }

    private static DesktopReportCandidate Describe(DesktopInstance instance, Dictionary<int, int> parents)
    {
        try
        {
            using var engine = Process.GetProcessById(instance.Owner.ProcessId);
            if (engine.StartTime.ToUniversalTime().Ticks != instance.Owner.StartedUtcTicks)
                return new(instance, null, null);
            var childStarted = engine.StartTime.ToUniversalTime();
            var pid = engine.Id;
            var visited = new HashSet<int> { pid };
            for (var depth = 0; depth < 8 && parents.TryGetValue(pid, out var parentId) && parentId > 0; depth++)
            {
                if (!visited.Add(parentId)) break;
                using var parent = Process.GetProcessById(parentId);
                var parentStarted = parent.StartTime.ToUniversalTime();
                if (parentStarted > childStarted) break; // Parent PID has been reused.
                if (parent.ProcessName.Equals("PBIDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    var name = ReportNameFromTitle(parent.MainWindowTitle);
                    engine.Refresh();
                    parent.Refresh();
                    if (engine.HasExited || parent.HasExited) return new(instance, null, null);
                    return new(instance, name, parent.Id);
                }
                pid = parentId;
                childStarted = parentStarted;
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        return new(instance, null, null);
    }

    public static string? ReportNameFromTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 512 || title.Any(char.IsControl)) return null;
        var text = title.Trim();
        foreach (var suffix in new[] { " - Microsoft Power BI Desktop", " - Power BI Desktop", " – Power BI Desktop", " — Power BI Desktop" })
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            { text = text[..^suffix.Length].Trim(); break; }
        text = text.TrimEnd('*').TrimEnd();
        return text.Length == 0 || text.Equals("Power BI Desktop", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Microsoft Power BI Desktop", StringComparison.OrdinalIgnoreCase) ? null : text;
    }

    private static Dictionary<int, int> ReadProcessParents()
    {
        var result = new Dictionary<int, int>();
        if (!OperatingSystem.IsWindows()) return result;
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Executable = "" };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                result[(int)entry.ProcessId] = (int)entry.ParentProcessId;
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally { CloseHandle(snapshot); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Executable;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
