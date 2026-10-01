using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Perch;

/// The phone link's side of the controller: what a tab looks like to the
/// phone, where a line from the phone goes, and turning the server on and
/// off from Settings → Phone. The server itself is PhoneServer.
internal sealed partial class AppController
{
    private PhoneServer? _phone;
    /// Each Claude pane's latest permission prompt, as its hook reported it
    /// going up (PermissionRequest; the hook doesn't hold it), and when.
    private readonly Dictionary<Guid, (PermAskMessage Msg, long At)> _paneAsks = new();
    private string? _phoneError;

    /// Start or stop the server to match Settings.PhoneEnabled. Safe to call
    /// again after any settings change.
    private void ApplyPhone()
    {
        if (!_settings.PhoneEnabled)
        {
            _phone?.Dispose();
            _phone = null;
            _phoneError = null;
            return;
        }
        if (string.IsNullOrEmpty(_settings.PhoneToken))
        {
            _settings.PhoneToken = PhoneServer.NewToken();
            _settings.Save();
        }
        if (_phone != null) return;
        var server = new PhoneServer(_ui, new PhoneServer.Host
        {
            Sessions = PhoneSessions,
            Send = PhoneSend,
            Reply = PhoneReplyAsync,
            Token = () => _settings.PhoneToken,
            Name = () => Environment.MachineName,
            History = PhoneHistoryAsync,
            Projects = PhoneProjects,
            Create = PhoneCreateAsync,
            Ask = id => SessionById(id) is Session s ? PhoneAskOf(s) : null,
            Answer = PhoneAnswer,
            Wake = PhoneWake,
        });
        try
        {
            server.Start(_settings.PhonePort);
            _phone = server;
            _phoneError = null;
        }
        catch (SocketException ex)
        {
            server.Dispose();
            _phoneError = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? $"Port {_settings.PhonePort} is taken by another program."
                : ex.Message;
            Log.Error("Phone.start", ex);
        }
    }

    private void PostPhoneInfo()
    {
        PostToPage(new
        {
            type = "phone.info",
            enabled = _settings.PhoneEnabled,
            listening = _phone != null,
            error = _phoneError,
            port = _phone?.Port ?? _settings.PhonePort,
            token = _settings.PhoneEnabled ? _settings.PhoneToken : "",
            hosts = _settings.PhoneEnabled ? PhoneServer.LocalAddresses().ToArray() : Array.Empty<string>(),
            name = Environment.MachineName,
        });
    }

    /// "New code": every paired phone has to scan again.
    private void OnPhoneNewCode()
    {
        _settings.PhoneToken = PhoneServer.NewToken();
        _settings.Save();
        Log.Info("Phone.token", "replaced");
        PostPhoneInfo();
    }

    private static string PhoneKind(Session s, List<PaneNode> leaves)
    {
        if (leaves.Any(p => p.IsChat)) return "chat";
        if (leaves.Any(p => p.AgentType == "claude" || !string.IsNullOrEmpty(p.ClaudeSessionId)))
            return s.ThreadOf != null ? "thread" : "claude";
        if (leaves.Any(p => p.AgentType == "codex" || !string.IsNullOrEmpty(p.CodexSessionId))) return "codex";
        return "shell";
    }

    private IReadOnlyList<PhoneSession> PhoneSessions() =>
        _store.Sessions.Select(s =>
        {
            var leaves = PaneTree.AllLeaves(s.Root).ToList();
            var kind = PhoneKind(s, leaves);
            var project = s.ProjectId is Guid pid ? _projects.ById(pid)?.Name : null;
            return new PhoneSession(
                s.Id, s.Title, project, kind,
                StateProjection.StateToString(StateProjection.AggregateState(leaves)),
                CanSend: kind is "chat" or "claude" or "thread",
                Active: _store.ActiveSessionId == s.Id,
                Asleep: s.Dormant,
                // The agent's pane when there is one: that's the color the tab wears.
                Asking: PhoneAskOf(s) is PhoneAsk a ? (a.Tool.Length > 0 ? $"{a.Tool}: {a.Summary}" : "") : null,
                DoneAtMs: leaves.Select(p => p.DoneAtUnixMs).DefaultIfEmpty(0).Max() is long done && done > 0 ? done : null,
                TurnStartMs: leaves.Where(p => p.AgentState == AgentState.Working && p.TurnStartUnixMs > 0)
                    .Select(p => (long?)p.TurnStartUnixMs).Min(),
                Note: leaves.FirstOrDefault(p => p.AgentState is AgentState.Waiting or AgentState.Permission
                    && !string.IsNullOrWhiteSpace(p.NotificationText))?.NotificationText,
                Parent: s.ThreadOf,
                Agent: kind is "claude" or "thread" ? "claude" : kind == "codex" ? "codex" : null,
                Color: (leaves.FirstOrDefault(p => p.IsChat || !string.IsNullOrEmpty(p.ClaudeSessionId)) ?? leaves.FirstOrDefault())?.ColorIndex ?? 0);
        }).ToList();

