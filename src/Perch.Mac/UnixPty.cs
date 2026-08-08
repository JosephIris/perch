using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Perch;

/// The macOS pty backend: openpty + posix_spawn. Mirrors ConPty's surface and
/// flow-control contract exactly (same high/low water marks, same
/// resize-dedupe, same exactly-once Exited) — see ConPty.cs for the WHY of
/// each; only the mechanics differ here.
///
/// The child is spawned through `/bin/sh -c <command>` with POSIX_SPAWN_SETSID
/// and the pty slave re-opened as fd 0 post-setsid, which makes the pty its
/// controlling terminal (the first tty a new session opens). The child is its
/// own session leader, so its pid doubles as the process-group id — that group
/// is the kill target on teardown and the membership scope for dev-server
/// attribution (IProcScope).
internal sealed class UnixPty : IPty
{
    public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived;
    public event EventHandler<int>? Exited;

    public int ProcessId { get; private set; }
    public IProcScope? Scope { get; private set; }

    private static readonly bool FlowControlEnabled =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PERCH_DISABLE_FLOW_CONTROL"));
    private const long HighWaterBytes = 256 * 1024;
    private const long LowWaterBytes = 64 * 1024;
    private readonly ManualResetEventSlim _readGate = new(initialState: true);
    private long _outstanding;
    private long _maxOutstanding;

    public long MaxOutstanding => Interlocked.Read(ref _maxOutstanding);

    private int _masterFd = -1;
    private FileStream? _stream;
    private Thread? _readerThread;
    private Thread? _waitThread;
    private bool _disposed;
    private int _exitedFired;
    private int _lastCols;
    private int _lastRows;

    public static UnixPty Start(string command, int cols, int rows, string? cwd = null)
    {
        if (cols < 1) cols = 80;
        if (rows < 1) rows = 24;

        var ws = new Libc.WinSize { ws_row = (ushort)rows, ws_col = (ushort)cols };
        if (Libc.openpty(out var master, out var slave, IntPtr.Zero, IntPtr.Zero, ref ws) != 0)
            throw new IOException($"openpty failed (errno={Marshal.GetLastWin32Error()})");

        var slavePath = Marshal.PtrToStringAnsi(Libc.ptsname(master))
            ?? throw new IOException("ptsname returned null");

        int pid;
        IntPtr fa = IntPtr.Zero, attr = IntPtr.Zero;
        try
        {
            Libc.Check(Libc.posix_spawn_file_actions_init(out fa), "fa_init");
            // Re-open the slave in the child AFTER setsid so it becomes the
            // controlling terminal, then mirror it onto stdout/stderr.
            Libc.Check(Libc.posix_spawn_file_actions_addopen(ref fa, 0, slavePath, Libc.O_RDWR, 0), "addopen");
            Libc.Check(Libc.posix_spawn_file_actions_adddup2(ref fa, 0, 1), "dup2.out");
            Libc.Check(Libc.posix_spawn_file_actions_adddup2(ref fa, 0, 2), "dup2.err");
            Libc.Check(Libc.posix_spawn_file_actions_addclose(ref fa, master), "close.master");
            Libc.Check(Libc.posix_spawn_file_actions_addclose(ref fa, slave), "close.slave");
            if (!string.IsNullOrEmpty(cwd))
                Libc.Check(Libc.posix_spawn_file_actions_addchdir_np(ref fa, cwd), "chdir");

            Libc.Check(Libc.posix_spawnattr_init(out attr), "attr_init");
            Libc.Check(Libc.posix_spawnattr_setflags(ref attr, Libc.POSIX_SPAWN_SETSID), "setflags");

            string?[] argv = { "/bin/sh", "-c", command, null };
            var envp = BuildEnv();
            var rc = Libc.posix_spawn(out pid, "/bin/sh", ref fa, ref attr, argv, envp);
            if (rc != 0) throw new IOException($"posix_spawn failed (rc={rc}) for: {command}");
        }
        catch
        {
            Libc.close(master);
            Libc.close(slave);
            throw;
        }
        finally
        {
            if (attr != IntPtr.Zero) Libc.posix_spawnattr_destroy(ref attr);
            if (fa != IntPtr.Zero) Libc.posix_spawn_file_actions_destroy(ref fa);
        }
        Libc.close(slave);

        var pty = new UnixPty
        {
            ProcessId = pid,
            Scope = new ProcessSessionScope(pid),
            _masterFd = master,
            _lastCols = cols,
            _lastRows = rows,
        };
        pty._stream = new FileStream(
            new SafeFileHandle((IntPtr)master, ownsHandle: false), FileAccess.ReadWrite);
        pty._readerThread = new Thread(pty.ReaderLoop) { IsBackground = true, Name = $"pty-read-{pid}" };
        pty._readerThread.Start();
        pty._waitThread = new Thread(pty.WaitLoop) { IsBackground = true, Name = $"pty-wait-{pid}" };
        pty._waitThread.Start();
        PtyOrphans.Record(pid);
        return pty;
    }


