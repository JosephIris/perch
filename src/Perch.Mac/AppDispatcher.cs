using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// The mac host's "UI thread": a dedicated managed thread draining a work
/// queue, with a SynchronizationContext installed so `await` continuations
/// started on it resume on it. That preserves the invariant the whole app is
/// built on (Session/PaneNode state is only touched on one thread — see
/// IUiThread). Photino's native main thread stays separate; the few Photino
/// calls (SendWebMessage etc.) are internally thread-safe or marshalled by
/// the window wrapper.
internal sealed class AppDispatcher : IUiThread
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public AppDispatcher()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "perch-app" };
        _thread.Start();
    }

    private void Run()
    {
        SynchronizationContext.SetSynchronizationContext(new Context(this));
        foreach (var work in _queue.GetConsumingEnumerable())
        {
            try { work(); }
            catch (Exception ex) { Log.Error("AppDispatcher", ex); }
        }
    }

    public void Post(Action action) => _queue.Add(action);

    public Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new QueueTimer(this, interval, tick);

    private sealed class Context : SynchronizationContext
    {
        private readonly AppDispatcher _d;
        public Context(AppDispatcher d) => _d = d;
        public override void Post(SendOrPostCallback cb, object? state) => _d.Post(() => cb(state));
        public override void Send(SendOrPostCallback cb, object? state) => _d.InvokeAsync(() => cb(state)).GetAwaiter().GetResult();
        public override SynchronizationContext CreateCopy() => this;
    }

    /// DispatcherTimer stand-in: a System.Threading.Timer whose ticks are
    /// marshalled onto the app thread. A tick already in the queue when Stop()
    /// runs is dropped by the _running check, matching DispatcherTimer's
    /// stop-means-stop behavior closely enough for our callers.
    private sealed class QueueTimer : IUiTimer
    {
        private readonly AppDispatcher _d;
        private readonly Action _tick;
        private Timer? _timer;
        private TimeSpan _interval;
        private volatile bool _running;

        public QueueTimer(AppDispatcher d, TimeSpan interval, Action tick)
        {
            _d = d;
            _interval = interval;
            _tick = tick;
        }

        public TimeSpan Interval
        {
            get => _interval;
            set
            {
                _interval = value;
                if (_running) _timer?.Change(value, value);
            }
        }

        public bool IsEnabled => _running;

        public void Start()
        {
            if (_running) return;
            _running = true;
            _timer ??= new Timer(_ => { if (_running) _d.Post(() => { if (_running) _tick(); }); },
                null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_interval, _interval);
        }

        public void Stop()
        {
            _running = false;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        public void Dispose()
        {
            _running = false;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
