using System;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Perch;

/// Per-pane named-pipe server. Listens on \\.\pipe\perch\<paneId>, accepts one
/// connection at a time, reads line-delimited JSON from the client, and raises
/// strongly-typed events on the UI dispatcher.
///
/// Mirrors perch for macOS's per-workspace socket: agents inside the pane talk
/// to the host via the tiny `perch` CLI, no escape-code parsing required.
internal sealed class PerchIpcServer : IDisposable
{
    public Guid PaneId { get; }
    public string PipePath => $@"\\.\pipe\perch\{PaneId:N}";

    public event Action<NotifyMessage>? OnNotify;
    public event Action<StatusMessage>? OnStatus;
    public event Action<MetaMessage>? OnMeta;
    public event Action<FocusMessage>? OnFocus;
    public event Action<SendMessage>? OnSend;
    public event Action<OpenMessage>? OnOpen;
    public event Action<GitBaselineMessage>? OnGitBaseline;
    public event Action<GitTouchedMessage>? OnGitTouched;
    public event Action<GitCommitMessage>? OnGitCommit;
    public event Action<TitleMessage>? OnTitle;
    public event Action<NameResetMessage>? OnNameReset;
    public event Action<AgentMessage>? OnAgent;
    public event Action<SessionMessage>? OnSession;
    public event Action<CloudStampedMessage>? OnCloudStamped;
    public event Action<PeerMsgMessage>? OnPeerMsg;
    public event Action<TeamPostMessage>? OnTeamPost;
    public event Action<TeamTaskMessage>? OnTeamTask;
    public event Action<TeamAskMessage>? OnTeamAsk;
    public event Action<TeamReactMessage>? OnTeamReact;
    public event Action<PermAskMessage>? OnPermAsk;
    public event Action<PermDeniedMessage>? OnPermDenied;

    private readonly CancellationTokenSource _cts = new();
    private readonly Dispatcher _dispatcher;
    private Task? _acceptLoop;
    private bool _disposed;

