using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Perch;

/// App-level control pipe for the test harness. Off by default; only listens
/// when PERCH_ENABLE_TEST_IPC is set in the host's environment so a normal
/// install exposes no surface.
///
/// Accepts line-delimited JSON like `{"verb":"pty.send","text":"echo hi\r"}`.
/// Each message is dispatched to a host-provided callback on the UI thread,
/// so the callback can touch WebView2 / ConPty / Session state safely.
///
/// The reason this exists: the previous WPF-era test harness drove the app
/// with SendKeys.SendWait, which sends to whichever HWND has the foreground.
/// When perch briefly lost focus during a pane split/redraw, the keystrokes
/// spilled into the user's Chrome and tore down browser windows. This pipe
/// is the keystroke-free replacement -- the page never enters the picture.
internal sealed class ControlIpcServer : IDisposable
{
    public const string PipeName = @"perch\control";

    public delegate void VerbHandler(string verb, JsonElement root);

    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;
    private readonly VerbHandler _onVerb;
    private Task? _acceptLoop;
    private bool _disposed;

    public ControlIpcServer(Dispatcher dispatcher, VerbHandler onVerb)
    {
        _dispatcher = dispatcher;
        _onVerb = onVerb;
    }

    public static bool IsEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PERCH_ENABLE_TEST_IPC"));

    public void Start()
    {
        if (_acceptLoop != null) return;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Log.Info("ControlIpc.start", $@"listening on \\.\pipe\{PipeName}");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleClientAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("ControlIpc.Accept", ex);
                try { await Task.Delay(250, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { server?.Dispose(); }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server);
        while (!ct.IsCancellationRequested)
        {
            string? line;
            try { line = await reader.ReadLineAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; }
            if (line == null) break;
            if (line.Length == 0) continue;
            Dispatch(line);
        }
    }

    private void Dispatch(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("verb", out var v)) return;
            var verb = v.GetString();
            if (string.IsNullOrEmpty(verb)) return;
            Log.Info($"ControlIpc.recv verb={verb}");
            // Clone the element so the callback can outlive this method's
            // `using var doc`. JsonElement is cheap to copy by deep-clone.
            var clone = doc.RootElement.Clone();
            _dispatcher.BeginInvoke(() =>
            {
                try { _onVerb(verb!, clone); }
                catch (Exception ex) { Log.Error($"ControlIpc.dispatch {verb}", ex); }
            });
        }
        catch (JsonException ex) { Log.Error("ControlIpc.parseJson", ex); }
        catch (Exception ex)     { Log.Error("ControlIpc.dispatch.outer", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
