using System;
using System.Threading.Tasks;

namespace Perch;

/// The one UI-thread invariant the whole app is built on: Session/PaneNode
/// state is mutated only on the UI thread, and every background path (pty
/// read threads, pipe servers, pollers, git tasks) re-enters through here
/// before touching it. On Windows this wraps the WPF Dispatcher; on macOS the
/// host window's main-thread invoker.
internal interface IUiThread
{
    /// Fire-and-forget marshal (Dispatcher.BeginInvoke).
    void Post(Action action);

    /// Awaitable marshal (Dispatcher.InvokeAsync).
    Task InvokeAsync(Action action);

    /// A timer whose tick runs on the UI thread (DispatcherTimer). Created
    /// stopped; callers Start() it.
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

internal interface IUiTimer : IDisposable
{
    TimeSpan Interval { get; set; }
    bool IsEnabled { get; }
    void Start();
    void Stop();
}
