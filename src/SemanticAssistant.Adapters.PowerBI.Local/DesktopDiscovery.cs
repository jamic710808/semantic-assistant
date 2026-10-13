using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SemanticAssistant.Adapters.PowerBI.Local;

public sealed record LocalPortOwner(int Port, int ProcessId, long StartedUtcTicks);
public sealed record DesktopInstance(string Workspace, LocalPortOwner Owner);
public interface ILocalPortInventory { ImmutableArray<LocalPortOwner> ReadOwners(); }

public sealed class WindowsLocalPortInventory : ILocalPortInventory
{
    public ImmutableArray<LocalPortOwner> ReadOwners()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        uint size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
        if (status != 122 || size > 4_000_000) throw new IOException("無法列舉本機服務埠。");
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, 2, 3, 0) != 0) throw new IOException("服務埠在列舉期間改變。");
            var count = Marshal.ReadInt32(buffer);
            if (count < 0 || 4L + count * Marshal.SizeOf<Row>() > size) throw new IOException("服務埠表無效。");
            var owners = ImmutableArray.CreateBuilder<LocalPortOwner>();
            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<Row>(buffer + 4 + i * Marshal.SizeOf<Row>());
                if (row.LocalAddress is not (0 or 0x0100007f)) continue;
                try
                {
                    using var process = Process.GetProcessById((int)row.ProcessId);
                    if (!process.ProcessName.Equals("msmdsrv", StringComparison.OrdinalIgnoreCase)) continue;
                    var bytes = BitConverter.GetBytes(row.LocalPort);
                    owners.Add(new((bytes[0] << 8) | bytes[1], process.Id, process.StartTime.ToUniversalTime().Ticks));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            return owners.ToImmutable();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Row
    { public uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort, ProcessId; }
    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref uint size,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
}

public sealed class DesktopDiscovery(ILocalPortInventory inventory)
{
    public ImmutableArray<DesktopInstance> Discover(IEnumerable<string> workspaceRoots)
    {
        var owners = inventory.ReadOwners();
        var instances = ImmutableArray.CreateBuilder<DesktopInstance>();
        foreach (var root in workspaceRoots)
        {
            if (!Directory.Exists(root) || IsLink(root)) continue;
            var directories = Directory.EnumerateDirectories(root, "AnalysisServicesWorkspace*").Take(129).ToArray();
            if (directories.Length > 128) throw new IOException("工作區目錄超過上限。");
            foreach (var directory in directories)
            {
                var data = Path.Combine(directory, "Data"); var file = Path.Combine(data, "msmdsrv.port.txt");
                if (IsLink(directory) || !Directory.Exists(data) || IsLink(data) || !File.Exists(file) || IsLink(file)) continue;
                try
                {
                    if (new FileInfo(file).Length > 128) continue;
                    var bytes = File.ReadAllBytes(file);
                    if (bytes.Length > 128) continue;
                    var value = (bytes.Contains((byte)0) ? Encoding.Unicode : Encoding.UTF8).GetString(bytes).Trim('\uFEFF', '\0', ' ', '\r', '\n');
                    if (!int.TryParse(value, out var port) || port is < 1 or > 65535) continue;
                    var matching = owners.Where(o => o.Port == port).ToArray();
                    if (matching.Length == 1) instances.Add(new(Path.GetFullPath(directory), matching[0]));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        return instances.Distinct().ToImmutableArray();
    }

    public static string[] DefaultRoots()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return [Path.Combine(local, "Microsoft", "Power BI Desktop", "AnalysisServicesWorkspaces"),
            Path.Combine(local, "Microsoft", "Power BI Desktop Store App", "AnalysisServicesWorkspaces")];
    }

    public void EnsureCurrent(DesktopInstance instance)
    {
        if (!inventory.ReadOwners().Contains(instance.Owner) || !Discover([Path.GetDirectoryName(instance.Workspace)!]).Contains(instance))
            throw new IOException("Desktop 已關閉或埠／PID／啟動時間已改變；請重新列舉。");
    }
    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
