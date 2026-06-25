// Minimal ConPTY probe. Runs the exact same Win32 sequence ConPty.Start
// does, spawns the given exe (default cmd.exe), waits 2.5s, prints whether
// the shell is still alive and how many bytes it wrote in that window.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using static Native;

string exe = args.Length > 0 ? args[0] : "cmd.exe";
int waitMs = args.Length > 1 && int.TryParse(args[1], out var w) ? w : 2500;

// Detach from our parent console BEFORE spawning. Perch is a WPF app with
// no inherited console, so cmd.exe attaches to the pseudoconsole. If the
// probe stays attached to its parent console (bash/cmd), cmd happily ignores
// the pseudoconsole and inherits the parent's console instead -- masking
// the bug we're trying to reproduce. We capture all output to a log file
// before detaching since Console.WriteLine wouldn't survive FreeConsole.
var logPath = Path.Combine(AppContext.BaseDirectory, "probe.log");
var log = new StreamWriter(logPath, append: false) { AutoFlush = true };
void Say(string s) { log.WriteLine(s); }

Say($"probe: target={exe}  waitMs={waitMs}");
Console.WriteLine($"probe: target={exe}  waitMs={waitMs}  log={logPath}");
FreeConsole();

var sa = new SECURITY_ATTRIBUTES
{
    nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
    bInheritHandle = true,
    lpSecurityDescriptor = IntPtr.Zero,
};
if (!CreatePipe(out var hPtyInRead, out var hPtyInWrite, ref sa, 0)) Fail("CreatePipe(in)");
if (!CreatePipe(out var hPtyOutRead, out var hPtyOutWrite, ref sa, 0)) Fail("CreatePipe(out)");

var size = new COORD { X = 80, Y = 24 };
int hr = CreatePseudoConsole(size, hPtyInRead, hPtyOutWrite, 0, out var hPC);
Say($"  CreatePseudoConsole hr=0x{hr:X8}");
CloseHandle(hPtyInRead);
CloseHandle(hPtyOutWrite);
if (hr != 0) Fail("CreatePseudoConsole");

var si = new STARTUPINFOEX();
si.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEX>();
IntPtr listSize = IntPtr.Zero;
InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
var attrList = Marshal.AllocHGlobal(listSize);
if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize)) Fail("InitializeProcThreadAttributeList");
var PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;
if (!UpdateProcThreadAttribute(attrList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
    Fail("UpdateProcThreadAttribute");
si.lpAttributeList = attrList;

var cmdLine = new StringBuilder(exe);
var pi = new PROCESS_INFORMATION();
const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
if (!CreateProcess(null, cmdLine, IntPtr.Zero, IntPtr.Zero, false,
        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, null, ref si, out pi))
    Fail("CreateProcess");

Say($"  CreateProcess pid={pi.dwProcessId}");

long bytesRead = 0;
long firstBytesAt = -1;
var ptyOutReadSafe = new SafeFileHandle(hPtyOutRead, ownsHandle: true);
var outStream = new FileStream(ptyOutReadSafe, FileAccess.Read, 4096, isAsync: false);
var stopwatch = System.Diagnostics.Stopwatch.StartNew();
var reader = new Thread(() =>
{
    var buf = new byte[8192];
    try
    {
        while (true)
        {
            int n;
            try { n = outStream.Read(buf, 0, buf.Length); }
            catch { break; }
            if (n <= 0) break;
            if (Interlocked.Read(ref firstBytesAt) < 0) Interlocked.Exchange(ref firstBytesAt, stopwatch.ElapsedMilliseconds);
            Interlocked.Add(ref bytesRead, n);
        }
    }
    catch { }
});
reader.IsBackground = true;
reader.Start();

Thread.Sleep(waitMs);

uint wait = WaitForSingleObject(pi.hProcess, 0);
bool alive = wait != 0; // 0 = signaled (exited)
GetExitCodeProcess(pi.hProcess, out uint exitCode);
Say($"  after {waitMs}ms: alive={alive}  exitCode={exitCode}  bytesRead={bytesRead}  firstBytesAt={firstBytesAt}ms");

try { ClosePseudoConsole(hPC); } catch { }
try { CloseHandle(hPtyInWrite); } catch { }
try { outStream.Dispose(); } catch { }
DeleteProcThreadAttributeList(attrList);
Marshal.FreeHGlobal(attrList);

return alive ? 0 : 1;

static void Fail(string what)
{
    var msg = $"FAIL {what}: Win32={Marshal.GetLastWin32Error()}";
    try { Console.Error.WriteLine(msg); } catch { }
    try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "probe.log"), msg + "\n"); } catch { }
    Environment.Exit(2);
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
static extern bool FreeConsole();

// ---------- P/Invoke -----------------------------------------------------

[StructLayout(LayoutKind.Sequential)]
struct COORD { public short X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct STARTUPINFO
{
    public uint cb;
    public IntPtr lpReserved, lpDesktop, lpTitle;
    public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public ushort wShowWindow, cbReserved2;
    public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
}

[StructLayout(LayoutKind.Sequential)]
struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

[StructLayout(LayoutKind.Sequential)]
struct PROCESS_INFORMATION
{
    public IntPtr hProcess, hThread;
    public uint dwProcessId, dwThreadId;
}

[StructLayout(LayoutKind.Sequential)]
struct SECURITY_ATTRIBUTES
{
    public uint nLength;
    public IntPtr lpSecurityDescriptor;
    [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
}

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcess(
        string? lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetExitCodeProcess(IntPtr hProcess, out uint exitCode);
}