    /// Extra per-host environment for every pane child (ZDOTDIR wrapper,
    /// PERCH_TOOLS_DIR). Assigned once at startup by Program.
    public static readonly System.Collections.Generic.Dictionary<string, string> ExtraEnv = new();

    /// The child inherits our environment (PATH already carries the tools dir
    /// — see Program) plus terminal identity. TERM_PROGRAM=Apple_Terminal is
    /// load-bearing: macOS's stock /etc/zshrc emits OSC 7 cwd reports for
    /// Terminal.app, which is exactly the signal pane.cwd tracking wants.
    private static string?[] BuildEnv()
    {
        var env = new System.Collections.Generic.List<string?>();
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var k = (string)e.Key;
            if (k is "TERM" or "TERM_PROGRAM" or "TERM_PROGRAM_VERSION") continue;
            if (ExtraEnv.ContainsKey(k)) continue;
            env.Add($"{k}={e.Value}");
        }
        foreach (var kv in ExtraEnv) env.Add($"{kv.Key}={kv.Value}");
        env.Add("TERM=xterm-256color");
        env.Add("COLORTERM=truecolor");
        env.Add("TERM_PROGRAM=Apple_Terminal");
        env.Add("TERM_PROGRAM_VERSION=460");
        env.Add(null);
        return env.ToArray();
    }

    private void ReaderLoop()
    {
        var buf = new byte[8192];
        try
        {
            while (!_disposed)
            {
                if (FlowControlEnabled) _readGate.Wait();
                if (_disposed) break;

                int n;
                try { n = _stream!.Read(buf, 0, buf.Length); }
                catch (IOException) { break; }   // EIO: last slave fd closed
                if (n <= 0) break;

                var cur = Interlocked.Add(ref _outstanding, n);
                if (cur > _maxOutstanding) _maxOutstanding = cur; // single writer
                if (FlowControlEnabled && cur >= HighWaterBytes) _readGate.Reset();

                var copy = new byte[n];
                Array.Copy(buf, copy, n);
                OutputReceived?.Invoke(this, copy);
            }
        }
        catch (Exception ex) { if (!_disposed) Log.Error("UnixPty.reader", ex); }
    }

    private void WaitLoop()
    {
        int status = 0;
        int rc;
        do { rc = Libc.waitpid(ProcessId, out status, 0); }
        while (rc < 0 && Marshal.GetLastWin32Error() == Libc.EINTR);
        var code = Libc.WIFEXITED(status) ? Libc.WEXITSTATUS(status)
                 : Libc.WIFSIGNALED(status) ? 128 + Libc.WTERMSIG(status)
                 : -1;
        RaiseExited(code);
    }

    private void RaiseExited(int code)
    {
        if (Interlocked.Exchange(ref _exitedFired, 1) != 0) return;
        Exited?.Invoke(this, code);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (_disposed) return;
        try
        {
            _stream!.Write(bytes);
            _stream.Flush();
        }
        catch (IOException ex) { Log.Error("UnixPty.write", ex); }
    }

    public void Ack(long bytes)
    {
        if (bytes <= 0) return;
        var cur = Interlocked.Add(ref _outstanding, -bytes);
        if (cur < 0) { Interlocked.Exchange(ref _outstanding, 0); cur = 0; }
        if (FlowControlEnabled && cur < LowWaterBytes) _readGate.Set();
    }

    public void Resize(int cols, int rows)
    {
        if (_disposed || cols < 1 || rows < 1) return;
        // Same size again is not a no-op downstream: TIOCSWINSZ fires SIGWINCH
        // regardless, the TUI redraws, and the idle watchdog reads the redraw
        // as agent activity. See ConPty's _lastCols note.
        if (cols == _lastCols && rows == _lastRows) return;
        _lastCols = cols; _lastRows = rows;

        var ws = new Libc.WinSize { ws_row = (ushort)rows, ws_col = (ushort)cols };
        if (Libc.IoctlWinSize(_masterFd, Libc.TIOCSWINSZ, ref ws) != 0)
            Log.Error("UnixPty.resize", new IOException($"TIOCSWINSZ errno={Marshal.GetLastWin32Error()}"));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // HUP the whole session's process group (child is session leader, so
        // -pid addresses its group), then close the master — either alone
        // usually suffices; together they cover a child that detached from
        // the tty. SIGKILL after a grace period catches HUP-ignorers.
        var pid = ProcessId;
        try { Libc.kill(-pid, Libc.SIGHUP); } catch { }
        PtyOrphans.Forget(pid);
        _readGate.Set();
        try { _stream?.Dispose(); } catch { }
        if (_masterFd >= 0) { Libc.close(_masterFd); _masterFd = -1; }
        _readerThread?.Join(500);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(2000);
            try { Libc.kill(-pid, Libc.SIGKILL); } catch { }
        });
        _readGate.Dispose();
    }
}