    public PerchIpcServer(Guid paneId, Dispatcher dispatcher)
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
        var pipeName = $@"perch\{PaneId:N}";
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
                Log.Error($"PerchIpc.AcceptLoop pane={PaneId}", ex);
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
            Log.Info($"PerchIpc.recv pane={PaneId:N} type={type}");
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
                case "git.touched":
                    var gt = JsonSerializer.Deserialize<GitTouchedMessage>(json, IpcJson.Options);
                    if (gt != null) _dispatcher.BeginInvoke(() => OnGitTouched?.Invoke(gt));
                    break;
                case "git.commit":
                    var gc = JsonSerializer.Deserialize<GitCommitMessage>(json, IpcJson.Options);
                    if (gc != null) _dispatcher.BeginInvoke(() => OnGitCommit?.Invoke(gc));
                    break;
                case "title":
                    var t = JsonSerializer.Deserialize<TitleMessage>(json, IpcJson.Options);
                    if (t != null) _dispatcher.BeginInvoke(() => OnTitle?.Invoke(t));
                    break;
                case "name.reset":
                    var nrm = JsonSerializer.Deserialize<NameResetMessage>(json, IpcJson.Options);
                    if (nrm != null) _dispatcher.BeginInvoke(() => OnNameReset?.Invoke(nrm));
                    break;
                case "agent":
                    var am = JsonSerializer.Deserialize<AgentMessage>(json, IpcJson.Options);
                    if (am != null) _dispatcher.BeginInvoke(() => OnAgent?.Invoke(am));
                    break;
                case "session":
                    var ses = JsonSerializer.Deserialize<SessionMessage>(json, IpcJson.Options);
                    if (ses != null) _dispatcher.BeginInvoke(() => OnSession?.Invoke(ses));
                    break;
                case "cloud.stamped":
                    var cs = JsonSerializer.Deserialize<CloudStampedMessage>(json, IpcJson.Options);
                    if (cs != null) _dispatcher.BeginInvoke(() => OnCloudStamped?.Invoke(cs));
                    break;
                case "peer.msg":
                    var pm = JsonSerializer.Deserialize<PeerMsgMessage>(json, IpcJson.Options);
                    if (pm != null) _dispatcher.BeginInvoke(() => OnPeerMsg?.Invoke(pm));
                    break;
                case "team.post":
                    var tp = JsonSerializer.Deserialize<TeamPostMessage>(json, IpcJson.Options);
                    if (tp != null) _dispatcher.BeginInvoke(() => OnTeamPost?.Invoke(tp));
                    break;
                case "team.task":
                    var tt = JsonSerializer.Deserialize<TeamTaskMessage>(json, IpcJson.Options);
                    if (tt != null) _dispatcher.BeginInvoke(() => OnTeamTask?.Invoke(tt));
                    break;
                case "team.ask":
                    var ta = JsonSerializer.Deserialize<TeamAskMessage>(json, IpcJson.Options);
                    if (ta != null) _dispatcher.BeginInvoke(() => OnTeamAsk?.Invoke(ta));
                    break;
                case "team.react":
                    var tr = JsonSerializer.Deserialize<TeamReactMessage>(json, IpcJson.Options);
                    if (tr != null) _dispatcher.BeginInvoke(() => OnTeamReact?.Invoke(tr));
                    break;
                case "perm.ask":
                    var pa = JsonSerializer.Deserialize<PermAskMessage>(json, IpcJson.Options);
                    if (pa != null) _dispatcher.BeginInvoke(() => OnPermAsk?.Invoke(pa));
                    break;
                case "perm.denied":
                    var pd = JsonSerializer.Deserialize<PermDeniedMessage>(json, IpcJson.Options);
                    if (pd != null) _dispatcher.BeginInvoke(() => OnPermDenied?.Invoke(pd));
                    break;
            }
        }
        catch (JsonException ex) { Log.Error("PerchIpc.Dispatch.Json", ex); }
        catch (Exception ex) { Log.Error("PerchIpc.Dispatch", ex); }
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

/// Sent by the cc HookHandler after a file-editing tool runs (post-tool-use,
/// Edit/Write/NotebookEdit), carrying the absolute path the agent touched.
/// The host records it per pane so that when several agents share ONE working
/// tree (projects-mode tabs without worktrees) each pane's loc chip can be
/// filtered to its own agent's files instead of every tab wearing the union.
internal sealed record GitTouchedMessage(
    [property: JsonPropertyName("path")] string? Path);

/// Sent by the cc HookHandler after a Bash tool call whose command ran
/// `git commit`, carrying the short sha parsed from the "[branch abc1234]"
/// marker in the tool's output. The host records it on the pane, and each
/// git refresh intersects the pane's claimed shas with `@{upstream}..HEAD`
/// to produce the per-tab "↑N mine" count — the branch-wide ahead is a fact
/// about the BRANCH and reads identically on every tab sharing it.
internal sealed record GitCommitMessage(
    [property: JsonPropertyName("sha")] string? Sha);

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

/// Sent by the cc HookHandler on Claude's session-start (name = "claude") and
/// cleared on session-end (name = ""). Tells the host which agent runs in the
/// pane so the header can show a "CC" badge. A plain shell never sends this.
internal sealed record AgentMessage(
    [property: JsonPropertyName("name")] string? Name);

/// Sent by the cc HookHandler on Claude's session-start, carrying Claude's
/// own session id (the SessionStart payload's `session_id`). The host persists
/// it on the pane so a relaunch can `claude --resume <id>`. Idempotent: the
/// resumed run re-emits session-start with the same id.
///
/// `name` is the cross-session peer name this launch went out under (the
/// wrap-claude shim passed the host-written per-pane name file as --name).
/// Null for launches predating the file. The host stores it on the pane so a
/// SendMessage target observed in another pane can be routed back to a row.
/// `socket` is the session's own inbox address (Claude Code exports it to
/// hooks as CLAUDE_CODE_MESSAGING_SOCKET, possibly with a `uds:` prefix).
/// A bot replying to a teammate sometimes addresses the REPLY ADDRESS it
/// was handed rather than a name; the host maps such a target back to a
/// pane — and so to a bot — through this.
internal sealed record SessionMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("socket")] string? Socket = null);