    private string PhoneSend(Guid id, string text)
    {
        if (SessionById(id) is not Session s) return "missing";
        if (text.Trim().Length == 0) return "empty";
        var leaves = PaneTree.AllLeaves(s.Root).ToList();
        switch (PhoneKind(s, leaves))
        {
            case "chat":
                _chatCtrl.OnSend(leaves.First(p => p.IsChat).Id, text);
                Log.Info("Phone.send", $"session={s.Id:N} chat");
                return "queued";
            case "claude":
            case "thread":
                var r = _threadSteer.Send(s, text);
                Log.Info("Phone.send", $"session={s.Id:N} {r}");
                return r switch
                {
                    ThreadSteering.SendResult.AnsweredQuestion => "answered",
                    ThreadSteering.SendResult.Empty => "empty",
                    _ => "queued",
                };
            default:
                return "unsupported";
        }
    }

    private async Task<PhoneReply?> PhoneReplyAsync(Guid id)
    {
        if (SessionById(id) is not Session s) return null;
        var leaves = PaneTree.AllLeaves(s.Root).ToList();
        if (leaves.FirstOrDefault(p => p.IsChat) is PaneNode chat)
            return _chatCtrl.LastReply(chat.Id) is { } r ? new PhoneReply(r.Text, r.AtMs) : null;
        var text = await ReadLastReplyAsync(s);
        return text == null ? null : new PhoneReply(text, null);
    }

    /// A tab's conversation for the phone: a project chat's own log, else the
    /// Claude transcript's prompts, prose and one line per tool call (the same
    /// events the chat's Overview draws for a thread). Empty for codex and
    /// shell tabs; null when the tab is gone.
    private async Task<IReadOnlyList<PhoneHistoryItem>?> PhoneHistoryAsync(Guid id)
    {
        if (SessionById(id) is not Session s) return null;
        var leaves = PaneTree.AllLeaves(s.Root).ToList();
        if (leaves.FirstOrDefault(p => p.IsChat) is PaneNode chat)
            return _chatCtrl.History(chat.Id)
                .Where(e => (e.Text ?? "").Trim().Length > 0)
                .Select(e => new PhoneHistoryItem(
                    e.Kind is "user" or "claude" or "tool" ? e.Kind : "notice",
                    e.Kind == "tool" && !string.IsNullOrEmpty(e.Tool) ? $"{e.Tool} {e.Text}".Trim() : (e.Text ?? "").Trim(),
                    e.AtMs > 0 ? e.AtMs : null))
                .ToList();
        var pane = leaves.FirstOrDefault(p => p.IsTerminal && !string.IsNullOrEmpty(p.ClaudeSessionId));
        if (pane == null) return Array.Empty<PhoneHistoryItem>();
        InspectorData? data = null;
        try { data = await _transcripts.ReadAsync(new TranscriptKey(pane.Id, pane.ClaudeSessionId, ResolvePaneCwd(s, pane))); }
        catch (Exception ex) { Log.Error("Phone.history", ex); }
        return (data?.Events ?? Array.Empty<InspectorEvent>())
            .Where(e => e.Kind is "prompt" or "beat" or "work")
            .TakeLast(200)
            .Select(e => new PhoneHistoryItem(
                e.Kind switch { "prompt" => "user", "beat" => "claude", _ => "tool" },
                e.Kind == "work"
                    ? $"{e.Verb} {e.Target}".Trim() + (e.Repeat > 1 ? $" ×{e.Repeat}" : "")
                    : e.Text.Trim(),
                DateTimeOffset.TryParse(e.Ts, out var at) ? at.ToUnixTimeMilliseconds() : null))
            .Where(i => i.Text.Length > 0)
            .ToList();
    }

