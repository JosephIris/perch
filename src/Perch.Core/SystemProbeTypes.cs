using System.Collections.Generic;

namespace Perch;

/// One TCP listener as the kernel reports it, before attribution.
internal sealed record RawListener(int Port, int Pid, string Addr);

/// One process row: enough to walk ancestry and describe a dev server.
internal sealed record RawProc(int Pid, int Ppid, string Name, string Cmd, long StartMs);

/// Where the localhost panel's raw facts come from.
///
/// Behind an interface purely so the attribution logic can be tested against
/// fixed input — the real implementations talk to the kernel and there is
/// nothing to assert about a live machine. Windows: iphlpapi + Toolhelp32 +
/// WMI (WindowsSystemProbe). macOS: lsof + ps (MacSystemProbe).
internal interface ISystemProbe
{
    (IReadOnlyList<RawListener> Listeners, IReadOnlyList<RawProc> Procs) Probe();
}
