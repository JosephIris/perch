using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Perch;

/// IUiThread over the WPF Dispatcher — the original substrate the app was
/// written against; Core code that used to take a Dispatcher now takes this.
internal sealed class WpfUiThread : IUiThread
{
    private readonly Dispatcher _dispatcher;

    public WpfUiThread(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) =>
        new WpfUiTimer(_dispatcher, interval, tick);

    private sealed class WpfUiTimer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public WpfUiTimer(Dispatcher dispatcher, TimeSpan interval, Action tick)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = interval,
            };
            _timer.Tick += (_, _) => tick();
        }

        public TimeSpan Interval
        {
            get => _timer.Interval;
            set => _timer.Interval = value;
        }

        public bool IsEnabled => _timer.IsEnabled;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
        public void Dispose() => _timer.Stop();
    }
}