    /// The permission prompt a tab's Claude is showing, or null.
    private PhoneAsk? PhoneAskOf(Session s)
    {
        if (ClaudePaneOf(s) is not PaneNode p || p.AgentState != AgentState.Permission) return null;
        if (!_paneAsks.TryGetValue(p.Id, out var a)) return new PhoneAsk("", "", null, Array.Empty<string>(), false);
        var rules = a.Msg.Suggestions ?? Array.Empty<string>();
        return new PhoneAsk(a.Msg.Tool ?? "", a.Msg.Summary ?? "", a.Msg.Input, rules, a.Msg.Always ?? rules.Length > 0);
    }

    /// Answer a permission prompt from the phone with the keys a person
    /// presses in the terminal, as the chat's Overview does for a thread:
    /// Enter takes "Yes", Down+Enter "Yes, and don't ask again" (offered only
    /// when the prompt has it), Escape "No, and tell Claude what to do
    /// differently" (ThreadSteering.Deny), after which the phone's words go in
    /// as the next line.
    private string PhoneAnswer(Guid id, string answer, string? text)
    {
        if (SessionById(id) is not Session s) return "missing";
        if (ClaudePaneOf(s) is not PaneNode p || p.AgentState != AgentState.Permission) return "not-asking";
        // One answer per prompt, shared with the Overview's: the pane reads
        // "permission" for a moment after the key lands, and a second answer
        // in that window would answer the NEXT prompt unseen.
        var now = Environment.TickCount64;
        if (_threadAnsweredAt.TryGetValue(p.Id, out var last) && now - last < 4000) return "not-asking";
        if (answer == "always" && PhoneAskOf(s) is { CanAlways: false }) return "not-asking";
        _threadAnsweredAt[p.Id] = now;
        _paneAsks.Remove(p.Id);
        // A deny ends the turn with no hook; ThreadSteering marks it done and
        // sends the words after it.
        if (answer == "deny") _threadSteer.Deny(s, text);
        else _panes.Write(p.Id, answer == "always"
            ? new byte[] { 0x1b, (byte)'[', (byte)'B', 0x0d }
            : new byte[] { 0x0d });
        Log.Info("Phone.answer", $"session={s.Id:N} {answer}{(text != null ? " +text" : "")}");
        return "answered";
    }

    /// Wake a sleeping tab from the phone: its Claude resumes in the
    /// background, as a line sent to it would make it, without one.
    private string PhoneWake(Guid id)
    {
        if (SessionById(id) is not Session s) return "missing";
        if (!s.Dormant) return "not-asleep";
        WakeSession(s, background: true);
        EnsureSessionRunning(s);
        PushState();
        Log.Info("Phone.wake", $"session={s.Id:N}");
        return "awake";
    }

    private IReadOnlyList<PhoneProject> PhoneProjects() =>
        _projects.Projects.Where(p => !p.Hidden && System.IO.Directory.Exists(p.Path))
            .Select(p => new PhoneProject(p.Id, p.Name))
            .ToList();

    /// A new tab from the phone: the same Claude tab "New tab" makes in a
    /// project on the desktop (no worktree), opened and selected there like
    /// one made at the keyboard, with the phone's first message as Claude's
    /// first prompt.
    private async Task<Guid?> PhoneCreateAsync(PhoneNewTab tab)
    {
        if (_projects.ById(tab.ProjectId) is not Project proj || !System.IO.Directory.Exists(proj.Path)) return null;
        var s = await CreateProjectTabAsync(proj, tab.Name.Length > 0 ? tab.Name : null, "claude",
            worktree: false, model: null, pinnedPeerName: null, firstPrompt: tab.Text);
        Log.Info("Phone.create", s == null ? $"project={proj.Id:N} failed" : $"session={s.Id:N} project={proj.Id:N}");
        return s?.Id;
    }
}