internal sealed class UnixPtyFactory : IPtyFactory
{
    public IPty Start(string command, int cols, int rows, string? cwd) =>
        UnixPty.Start(command, cols, rows, cwd);
}

/// "Is this pid one of the pane's processes?" — the macOS stand-in for the
/// Windows job object. Uses the kernel SESSION id (the pane child is a
/// session leader via POSIX_SPAWN_SETSID): every descendant inherits it,
/// shells' per-job process groups stay inside it, and it survives
/// intermediate parents exiting — the same property the job object gave us.
internal sealed class ProcessSessionScope : IProcScope
{
    private readonly int _sid;
    public ProcessSessionScope(int sid) => _sid = sid;
    public bool ContainsPid(int pid)
    {
        var s = Libc.getsid(pid);
        return s > 0 && s == _sid;
    }
}

/// libSystem interop for the pty. The ioctl shim is arch-aware: Apple arm64
/// passes variadic args on the stack, so the winsize pointer must be pushed
/// past the eight named-arg registers (six IntPtr pads) to land where
/// ioctl's va_arg reads it. On x64 the plain 3-arg form is correct.
internal static class Libc
{
    private const string Lib = "libSystem";

    public const int O_RDWR = 2;
    public const short POSIX_SPAWN_SETSID = 0x0400;
    public const ulong TIOCSWINSZ = 0x80087467;
    public const int SIGHUP = 1;
    public const int SIGKILL = 9;
    public const int EINTR = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct WinSize { public ushort ws_row, ws_col, ws_xpixel, ws_ypixel; }

    [DllImport(Lib, SetLastError = true)]
    public static extern int openpty(out int master, out int slave, IntPtr name, IntPtr termios, ref WinSize winsize);

    [DllImport(Lib, SetLastError = true)]
    public static extern IntPtr ptsname(int fd);

    [DllImport(Lib, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Lib, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_x64(int fd, ulong request, ref WinSize ws);

    [DllImport(Lib, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int ioctl_arm64(int fd, ulong request,
        IntPtr p1, IntPtr p2, IntPtr p3, IntPtr p4, IntPtr p5, IntPtr p6, ref WinSize ws);

    public static int IoctlWinSize(int fd, ulong request, ref WinSize ws) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? ioctl_arm64(fd, request, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref ws)
            : ioctl_x64(fd, request, ref ws);

    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn_file_actions_init(out IntPtr actions);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn_file_actions_destroy(ref IntPtr actions);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addopen(ref IntPtr actions, int fd, string path, int oflag, int mode);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn_file_actions_adddup2(ref IntPtr actions, int fd, int newfd);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addclose(ref IntPtr actions, int fd);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn_file_actions_addchdir_np(ref IntPtr actions, string path);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawnattr_init(out IntPtr attr);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawnattr_destroy(ref IntPtr attr);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawnattr_setflags(ref IntPtr attr, short flags);
    [DllImport(Lib, SetLastError = true)]
    public static extern int posix_spawn(out int pid, string path, ref IntPtr fileActions, ref IntPtr attr, string?[] argv, string?[] envp);
    [DllImport(Lib, SetLastError = true)]
    public static extern int waitpid(int pid, out int status, int options);
    [DllImport(Lib, SetLastError = true)]
    public static extern int kill(int pid, int sig);
    [DllImport(Lib, SetLastError = true)]
    public static extern int getpgid(int pid);
    [DllImport(Lib, SetLastError = true)]
    public static extern int getsid(int pid);

    public static bool WIFEXITED(int status) => (status & 0x7F) == 0;
    public static int WEXITSTATUS(int status) => (status >> 8) & 0xFF;
    public static bool WIFSIGNALED(int status) => (status & 0x7F) != 0 && (status & 0x7F) != 0x7F;
    public static int WTERMSIG(int status) => status & 0x7F;

    public static void Check(int rc, string site)
    {
        if (rc != 0) throw new IOException($"posix_spawn.{site} failed (rc={rc})");
    }
}