/// Sent by the cc HookHandler when the agent messages ANOTHER Claude Code
/// session (the cross-session SendMessage tool). phase="sending" fires from
/// PreToolUse — the note is in flight, warm the pair bracket; phase="sent"
/// fires from PostToolUse with the delivered/failed verdict (`ok`). `target`
/// is the peer NAME the sender addressed; the host resolves it to a pane via
/// the names it assigned. `text` is a one-line cut of the message body.
///
/// `message` is the FULL body and `summary` the sender's own one-liner, both
/// added for the team room, which shows what a bot actually said to a
/// teammate rather than a 140-char cut. Optional so a hook binary predating
/// them still parses (the pair note only ever needed `text`).
internal sealed record PeerMsgMessage(
    [property: JsonPropertyName("phase")] string? Phase,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("ok")] bool? Ok = null,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("summary")] string? Summary = null);

/// Sent by `perch team post <text>` from inside a bot's pane: a note for the
/// team room (and so for the user) that pings no teammate. The bot's way to
/// say "done, it's in src/x" without a SendMessage to each colleague. The
/// host appends it to the room ledger; nothing is typed anywhere.
internal sealed record TeamPostMessage(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("image")] string? Image = null);

/// `perch team ask "<question>" [--choices "A|B"]` from a bot: a card in the
/// room the owner answers; the answer comes back to the bot as a post. `id`
/// is minted by the CLI so the answer can name the card it closes.
internal sealed record TeamAskMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("choices")] string[]? Choices = null);

/// `perch team react <target> <emoji>` from a bot. `target` is `#<seq>` (a
/// room row by number) or `@<nick>` (that bot's latest message).
internal sealed record TeamReactMessage(
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("emoji")] string? Emoji);

/// The PermissionRequest hook in a bot's pane: Claude Code is about to show a
/// permission prompt and the hook is holding it (polling for the answer file)
/// so the owner can answer from the room instead. `summary` is one line
/// (Bash: the command; Edit/Write: the file; else the tool name); `input` is
/// the raw tool_input JSON, capped; `suggestions` the rules cc offered.
internal sealed record PermAskMessage(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("input")] string? Input = null,
    [property: JsonPropertyName("suggestions")] string[]? Suggestions = null);

/// The PermissionDenied hook: auto mode's classifier blocked a tool call.
/// Information only — nothing to answer.
internal sealed record PermDeniedMessage(
    [property: JsonPropertyName("tool")] string? Tool,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("reason")] string? Reason = null);

/// `perch team task …` from a bot: the task board's verbs. `op` is "main"
/// (the lead sets or renames the task), "assign" (the lead gives `bot` a
/// piece), "mine" (a bot sets its own piece, status, note) or "done" (the
/// lead asks the owner to confirm). TeamController checks who may do what.
internal sealed record TeamTaskMessage(
    [property: JsonPropertyName("op")] string? Op,
    [property: JsonPropertyName("bot")] string? Bot,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("taskId")] string? TaskId = null);

/// Sent by the cc HookHandler (PreToolUse/Bash) the moment it stamps agent
/// labels onto a `gcloud ... create`. The hook can only put JOIN KEYS on the
/// resource — GCP label values are capped at 63 chars of [a-z0-9_-], so the
/// pane's name and the prompt behind the machine cannot go there. This message
/// is the host's cue to snapshot both into the ledger, keyed by session id, so
/// that when the pane is long gone the panel can still say what the orphaned
/// cluster was actually FOR.
internal sealed record CloudStampedMessage(
    [property: JsonPropertyName("session")] string? Session,
    [property: JsonPropertyName("kind")] string? Kind);
