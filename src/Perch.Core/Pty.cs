using System;

namespace Perch;

/// The pseudo-terminal a pane's shell runs in. One implementation per OS:
/// ConPTY (CreatePseudoConsole) on Windows, openpty + posix_spawn on macOS.
/// The surface is exactly what PaneManager/MainWindow consumed from the
/// original ConPty class — see that file for the flow-control contract.
internal interface IPty : IDisposable
{
    /// Raw output bytes, fired on the pty's read thread. Subscribers marshal.
    event EventHandler<ReadOnlyMemory<byte>> OutputReceived;

    /// Shell process exited (exit code). Fired exactly once.
    event EventHandler<int> Exited;

    int ProcessId { get; }

    /// Peak unacked backlog in bytes — diagnostic for the flow-control
    /// harness (`pty.flowstats`).
    long MaxOutstanding { get; }

    /// Kernel-tracked membership scope for dev-server attribution (job
    /// object on Windows, process group on macOS). Null when unavailable.
    IProcScope? Scope { get; }

    void Write(ReadOnlySpan<byte> bytes);

    /// Release flow-control backpressure for bytes the page has rendered.
    void Ack(long bytes);

    /// Resize, deduping identical sizes (a redundant SIGWINCH makes the TUI
    /// redraw and the idle watchdog reads that as agent activity).
    void Resize(int cols, int rows);
}

internal interface IPtyFactory
{
    /// Spawn `command` on a fresh pty. Throws on failure; the caller surfaces
    /// the error to the page.
    IPty Start(string command, int cols, int rows, string? cwd);
}

/// "Is this pid one of the pane's processes?" — answers dev-server
/// attribution even after intermediate parents have exited. Windows: job
/// object membership. macOS: process-group / session membership.
internal interface IProcScope
{
    bool ContainsPid(int pid);
}
