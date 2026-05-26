using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CmuxWin;

/// Thin wrapper around Win32 ConPTY (`CreatePseudoConsole` &c. — Win10 1809+).
///
/// One instance owns:
///   - a pseudo-console handle
///   - two anonymous pipes (PTY-in for our writes; PTY-out for our reads)
///   - the spawned shell process attached to the PTY
///   - a background reader thread that fires <see cref="OutputReceived"/>
///     with raw bytes the shell wrote (xterm.js writes those verbatim)
///
/// The owning process for input is the caller; we expose Write(ReadOnlySpan)
/// and Resize(cols, rows). On Dispose we close the PTY (which terminates
/// the shell), CloseHandle the pipes, join the reader, and reap the proc.
///
/// Why ConPTY directly instead of System.Diagnostics.Process + redirected
/// stdio: redirected stdio loses TTY semantics (no PSReadLine, no alt-screen,
/// no color in some shells, no cursor positioning). ConPTY is what Windows
/// Terminal / VS Code's integrated terminal use; it's the right surface for
/// a real interactive shell.
internal sealed class ConPty : IDisposable
{
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;

    public int ProcessId { get; private set; }

    private IntPtr _hPC;
    private SafeFileHandle? _ptyInWrite;     // we write -> shell stdin
    private SafeFileHandle? _ptyOutRead;     // we read  <- shell stdout/stderr
    private FileStream? _outStream;
    private FileStream? _inStream;
    private Thread? _readerThread;
    private PROCESS_INFORMATION _proc;
    private bool _disposed;

