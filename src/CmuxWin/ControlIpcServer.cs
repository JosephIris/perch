using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CmuxWin;

/// App-level control pipe used by the test harness so the host can be driven
/// without synthesizing keystrokes (SendKeys spills to whichever window has
/// the foreground and once cost us several Chrome windows). Accepts
/// line-delimited JSON like `{"verb":"split-right"}` and dispatches each
/// verb to the existing UI commands on the dispatcher thread.
///
/// Disabled by default — only starts when CMUX_ENABLE_TEST_IPC is set in
/// the environment of the launching process. A normal install never opens
/// the pipe so there's no production-facing surface to harden.
internal sealed class ControlIpcServer : IDisposable
{
    public const string PipeName = @"cmux\control";

    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;
    private readonly Action _splitRight;
    private readonly Action _splitDown;
    private readonly Action _closeActivePane;
    private Task? _acceptLoop;
    private bool _disposed;

    public ControlIpcServer(
        Dispatcher dispatcher,
        Action splitRight,
        Action splitDown,
        Action closeActivePane)
    {
        _dispatcher = dispatcher;
        _splitRight = splitRight;
        _splitDown = splitDown;
        _closeActivePane = closeActivePane;
    }

    public static bool IsEnabled =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CMUX_ENABLE_TEST_IPC"));

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
            Log.Info($"ControlIpc.recv verb={verb}");
            _dispatcher.BeginInvoke(() =>
            {
                try
                {
                    switch (verb)
                    {
                        case "split-right":       _splitRight(); break;
                        case "split-down":        _splitDown(); break;
                        case "close-active-pane": _closeActivePane(); break;
                        default: Log.Info($"ControlIpc.unknown verb={verb}"); break;
                    }
                }
                catch (Exception ex) { Log.Error($"ControlIpc.dispatch {verb}", ex); }
            });
        }
        catch (JsonException ex) { Log.Error("ControlIpc.parseJson", ex); }
        catch (Exception ex) { Log.Error("ControlIpc.dispatch.outer", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        _cts.Dispose();
    }
}
