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
                Asleep: s.Dormant);
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
}
