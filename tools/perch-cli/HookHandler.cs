using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PerchCli;

// Handles `perch hooks claude <event>` callbacks fired by Claude Code via the
// --settings hooks JSON our wrapper injects. Claude passes the hook context
// as JSON on stdin; we extract what's useful and send it back to the perch
// host through the IPC pipe.
//
// We intentionally accept-and-forget unknown events so adding a new hook to
// Claude Code doesn't break us.
internal static class HookHandler
{
    public static int Run(string pipeName, string[] args)
    {
        // Usage: perch hooks claude <event>
        if (args.Length < 3 || args[1] != "claude")
        {
            Console.Error.WriteLine("perch hooks: usage: perch hooks claude <event>");
            return 2;
        }
        var evt = args[2];

        // Claude Code passes the hook payload on stdin as JSON. Read it
        // (with a small budget) and try to parse — if it isn't JSON, fall
        // back to event-only behavior so we still report state transitions.
        string stdinPayload = "";
        try
        {
            if (Console.IsInputRedirected)
            {
                using var sr = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
                // 64KB cap so a runaway payload can't hang us. Claude's
                // hook payloads are tiny in practice.
                var buf = new char[64 * 1024];
                var n = sr.ReadBlock(buf, 0, buf.Length);
                stdinPayload = new string(buf, 0, n);
            }
        }
        catch { /* tolerate */ }

        JsonElement? root = null;
        if (!string.IsNullOrWhiteSpace(stdinPayload))
        {
            try
            {
                var doc = JsonDocument.Parse(stdinPayload);
                root = doc.RootElement.Clone();
            }
            catch { /* not JSON; ignore */ }
        }

        // Map Claude's event → our IPC message(s).
        switch (evt)
        {
            case "session-start":
                // Tell the host this pane is running Claude Code so its header
                // shows a "CC" badge. This wrapper only ever runs for `claude`,
                // so the tool name is definitive.
                Send(pipeName, new { type = "agent", name = "claude" });
                Send(pipeName, new { type = "status", state = "working", detail = "claude started" });
                // Re-arm pane auto-naming so the next first prompt re-titles
                // the pane to the new task — that's what makes a fresh launch
                // (ctrl+c twice → relaunch) or `/clear` pick up a new name.
                // The host skips this for source="resume" and for user-named
                // panes. `source` is Claude's SessionStart source.
                Send(pipeName, new { type = "name.reset", source = StringFrom(root, "source") });
                // Capture HEAD as the commit-count baseline for THIS cc
                // session. Host recomputes count via git rev-list on each
                // subsequent state change. No git? No baseline — nothing
                // surfaces, no harm.
                var baseline = TryGitRevParseHead();
                if (!string.IsNullOrEmpty(baseline))
                    Send(pipeName, new { type = "git.baseline", sha = baseline });
                break;

            case "session-end":
                Send(pipeName, new { type = "status", state = "idle", detail = (string?)null });
                // Clear the baseline so the count doesn't keep ticking after
                // the cc session ends.
                Send(pipeName, new { type = "git.baseline", sha = "" });
                // Drop the agent badge — the pane is back to a plain shell.
                Send(pipeName, new { type = "agent", name = "" });
                break;

            case "prompt-submit":
                // User just submitted a prompt → agent is thinking. Include
                // the prompt's leading chars as detail when present.
                Send(pipeName, new { type = "status", state = "working", detail = StringFrom(root, "prompt", maxLen: 60) ?? "thinking" });
                // Also forward the prompt as a title candidate so the host can
                // auto-name a still-unnamed pane after the first message. Send
                // a generous slice: the host cuts a ~40-char label AND keeps
                // the rest for the pane-header hover tooltip ("full original
                // message"). Skip when there's no prompt.
                var promptText = StringFrom(root, "prompt", maxLen: 400);
                if (!string.IsNullOrWhiteSpace(promptText))
                    Send(pipeName, new { type = "title", text = promptText });
                break;

            case "notification":
                // Claude's Notification hook fires for two distinct cases:
                //   1. a permission request (agent is BLOCKED, can't proceed)
                //   2. an idle nudge ("waiting for your input" after 60s)
                // Only #1 is a real call for attention. #2 fires on a turn that
                // already finished (Stop → "done"/idle) and merely sat untouched
                // for 60s — the agent is NOT blocked and nothing new happened, so
                // escalating it to a loud "waiting / needs you" cried wolf. We
                // detect the permission case by message text; for the idle nudge
                // we raise no notification and re-assert "done", leaving the pane
                // calm at idle (re-asserting also recovers a pane whose Stop hook
                // was dropped). Detect by the message text.
                var msg = StringFrom(root, "message") ?? "claude needs attention";
                var isPermission = msg.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isPermission)
                {
                    Send(pipeName, new { type = "notify", level = "warn", text = msg });
                    Send(pipeName, new { type = "status", state = "permission", detail = (string?)null });
                }
                else
                {
                    Send(pipeName, new { type = "status", state = "done", detail = (string?)null });
                }
                break;

            case "stop":
                // Turn complete — the calm "done" state, surfaced to the user as
                // "idle". The ball is in your court but the agent is NOT blocked:
                // it finished and is at rest, your move, no rush. This never
                // escalates on its own; only a genuine permission prompt (see the
                // notification case) raises the loud "needs you" state.
                Send(pipeName, new { type = "status", state = "done", detail = (string?)null });
                break;

            case "subagent-stop":
                // Subagent finished; not a state transition for the top-level
                // pane. Skip — agents tend to fire many of these.
                break;

            case "pre-tool-use":
                // Report what the agent is doing as a human verb + target —
                // "editing pane.ts", "running npm", "reading Session.cs" — so
                // the sidebar/dashboard answer "what's it doing right now?"
                // concretely instead of "using Edit".
                var tool = StringFrom(root, "tool_name") ?? StringFrom(root, "tool");
                if (!string.IsNullOrEmpty(tool))
                    Send(pipeName, new { type = "status", state = "working", detail = PrettyAction(root, tool!) });
                break;

            case "post-tool-use":
                // A tool just finished → the agent is actively working again.
                // This is the ONLY hook that fires after a permission prompt is
                // answered (approving isn't a UserPromptSubmit, and PreToolUse
                // fires before the prompt). It's what unsticks a pane from the
                // loud "permission" state before the turn's terminal Stop. No
                // detail — the host coalesces unchanged working→working pushes,
                // so this firehose is cheap (see OnAgentStatus).
                Send(pipeName, new { type = "status", state = "working", detail = (string?)null });
                break;

            default:
                // Unknown event — keep the hook fast and silent.
                break;
        }
        return 0;
    }

    /// JSON helper: pulls a string property if present, optionally truncating.
    /// Claude payloads may carry the field at the root OR nested under a
    /// wrapper, so we check both shapes.
    private static string? StringFrom(JsonElement? root, string name, int maxLen = 0)
    {
        if (root is not JsonElement el) return null;
        string? Pick(JsonElement e)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
        var s = Pick(el);
        if (s == null)
        {
            // Some Claude versions nest under "hook_input" / "data".
            foreach (var wrap in new[] { "hook_input", "data" })
            {
                if (el.TryGetProperty(wrap, out var w) && w.ValueKind == JsonValueKind.Object)
                {
                    s = Pick(w);
                    if (s != null) break;
                }
            }
        }
        if (s != null && maxLen > 0 && s.Length > maxLen) s = s.Substring(0, maxLen) + "…";
        return s;
    }

    /// Pull a string field out of the hook's `tool_input` object (Edit's
    /// file_path, Bash's command, …). Checks the root and the same wrappers
    /// StringFrom does. Null when absent.
    private static string? ToolInputString(JsonElement? root, string field)
    {
        if (root is not JsonElement el) return null;
        static string? FromHolder(JsonElement h, string f)
        {
            if (h.TryGetProperty("tool_input", out var ti) && ti.ValueKind == JsonValueKind.Object
                && ti.TryGetProperty(f, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
        var s = FromHolder(el, field);
        if (s == null)
        {
            foreach (var wrap in new[] { "hook_input", "data" })
                if (el.TryGetProperty(wrap, out var w) && w.ValueKind == JsonValueKind.Object)
                {
                    s = FromHolder(w, field);
                    if (s != null) break;
                }
        }
        return s;
    }

    /// Human "verb + target" label for a tool call — "editing pane.ts",
    /// "running npm", "reading Session.cs". Falls back to "using {tool}" for
    /// tools we don't special-case (or when the input field is missing).
    private static string PrettyAction(JsonElement? root, string tool)
    {
        static string Base(string? p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            var idx = p!.LastIndexOfAny(new[] { '/', '\\' });
            return idx >= 0 ? p.Substring(idx + 1) : p;
        }
        switch (tool)
        {
            case "Edit":
            case "MultiEdit":
            case "Write":
            case "NotebookEdit":
            {
                var f = Base(ToolInputString(root, "file_path") ?? ToolInputString(root, "notebook_path"));
                return f.Length > 0 ? $"editing {f}" : "editing a file";
            }
            case "Read":
            {
                var f = Base(ToolInputString(root, "file_path"));
                return f.Length > 0 ? $"reading {f}" : "reading a file";
            }
            case "Bash":
            {
                var cmd = (ToolInputString(root, "command") ?? "").TrimStart();
                var sp = cmd.IndexOfAny(new[] { ' ', '\n', '\t' });
                var prog = sp > 0 ? cmd.Substring(0, sp) : cmd;
                return prog.Length > 0 ? $"running {prog}" : "running a command";
            }
            case "Grep":
            case "Glob":
                return "searching";
            case "WebFetch":
                return "fetching the web";
            case "WebSearch":
                return "searching the web";
            case "Task":
                return "running a subagent";
            case "TodoWrite":
                return "planning";
            default:
                return $"using {tool}";
        }
    }

    /// Run `git rev-parse HEAD` in the cwd Claude was launched from. Returns
    /// the sha on success, null if git fails (no repo, no git on PATH, etc).
    /// Synchronous + short timeout — the hook must stay fast.
    private static string? TryGitRevParseHead()
    {
        try
        {
            using var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-parse HEAD",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            if (!p.Start()) return null;
            if (!p.WaitForExit(1500)) { try { p.Kill(); } catch { } return null; }
            if (p.ExitCode != 0) return null;
            return p.StandardOutput.ReadToEnd().Trim();
        }
        catch { return null; }
    }

    private static void Send(string pipeName, object payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(2000);
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception ex)
        {
            // Hooks must never break the agent. Log to stderr (which Claude
            // shows when verbose) and move on.
            Console.Error.WriteLine($"perch hooks: send failed: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
