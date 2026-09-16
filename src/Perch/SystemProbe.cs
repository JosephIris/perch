using System;
using System.Collections.Generic;
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
/// in about a millisecond, and NtQuerySystemInformation dates every process
/// in one more, which is all the ancestry walk needs. Command lines
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

        var want = new HashSet<int>();
        foreach (var l in listeners) if (l.Pid > 4) want.Add(l.Pid);
        if (want.Count == 0) return rows;

        // Every row gets a start time: the ancestry walk needs them for the
        // whole chain above a listener (an ancestor that started after its
        // child is a recycled pid, and without the times the walk can't tell),
        // and one kernel call dates the box, so there is nothing to save by
        // picking. Only listening processes need a command line (framework
        // detection) — that is the expensive field, and it stays filtered.
        var times = CreateTimes();
        var cmds = CommandLines(want);
        for (var i = 0; i < rows.Count; i++)
        {
            var pid = rows[i].Pid;
            times.TryGetValue(pid, out var start);
            var row = rows[i] with { StartMs = start };
            if (want.Contains(pid))
            {
                cmds.TryGetValue(pid, out var cmd);
                row = row with { Cmd = Scrub(cmd ?? "") };
            }
            rows[i] = row;
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

    private const int SystemProcessInformation = 5;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buf, int len, out int needed);

    /// pid → creation time (unix ms) for every process on the box, from one
    /// NtQuerySystemInformation call and no process handles. The handle route
    /// (OpenProcess + GetProcessTimes, which is also what Process.StartTime
    /// does) is refused for SYSTEM's services when Perch runs as a normal
    /// user — so it could date the pane's perch.exe but never the wininit that
    /// pointed at its recycled pid, which is the one comparison the pid-reuse
    /// check exists to make. This is the source Task Manager and .NET's own
    /// Process.GetProcesses read; the layout is the x64 SYSTEM_PROCESS_INFORMATION
    /// (offsets derived from IntPtr.Size so an x86 build reads it too). A
    /// missing pid, or a failed call, just leaves a time at 0 — the "up 4m"
    /// label omits it and the reuse check skips it.
    private static Dictionary<int, long> CreateTimes()
    {
        var map = new Dictionary<int, long>();
        var len = 512 * 1024;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            int status;
            while ((status = NtQuerySystemInformation(SystemProcessInformation, buf, len, out var needed))
                   == STATUS_INFO_LENGTH_MISMATCH)
            {
                len = Math.Max(needed + 64 * 1024, len * 2);
                Marshal.FreeHGlobal(buf);
                buf = Marshal.AllocHGlobal(len);
            }
            if (status != 0) { Log.Info($"SystemProbe.CreateTimes: NTSTATUS 0x{status:X8}"); return map; }

            // NextEntryOffset(4) NumberOfThreads(4) WorkingSetPrivateSize(8)
            // HardFaultCount(4) NumberOfThreadsHighWatermark(4) CycleTime(8)
            // CreateTime(8) UserTime(8) KernelTime(8) ImageName(UNICODE_STRING)
            // BasePriority(4, then pointer-aligned) UniqueProcessId(ptr) ...
            const int createOff = 32;
            var pidOff = Align(56 + 2 * IntPtr.Size + 4, IntPtr.Size);
            var p = buf;
            var end = IntPtr.Add(buf, len);
            while (IntPtr.Add(p, pidOff + IntPtr.Size).ToInt64() <= end.ToInt64())
            {
                var next = Marshal.ReadInt32(p, 0);
                var create = Marshal.ReadInt64(p, createOff);
                var pid = (int)Marshal.ReadIntPtr(p, pidOff).ToInt64();
                if (pid > 0 && create > 0)
                    map[pid] = DateTimeOffset.FromFileTime(create).ToUnixTimeMilliseconds();
                if (next <= 0) break;
                p = IntPtr.Add(p, next);
            }
        }
        catch (Exception ex) { Log.Error("SystemProbe.CreateTimes", ex); }
        finally { Marshal.FreeHGlobal(buf); }
        return map;
    }

    private static int Align(int v, int to) => (v + to - 1) / to * to;

    /// Command lines are arbitrary user text. One process launched with a raw
    /// BEL in its arguments used to poison the whole scan; the JSON hop that
    /// made that fatal is gone, but these strings still cross into the webview
    /// as JSON, so they get scrubbed at the boundary where they enter.
    private static string Scrub(string s) => LocalPoller.StripControlChars(s);
}
