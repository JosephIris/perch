using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CmuxWin;

/// Per-pane named-pipe server. Listens on \\.\pipe\cmux\<paneId>, accepts one
/// connection at a time, reads line-delimited JSON from the client, and raises
/// strongly-typed events on the UI dispatcher.
///
/// Mirrors cmux for macOS's per-workspace socket: agents inside the pane talk
/// to the host via the tiny `cmux` CLI, no escape-code parsing required.
internal sealed class CmuxIpcServer : IDisposable
{
    public Guid PaneId { get; }
    public string PipePath => $@"\\.\pipe\cmux\{PaneId:N}";

    public event Action<NotifyMessage>? OnNotify;
    public event Action<StatusMessage>? OnStatus;
    public event Action<MetaMessage>? OnMeta;
    public event Action<FocusMessage>? OnFocus;
    public event Action<SendMessage>? OnSend;
    public event Action<OpenMessage>? OnOpen;
    public event Action<GitBaselineMessage>? OnGitBaseline;
    public event Action<TitleMessage>? OnTitle;
    public event Action<NameResetMessage>? OnNameReset;

    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;
    private Task? _acceptLoop;
    private bool _disposed;

    public CmuxIpcServer(Guid paneId, Dispatcher dispatcher)
    {
        PaneId = paneId;
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        if (_acceptLoop != null) return;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        // One connection at a time is plenty — the CLI does write-and-exit
        // and we want serialization for free. PipeOptions.Asynchronous so we
        // can cancel WaitForConnectionAsync on dispose.
        var pipeName = $@"cmux\{PaneId:N}";
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    pipeName,
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
                Log.Error($"CmuxIpc.AcceptLoop pane={PaneId}", ex);
                // Back off so a misbehaving client can't spin us.
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
            catch (IOException) { break; } // client disconnected
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
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)) return;
            var type = typeEl.GetString();
            Log.Info($"CmuxIpc.recv pane={PaneId:N} type={type}");
            switch (type)
            {
                case "notify":
                    var n = JsonSerializer.Deserialize<NotifyMessage>(json, IpcJson.Options);
                    if (n != null) _dispatcher.BeginInvoke(() => OnNotify?.Invoke(n));
                    break;
                case "status":
                    var s = JsonSerializer.Deserialize<StatusMessage>(json, IpcJson.Options);
                    if (s != null) _dispatcher.BeginInvoke(() => OnStatus?.Invoke(s));
                    break;
                case "meta":
                    var m = JsonSerializer.Deserialize<MetaMessage>(json, IpcJson.Options);
                    if (m != null) _dispatcher.BeginInvoke(() => OnMeta?.Invoke(m));
                    break;
                case "focus":
                    var f = JsonSerializer.Deserialize<FocusMessage>(json, IpcJson.Options);
                    if (f != null) _dispatcher.BeginInvoke(() => OnFocus?.Invoke(f));
                    break;
                case "send":
                    var sm = JsonSerializer.Deserialize<SendMessage>(json, IpcJson.Options);
                    if (sm != null) _dispatcher.BeginInvoke(() => OnSend?.Invoke(sm));
                    break;
                case "open":
                    var om = JsonSerializer.Deserialize<OpenMessage>(json, IpcJson.Options);
                    if (om != null) _dispatcher.BeginInvoke(() => OnOpen?.Invoke(om));
                    break;
                case "git.baseline":
                    var gb = JsonSerializer.Deserialize<GitBaselineMessage>(json, IpcJson.Options);
                    if (gb != null) _dispatcher.BeginInvoke(() => OnGitBaseline?.Invoke(gb));
                    break;
                case "title":
                    var t = JsonSerializer.Deserialize<TitleMessage>(json, IpcJson.Options);
                    if (t != null) _dispatcher.BeginInvoke(() => OnTitle?.Invoke(t));
                    break;
                case "name.reset":
                    var nrm = JsonSerializer.Deserialize<NameResetMessage>(json, IpcJson.Options);
                    if (nrm != null) _dispatcher.BeginInvoke(() => OnNameReset?.Invoke(nrm));
                    break;
            }
        }
        catch (JsonException ex) { Log.Error("CmuxIpc.Dispatch.Json", ex); }
        catch (Exception ex) { Log.Error("CmuxIpc.Dispatch", ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        // Don't wait synchronously — the accept loop may be parked in
        // WaitForConnectionAsync. Cancellation will unblock it and the task
        // will finish on its own. Dispose() must not block the UI thread.
        _cts.Dispose();
    }
}

internal static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed record NotifyMessage(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("level")] string? Level);

internal sealed record StatusMessage(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("detail")] string? Detail);

internal sealed record MetaMessage(
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("ports")] int[]? Ports,
    [property: JsonPropertyName("cwd")] string? Cwd);

internal sealed record FocusMessage(
    [property: JsonPropertyName("target")] string Target);

internal sealed record SendMessage(
    [property: JsonPropertyName("target")] string Target,
    [property: JsonPropertyName("input")] string Input);

internal sealed record OpenMessage(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("cwd")] string? Cwd,
    [property: JsonPropertyName("cmd")] string? Cmd);

/// Sent by the cc HookHandler on Claude's session-start, after it captures
/// HEAD locally via `git rev-parse HEAD`. The host stores this on the pane
/// and recomputes commit count via `git rev-list <sha>..HEAD --count` each
/// time the pane's state changes.
internal sealed record GitBaselineMessage(
    [property: JsonPropertyName("sha")] string Sha);

/// Sent by the cc HookHandler on Claude's first UserPromptSubmit. Carries the
/// (already length-bounded) prompt text the host uses to auto-name a still-
/// auto-named pane — "capture what's happening" from the first message.
internal sealed record TitleMessage(
    [property: JsonPropertyName("text")] string Text);

/// Sent by the cc HookHandler on Claude's session-start (new launch or
/// `/clear`). Re-enables agent auto-naming for the pane so the NEXT first
/// prompt re-titles it — unless the user manually named it. `source` is
/// Claude's SessionStart source ("startup" | "clear" | "resume"); the host
/// skips the reset on "resume" so resumed sessions keep their label.
internal sealed record NameResetMessage(
    [property: JsonPropertyName("source")] string? Source);
