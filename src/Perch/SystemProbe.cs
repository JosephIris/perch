using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Perch;

/// The real thing: iphlpapi for listeners, Toolhelp32 for the process tree,
/// WMI for command lines.
///
/// This replaced a `powershell.exe -EncodedCommand` shell-out that ran
/// Get-NetTCPConnection + Get-CimInstance Win32_Process every 3 seconds while
/// the panel was open. That subprocess cost ~200-300ms of CPU on startup alone,
/// before doing any work, and it made the localhost panel the single most
/// expensive spawn in the app. Everything here is an in-process call.
///
/// The split matters for cost. Toolhelp32 gives pid/ppid/name for every process
/// in about a millisecond, which is all the ancestry walk needs. Command lines
/// are the expensive field (WMI), and only the LISTENING process's command line
/// is ever read — Describe() never looks at an ancestor's — so the WMI query is
/// filtered to that handful of pids instead of enumerating the box.
internal sealed class WindowsSystemProbe : ISystemProbe
{
    // Loopback + wildcard only. A server on 0.0.0.0 is reachable at localhost so
    // it counts; one bound to a specific LAN NIC is not a "localhost dev server"
    // and is deliberately excluded. Same rule the PowerShell script used.
    private static readonly HashSet<string> Loopback =
        new(StringComparer.Ordinal) { "127.0.0.1", "::1", "0.0.0.0", "::" };

    public (IReadOnlyList<RawListener>, IReadOnlyList<RawProc>) Probe()
    {
        var listeners = new List<RawListener>();
        try { listeners.AddRange(Listeners()); }
        catch (Exception ex) { Log.Error("SystemProbe.Listeners", ex); }

        var procs = new List<RawProc>();
        try { procs.AddRange(Processes(listeners)); }
        catch (Exception ex) { Log.Error("SystemProbe.Processes", ex); }

        return (listeners, procs);
    }

    // ---- listeners (iphlpapi) ----------------------------------------------

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_LISTENER = 3;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable, ref int dwOutBufLen, bool sort, int ipVersion, int tblClass, int reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcp6RowOwnerPid
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] LocalAddr;
        public uint LocalScopeId, LocalPort;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] RemoteAddr;
        public uint RemoteScopeId, RemotePort, State, OwningPid;
    }

    private static IEnumerable<RawListener> Listeners()
    {
        foreach (var r in Table(AF_INET)) yield return r;
        foreach (var r in Table(AF_INET6)) yield return r;
    }

    private static List<RawListener> Table(int family)
    {
        var rows = new List<RawListener>();
        var len = 0;
        // First call sizes the buffer; 122 is ERROR_INSUFFICIENT_BUFFER, which is
        // the expected outcome, not a failure.
        GetExtendedTcpTable(IntPtr.Zero, ref len, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (len <= 0) return rows;

        var buf = Marshal.AllocHGlobal(len);
        try
        {
            if (GetExtendedTcpTable(buf, ref len, false, family, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return rows;

            var count = Marshal.ReadInt32(buf);
            var p = IntPtr.Add(buf, 4);
            var size = family == AF_INET
                ? Marshal.SizeOf<MibTcpRowOwnerPid>()
                : Marshal.SizeOf<MibTcp6RowOwnerPid>();

            for (var i = 0; i < count; i++, p = IntPtr.Add(p, size))
            {
                string addr;
                uint port, pid;
                if (family == AF_INET)
                {
                    var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(p);
                    addr = Ipv4(row.LocalAddr);
                    port = row.LocalPort;
                    pid = row.OwningPid;
                }
                else
                {
                    var row = Marshal.PtrToStructure<MibTcp6RowOwnerPid>(p);
                    addr = Ipv6(row.LocalAddr);
                    port = row.LocalPort;
                    pid = row.OwningPid;
                }
                if (!Loopback.Contains(addr)) continue;
                rows.Add(new RawListener(NetworkPort(port), (int)pid, addr));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return rows;
    }

    /// dwLocalPort carries the port in network byte order in its low 16 bits.
    private static int NetworkPort(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    private static string Ipv4(uint a)
        => $"{a & 0xFF}.{(a >> 8) & 0xFF}.{(a >> 16) & 0xFF}.{(a >> 24) & 0xFF}";

    /// Only the two forms we keep need to round-trip exactly; anything else is
    /// filtered out by the Loopback set, so a coarse rendering is fine.
    private static string Ipv6(byte[] a)
    {
        if (a == null || a.Length != 16) return "";
        var allZero = true;
        for (var i = 0; i < 16; i++) if (a[i] != 0) { allZero = false; break; }
        if (allZero) return "::";
        var loop = a[15] == 1;
        if (loop) for (var i = 0; i < 15; i++) if (a[i] != 0) { loop = false; break; }
        return loop ? "::1" : "other";
    }

    // ---- processes (Toolhelp32 + WMI for the few command lines we need) -----

    private const uint TH32CS_SNAPPROCESS = 2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snap, ref ProcessEntry32 e);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    private static List<RawProc> Processes(IReadOnlyList<RawListener> listeners)
    {
        var rows = new List<RawProc>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return rows;
        try
        {
            var e = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            for (var ok = Process32FirstW(snap, ref e); ok; ok = Process32NextW(snap, ref e))
                rows.Add(new RawProc((int)e.th32ProcessID, (int)e.th32ParentProcessID,
                                     Scrub(e.szExeFile ?? ""), "", 0));
        }
        finally { CloseHandle(snap); }

        // Only listening processes need a command line (framework detection) and
        // a start time (the "up 4m" label). Ancestors are walked for ppid alone,
        // so enriching them would be pure cost.
        var want = new HashSet<int>();
        foreach (var l in listeners) if (l.Pid > 4) want.Add(l.Pid);
        if (want.Count == 0) return rows;

        var cmds = CommandLines(want);
        for (var i = 0; i < rows.Count; i++)
        {
            if (!want.Contains(rows[i].Pid)) continue;
            cmds.TryGetValue(rows[i].Pid, out var cmd);
            rows[i] = rows[i] with { Cmd = Scrub(cmd ?? ""), StartMs = StartMs(rows[i].Pid) };
        }
        return rows;
    }

    /// WMI, filtered to the pids that matter. A `WHERE ProcessId = a OR ...`
    /// over a handful of pids is orders of magnitude cheaper than enumerating
    /// Win32_Process, which is what the old script did on every scan.
    private static Dictionary<int, string> CommandLines(HashSet<int> pids)
    {
        var map = new Dictionary<int, string>();
        try
        {
            var where = new StringBuilder();
            foreach (var pid in pids)
            {
                if (where.Length > 0) where.Append(" OR ");
                where.Append("ProcessId=").Append(pid);
            }
            var q = $"SELECT ProcessId, CommandLine FROM Win32_Process WHERE {where}";
            using var searcher = new System.Management.ManagementObjectSearcher(q);
            foreach (System.Management.ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var pid = Convert.ToInt32(mo["ProcessId"]);
                    map[pid] = mo["CommandLine"] as string ?? "";
                }
            }
        }
        catch (Exception ex) { Log.Error("SystemProbe.CommandLines", ex); }
        return map;
    }

    private static long StartMs(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return new DateTimeOffset(p.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        }
        catch { return 0; }   // protected or already gone — the label just omits it
    }

    /// Command lines are arbitrary user text. One process launched with a raw
    /// BEL in its arguments used to poison the whole scan; the JSON hop that
    /// made that fatal is gone, but these strings still cross into the webview
    /// as JSON, so they get scrubbed at the boundary where they enter.
    private static string Scrub(string s) => LocalPoller.StripControlChars(s);
}