    public static ConPty Start(string command, int cols, int rows, string? cwd = null)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        // Two pipes. Each Win32 pipe is a pair of handles. We end up keeping
        // the "outer" end for ourselves (read on PTY-out, write on PTY-in)
        // and hand the "inner" end to CreatePseudoConsole.
        //
        // Pipes MUST be inheritable. CreatePseudoConsole internally spawns
        // openconsole.exe (the helper that bridges the VT stream to a real
        // console buffer); when ConPTY duplicates our handles it preserves
        // their inheritability, and openconsole needs to inherit them.
        // Non-inheritable pipes here means cmd.exe / pwsh prints its banner
        // and immediately exits because its stdin pipe has no live writers
        // visible to the worker. (We hit exactly that symptom in stage 2:
        // 30ms exit, banner-sized output flushed on close.)
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
            lpSecurityDescriptor = IntPtr.Zero,
        };
        if (!CreatePipe(out var hPtyInRead, out var hPtyInWrite, ref sa, 0))
            throw new InvalidOperationException("CreatePipe (in) failed: " + Marshal.GetLastWin32Error());
        if (!CreatePipe(out var hPtyOutRead, out var hPtyOutWrite, ref sa, 0))
        {
            CloseHandle(hPtyInRead); CloseHandle(hPtyInWrite);
            throw new InvalidOperationException("CreatePipe (out) failed: " + Marshal.GetLastWin32Error());
        }

        // Create the pseudo-console with our two handles.
        var size = new COORD { X = (short)cols, Y = (short)rows };
        int hr = CreatePseudoConsole(size, hPtyInRead, hPtyOutWrite, 0, out var hPC);
        // CreatePseudoConsole duplicates the inner handles internally; close
        // ours so the only references left are inside the PTY.
        CloseHandle(hPtyInRead);
        CloseHandle(hPtyOutWrite);
        if (hr != 0)
        {
            CloseHandle(hPtyInWrite); CloseHandle(hPtyOutRead);
            throw new InvalidOperationException($"CreatePseudoConsole failed: 0x{hr:X8}");
        }

        // STARTUPINFOEX attribute list with PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE.
        var si = new STARTUPINFOEX();
        si.StartupInfo.cb = (uint)Marshal.SizeOf<STARTUPINFOEX>();
        IntPtr attrList = IntPtr.Zero;
        IntPtr listSize = IntPtr.Zero;
        try
        {
            // First call: get required size.
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
            attrList = Marshal.AllocHGlobal(listSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref listSize))
                throw new InvalidOperationException("InitializeProcThreadAttributeList failed: " + Marshal.GetLastWin32Error());

            if (!UpdateProcThreadAttribute(
                    attrList, 0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("UpdateProcThreadAttribute failed: " + Marshal.GetLastWin32Error());

            si.lpAttributeList = attrList;

            // CreateProcess with EXTENDED_STARTUPINFO_PRESENT.
            var pi = new PROCESS_INFORMATION();
            // CreateProcess wants a mutable command line.
            var cmdLine = new StringBuilder(command);
            if (!CreateProcess(
                    null, cmdLine,
                    IntPtr.Zero, IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    cwd,
                    ref si,
                    out pi))
            {
                throw new InvalidOperationException($"CreateProcess('{command}') failed: " + Marshal.GetLastWin32Error());
            }

            var inst = new ConPty
            {
                _hPC = hPC,
                _ptyInWrite = new SafeFileHandle(hPtyInWrite, ownsHandle: true),
                _ptyOutRead = new SafeFileHandle(hPtyOutRead, ownsHandle: true),
                _proc = pi,
                ProcessId = (int)pi.dwProcessId,
            };
            inst._inStream = new FileStream(inst._ptyInWrite, FileAccess.Write, 4096, isAsync: false);
            inst._outStream = new FileStream(inst._ptyOutRead, FileAccess.Read, 4096, isAsync: false);
            inst.StartReader();
            return inst;
        }
        catch
        {
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
            CloseHandle(hPtyInWrite); CloseHandle(hPtyOutRead);
            if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
            throw;
        }
        finally
        {
            // The attribute list can be freed once CreateProcess returns
            // (Windows copies what it needs).
            if (attrList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrList);
                Marshal.FreeHGlobal(attrList);
            }
        }
    }

    private void StartReader()
    {
        _readerThread = new Thread(ReaderLoop)
        {
            IsBackground = true,
            Name = $"ConPty.Reader[{ProcessId}]",
        };
        _readerThread.Start();
    }

    private void ReaderLoop()
    {
        var buf = new byte[8192];
        try
        {
            while (!_disposed)
            {
                int n;
                try { n = _outStream!.Read(buf, 0, buf.Length); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (n <= 0) break;
                var copy = new byte[n];
                Buffer.BlockCopy(buf, 0, copy, 0, n);
                try { OutputReceived?.Invoke(this, copy); }
                catch (Exception ex) { Log.Error("ConPty.Output.handler", ex); }
            }
        }
        catch (Exception ex) { Log.Error("ConPty.Reader", ex); }
        finally
        {
            int code = -1;
            try
            {
                if (WaitForSingleObject(_proc.hProcess, 100) == 0 &&
                    GetExitCodeProcess(_proc.hProcess, out var c)) code = (int)c;
            }
            catch { }
            try { Exited?.Invoke(this, code); } catch { }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed || _inStream == null) return;
        // FileStream.Write(ReadOnlySpan) is supported on .NET 8.
        _inStream.Write(data);
        _inStream.Flush();
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        if (cols < 1) cols = 1;
        if (rows < 1) rows = 1;
        var size = new COORD { X = (short)cols, Y = (short)rows };
        int hr = ResizePseudoConsole(_hPC, size);
        if (hr != 0) Log.Error("ConPty.Resize", new InvalidOperationException($"hr=0x{hr:X8}"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // ClosePseudoConsole terminates the attached process and signals
        // EOF on the pipes, which unblocks ReaderLoop.
        try { if (_hPC != IntPtr.Zero) ClosePseudoConsole(_hPC); } catch { }
        _hPC = IntPtr.Zero;
        try { _inStream?.Dispose(); }  catch { }
        try { _outStream?.Dispose(); } catch { }
        try { _ptyInWrite?.Dispose(); } catch { }
        try { _ptyOutRead?.Dispose(); } catch { }
        // Reader thread should exit on its own once the pipes are closed;
        // give it a brief grace period.
        try { _readerThread?.Join(500); } catch { }
        try { if (_proc.hThread  != IntPtr.Zero) CloseHandle(_proc.hThread);  } catch { }
        try { if (_proc.hProcess != IntPtr.Zero) CloseHandle(_proc.hProcess); } catch { }
    }

    // -------------------- Native ----------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public uint cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public uint dwProcessId, dwThreadId;
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public uint   nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, uint dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
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
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);
}
