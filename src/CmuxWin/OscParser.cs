using System;
using System.Collections.Generic;
using System.Text;

namespace CmuxWin;

internal enum NotificationLevel { Info, Success, Warn, Error }

internal enum OscKind { Notification, Cwd }

/// One extracted OSC event. <see cref="Text"/> is the URL-decoded path for
/// <see cref="OscKind.Cwd"/>, or the message body for
/// <see cref="OscKind.Notification"/>. <see cref="Level"/> only applies to
/// notifications and is <see cref="NotificationLevel.Info"/> otherwise.
internal readonly record struct OscEvent(OscKind Kind, string Text, NotificationLevel Level);

/// Streaming OSC parser. Currently recognises:
///   * OSC 9 — growl-style notification (iTerm2 / Ghostty / WT compatible)
///   * OSC 7 — working-directory hint (`file://host/path`)
///
/// Sequences may split across multiple PTY chunks; the parser holds state
/// between Feed calls.
///
/// Format:
///   ESC ] <code> ; <payload> BEL              (or ESC \ as ST terminator)
internal sealed class OscParser
{
    private enum State { Idle, GotEsc, GotBracket, ReadingCode, AfterSemi, GotEscInPayload }

    private State _state = State.Idle;
    private readonly StringBuilder _code = new(4);
    private readonly StringBuilder _payload = new();
    private const int MaxPayload = 4096;

    public IReadOnlyList<OscEvent> Feed(ReadOnlySpan<char> chunk)
    {
        List<OscEvent>? hits = null;

        foreach (var c in chunk)
        {
            switch (_state)
            {
                case State.Idle:
                    if (c == '\x1b') _state = State.GotEsc;
                    break;

                case State.GotEsc:
                    if (c == ']') { _state = State.GotBracket; _code.Clear(); }
                    else _state = c == '\x1b' ? State.GotEsc : State.Idle;
                    break;

                case State.GotBracket:
                    if (c >= '0' && c <= '9') { _code.Append(c); _state = State.ReadingCode; }
                    else _state = State.Idle;
                    break;

                case State.ReadingCode:
                    if (c >= '0' && c <= '9') _code.Append(c);
                    else if (c == ';')
                    {
                        _payload.Clear();
                        _state = IsRecognisedCode(_code) ? State.AfterSemi : State.Idle;
                    }
                    else _state = State.Idle;
                    break;

                case State.AfterSemi:
                    if (c == '\x07') { Emit(ref hits); _state = State.Idle; }
                    else if (c == '\x1b') _state = State.GotEscInPayload;
                    else if (_payload.Length < MaxPayload) _payload.Append(c);
                    else { _state = State.Idle; _payload.Clear(); }
                    break;

                case State.GotEscInPayload:
                    if (c == '\\') { Emit(ref hits); _state = State.Idle; }
                    else
                    {
                        _payload.Append('\x1b');
                        if (c == '\x1b') _state = State.GotEsc;
                        else { _payload.Append(c); _state = State.AfterSemi; }
                    }
                    break;
            }
        }

        return (IReadOnlyList<OscEvent>?)hits ?? Array.Empty<OscEvent>();
    }

    private static bool IsRecognisedCode(StringBuilder code)
    {
        if (code.Length == 1)
        {
            return code[0] == '7' || code[0] == '9';
        }
        return false;
    }

    private void Emit(ref List<OscEvent>? hits)
    {
        var code = _code.ToString();
        var payload = _payload.ToString();
        _payload.Clear();
        _code.Clear();
        hits ??= new List<OscEvent>(1);

        switch (code)
        {
            case "9":
                // Windows Terminal "shell integration" uses OSC 9 subcodes,
                // notably `OSC 9;9;<path>` to report cwd. Disambiguate before
                // treating the payload as a notification body.
                if (payload.StartsWith("9;", StringComparison.Ordinal))
                {
                    var cwd9 = NormalisePath(payload.Substring(2));
                    if (!string.IsNullOrEmpty(cwd9))
                        hits.Add(new OscEvent(OscKind.Cwd, cwd9, NotificationLevel.Info));
                    return;
                }
                if (payload.StartsWith("cmux:", StringComparison.Ordinal))
                {
                    var sep = payload.IndexOf(';');
                    if (sep > "cmux:".Length)
                    {
                        var levelStr = payload.Substring("cmux:".Length, sep - "cmux:".Length);
                        var body = payload.Substring(sep + 1);
                        hits.Add(new OscEvent(OscKind.Notification, body, ParseLevel(levelStr)));
                        return;
                    }
                }
                hits.Add(new OscEvent(OscKind.Notification, payload, NotificationLevel.Info));
                return;

            case "7":
                var path = ParseOsc7Path(payload);
                if (!string.IsNullOrEmpty(path))
                    hits.Add(new OscEvent(OscKind.Cwd, path, NotificationLevel.Info));
                return;
        }
    }

    private static string ParseOsc7Path(string payload)
    {
        string path = payload;
        const string scheme = "file://";
        if (path.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            path = path.Substring(scheme.Length);
            var slash = path.IndexOf('/');
            path = slash >= 0 ? path.Substring(slash) : "/" + path;
        }
        return NormalisePath(path);
    }

    private static string NormalisePath(string path)
    {
        path = path.Trim().TrimStart('"').TrimEnd('"');
        if (path.Length >= 3 && path[0] == '/' && path[2] == ':') path = path.Substring(1);
        try { path = Uri.UnescapeDataString(path); } catch { /* leave as-is */ }
        return path.Replace('/', '\\');
    }

    private static NotificationLevel ParseLevel(string s) => s.ToLowerInvariant() switch
    {
        "success" or "ok" => NotificationLevel.Success,
        "warn" or "warning" => NotificationLevel.Warn,
        "error" or "err" or "fail" => NotificationLevel.Error,
        _ => NotificationLevel.Info,
    };
}
