using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace PerchCli;

// Handles `perch hooks <agent> <event>` callbacks fired by the coding agent
// running in a pane: Claude Code via the --settings hooks JSON our wrapper
// injects, or codex via the `perch` profile the codex wrapper writes. Both
// pass the hook context as JSON on stdin, and — this is why one handler serves
// both — codex deliberately mirrors Claude Code's event names AND its payload
// field names (session_id, cwd, hook_event_name, tool_name, tool_input,
// prompt, message). We extract what's useful and send it back to the perch
// host through the IPC pipe.
//
// Where the two genuinely differ, `agent` is the switch: which badge the pane
// wears, and which tool names PrettyAction knows how to phrase. Everything
// claude-only (peer messaging, gcloud stamping, the team room's held
// permission prompts) is reached only from events codex never fires, or is
// guarded on the agent.
//
// We intentionally accept-and-forget unknown events so adding a new hook to
// either agent doesn't break us.
internal static class HookHandler
{
    public static int Run(string pipeName, string[] args)
    {
        // Usage: perch hooks <claude|codex> <event>
        if (args.Length < 3 || args[1] is not ("claude" or "codex"))
        {
            Console.Error.WriteLine("perch hooks: usage: perch hooks <claude|codex> <event>");
            return 2;
        }
        var agent = args[1];
        var evt = args[2];
        // The one event whose HANDLING differs between the two agents, not just
        // its payload — routed to its own case rather than branching inside a
        // 90-line one. See "codex-permission" below.
        if (agent == "codex" && evt == "permission-request") evt = "codex-permission";

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

        // Diagnostic seam. PERCH_HOOK_DUMP=<file> appends every hook payload we
        // are handed, which is how the codex mapping below was written: an
        // agent's own docs never tell you the exact shape of `tool_input` for
        // the tool you care about, and guessing it produces an activity line
        // that reads "using exec" forever. Off unless the variable is set, and
        // best-effort — a diagnostic must never break a hook.
        var dump = Environment.GetEnvironmentVariable("PERCH_HOOK_DUMP");
        if (!string.IsNullOrWhiteSpace(dump))
            try { File.AppendAllText(dump!, $"=== {agent} {evt}\n{stdinPayload}\n\n"); }
            catch { }

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
                // Tell the host which agent is running here so its header shows
                // the right badge ("CC" / "CX"). The wrapper that installed this
                // hook is the definitive answer.
                Send(pipeName, new { type = "agent", name = agent });
                Send(pipeName, new { type = "status", state = "working", detail = $"{agent} started" });
                // The agent's own id for this conversation. Both agents have
                // one and both can resume from it, but they are NOT
                // interchangeable — `claude --resume <id>` vs `codex resume
                // <id>`, and two entirely different journal files — so the
                // agent travels with it and the host files it accordingly.
                // Codex also hands us the journal's exact path, which saves the
                // host a directory search.
                var sessionId = StringFrom(root, "session_id");
                if (!string.IsNullOrWhiteSpace(sessionId))
                    // `name` is the peer name this launch went out under (the
                    // wrapper read the same file moments ago and passed it as
                    // --name) — the address other sessions use in SendMessage.
                    // The host stores it per pane so it can route an observed
                    // send's target back to a sidebar row. Null when the file
                    // is absent (a pre-pairing pane): cc then auto-names from
                    // the cwd and the host falls back to the tab title.
                    // `socket` is the session's own inbox address, exported
                    // to hooks by cc: what a teammate's reply may be addressed
                    // to instead of the name. The host maps it back to us.
                    Send(pipeName, new
                    {
                        type = "session", id = sessionId, agent,
                        // Peer messaging is Claude Code's; codex has no
                        // equivalent, so neither field is looked up for it.
                        name = agent == "claude" ? ReadOwnPeerName() : null,
                        socket = agent == "claude"
                            ? NullIfBlank(Environment.GetEnvironmentVariable("CLAUDE_CODE_MESSAGING_SOCKET"))
                            : null,
                        // Where this conversation's journal lives. Codex states
                        // it outright; Claude Code doesn't, and the host finds
                        // that one from the id + cwd as it always has.
                        path = NullIfBlank(StringFrom(root, "transcript_path")),
                        // The model this launch actually got. Perch picks
                        // Claude's, so there it's already known; codex's is
                        // whatever codex resolved, and this is the only place
                        // the header can learn it.
                        model = NullIfBlank(StringFrom(root, "model")),
                    });
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
                // Codex only: refresh the model. Its user can change models
                // mid-conversation with `/model`, and its prompt payload
                // carries whatever is in force — so the header chip follows the
                // switch instead of showing what the session started on. (No id
                // is sent: this is a correction, not a new session.)
                if (agent == "codex" && StringFrom(root, "model") is { Length: > 0 } turnModel)
                    Send(pipeName, new { type = "session", agent, model = turnModel });
                // If this pane's tab has a board, point the agent at it; if
                // the pane is a team bot, hand it the current roster. One
                // object on stdout, or nothing. Claude-shaped stdout, so
                // claude only — codex validates a hook's output against its own
                // schema and would log ours as invalid.
                if (agent == "claude") EmitPromptContext();
                break;

            case "notification":
                // cc stamps every notification with a structured type (verified
                // against cc 2.1.207: permission_prompt, idle_prompt, auth_success,
                // elicitation_dialog, elicitation_complete, elicitation_response,
                // agent_needs_input, agent_completed). Key on THAT — classifying by
                // English message text is how a wording change once read a blocked
                // agent as calm. The text sniff survives only as the fallback for
                // older cc builds that don't send the field.
                //
                //   permission_prompt   → BLOCKED on a tool dialog. The loud red one.
                //   elicitation_dialog /
                //   agent_needs_input   → a question/elicitation dialog wants input.
                //                         Yellow "waiting" + the ask as the note —
                //                         gentler than permission, but it must land
                //                         in "Needs you": the turn cannot proceed.
                //   elicitation_response/
                //   elicitation_complete→ that dialog was answered; agent resumes.
                //   idle_prompt         → the 60s nudge on a turn that already
                //                         finished. NOT a block, nothing new
                //                         happened — escalating it cried wolf. We
                //                         re-assert calm "done" (which also
                //                         recovers a pane whose Stop was dropped).
                //   auth_success /
                //   agent_completed /
                //   anything future     → not a pane-state signal; stay silent so a
                //                         new cc notification kind can't corrupt
                //                         state (the buffer probe is the net).
                var msg = StringFrom(root, "message") ?? "claude needs attention";
                var notifType = StringFrom(root, "notification_type") ?? "";
                if (notifType.Length == 0)
                    notifType = msg.IndexOf("permission", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "permission_prompt"
                        : "idle_prompt";
                switch (notifType)
                {
                    case "permission_prompt":
                        Send(pipeName, new { type = "notify", level = "warn", text = msg });
                        Send(pipeName, new { type = "status", state = "permission", detail = (string?)null });
                        break;
                    case "elicitation_dialog":
                    case "agent_needs_input":
                        Send(pipeName, new { type = "notify", level = "warn", text = msg });
                        Send(pipeName, new { type = "status", state = "waiting", detail = (string?)null });
                        break;
                    case "elicitation_response":
                    case "elicitation_complete":
                        Send(pipeName, new { type = "status", state = "working", detail = (string?)null });
                        break;
                    case "idle_prompt":
                        Send(pipeName, new { type = "status", state = "done", detail = (string?)null });
                        break;
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

            case "codex-permission":
                // Codex's PermissionRequest. Three jobs, in this order.
                //
                // First, find out who answers it. Codex fires this hook for
                // every escalation, before it decides between the person and
                // its own reviewer model ("auto-review", a thread setting) -
                // and when the reviewer is on, nobody at the keyboard is asked:
                // the reviewer rules a second later and the turn carries on.
                // That is a working beat, not a wait, so it gets the quiet
                // activity line and nothing else. Answering it from here would
                // also pre-empt the reviewer, which is not what the user chose.
                var askSummary = ToolSummary(root, StringFrom(root, "tool_name") ?? "something");
                if (CodexAutoReview.IsOn(StringFrom(root, "transcript_path")))
                {
                    Send(pipeName, new { type = "status", state = "working", detail = "auto-reviewing " + askSummary });
                    return 0;
                }
                // Then say the pane is blocked. Claude Code announces its own
                // prompt through a Notification hook and this case doesn't have
                // to; codex sends no such notice, so without this line a codex
                // pane waiting on an approval would look like it was still
                // working. PostToolUse clears it the moment the command runs.
                Send(pipeName, new
                {
                    type = "notify", level = "warn",
                    text = "Codex is asking to run " + askSummary,
                });
                Send(pipeName, new { type = "status", state = "permission", detail = (string?)null });
                // Last, hold it, exactly as a Claude bot's prompt is held, so a
                // codex bot's approval can be answered from the room instead of
                // in its terminal. Returns immediately for any pane that isn't
                // a bot's, and codex then shows its own card as usual.
                return HoldPermission(pipeName, root, agent);

            case "pre-send":
                // PreToolUse, matcher "SendMessage" — the agent is messaging
                // another Claude Code session (cross-session messaging). Report
                // it as it happens: a transient "messaging <target>" activity
                // detail for the sender's row, and a structured peer.msg so the
                // host can warm the pair bracket while the note is in flight.
                // Async hook: nothing printed, nothing rewritten, never blocks.
                {
                    var sendTarget = PeerTarget(root);
                    if (sendTarget.Length > 0)
                    {
                        Send(pipeName, new
                        {
                            type = "peer.msg",
                            phase = "sending",
                            target = sendTarget,
                            text = PeerText(root),
                            message = PeerFullText(root),
                            summary = PeerSummary(root),
                        });
                        Send(pipeName, new { type = "status", state = "working", detail = $"messaging {sendTarget}" });
                    }
                }
                break;

            case "pre-bash":
                // Registered with a "Bash" matcher (NOT the "" matcher that
                // pre-tool-use would need) purely to stamp agent-attribution
                // labels onto `gcloud ... create`. Deliberately silent otherwise:
                // it emits no IPC and prints nothing for the overwhelming majority
                // of Bash calls, so it can't resurrect the status-detail firehose
                // that got PreToolUse disabled in the first place.
                return StampGcloud(pipeName, root);

            case "post-tool-use":
                // A tool just finished → the agent is actively working again.
                // This is the ONLY hook that fires after a permission prompt is
                // answered (approving isn't a UserPromptSubmit, and PreToolUse
                // fires before the prompt). It's what unsticks a pane from the
                // loud "permission" state before the turn's terminal Stop. No
                // detail — the host coalesces unchanged working→working pushes,
                // so this firehose is cheap (see OnAgentStatus).
                Send(pipeName, new { type = "status", state = "working", detail = (string?)null });
                // File-editing tools also claim the file for THIS pane, so the
                // host can split a shared working tree's loc between the agents
                // editing it (projects-mode tabs without worktrees). Only tools
                // whose input names a file — a Bash side-effect can't be claimed.
                // The rest of this case reads the agent's tool vocabulary. The
                // two overlap more than they look: codex calls its shell tool
                // "Bash" and passes `tool_input.command` as a plain string,
                // exactly like Claude Code, so the commit-claiming below is
                // shared. What differs is the file-editing tool — codex has one
                // (apply_patch) whose input is a patch script, handled just
                // after this.
                var editTool = StringFrom(root, "tool_name") ?? StringFrom(root, "tool");
                if (editTool is "Edit" or "MultiEdit" or "Write" or "NotebookEdit")
                {
                    var touched = ToolInputString(root, "file_path") ?? ToolInputString(root, "notebook_path");
                    if (!string.IsNullOrWhiteSpace(touched))
                        Send(pipeName, new { type = "git.touched", path = touched });
                }
                // Codex's editing tool. One call can touch several files, and
                // the patch header names each one absolutely, so every one is
                // claimed — that's what lets two agents sharing a working tree
                // each get billed only for their own lines.
                else if (editTool is "apply_patch")
                {
                    foreach (var path in CodexPatch.Files(ToolInputString(root, "command")))
                        Send(pipeName, new { type = "git.touched", path });
                }
                // A Bash `git commit` that just ran: claim its new sha(s) for
                // THIS pane, parsed from the "[branch abc1234]" marker in the
                // tool's OUTPUT. Output-parse, never `rev-parse HEAD` — this
                // hook also fires when the commit FAILED, and HEAD would then
                // name someone else's commit. The claim is what lets the
                // sidebar split "↑N unpushed" per tab on a shared branch.
                else if (editTool is "Bash")
                {
                    var bashCmd = ToolInputString(root, "command") ?? "";
                    if (bashCmd.IndexOf("git commit", StringComparison.OrdinalIgnoreCase) >= 0)
                        foreach (var sha in CommitShaMarkers(RawToolResponse(root)))
                            Send(pipeName, new { type = "git.commit", sha });
                }
                // A cross-session SendMessage just completed. This is the
                // delivered/failed verdict the pre-send phase can't know: the
                // host turns success into a "from <sender>" note on the
                // RECEIVING tab's row, and failure into a warn on the sender's.
                else if (editTool is "SendMessage")
                {
                    var sentTarget = PeerTarget(root);
                    if (sentTarget.Length > 0)
                    {
                        var sendOk = PeerSendOk(root);
                        Send(pipeName, new
                        {
                            type = "peer.msg",
                            phase = "sent",
                            target = sentTarget,
                            text = PeerText(root),
                            ok = sendOk,
                            message = PeerFullText(root),
                            summary = PeerSummary(root),
                            // Only for a failure, and only the first sentence:
                            // the room says "couldn't reach Ada — <why>" instead
                            // of showing the undelivered body as if it landed.
                            reason = sendOk ? null : PeerVerdict.Reason(RawToolResponse(root)),
                        });
                    }
                }
                break;

            case "permission-request":
                // Claude Code is about to show a permission prompt (in any mode
                // that asks, auto included for what auto never approves). For a
                // team bot's pane, hold it and let the owner answer from the
                // room; for any other pane, stay silent and the prompt shows.
                return HoldPermission(pipeName, root, agent);

            case "permission-denied":
                // Auto mode's classifier blocked a tool call. Information for
                // the room; nothing to answer.
                if (Environment.GetEnvironmentVariable("PERCH_PANE_ID") is { Length: > 0 } dpane
                    && File.Exists(Path.Combine(Path.GetTempPath(), $"perch-team-{dpane}.txt")))
                {
                    var dtool = StringFrom(root, "tool_name") ?? "tool";
                    Send(pipeName, new
                    {
                        type = "perm.denied", tool = dtool, summary = ToolSummary(root, dtool),
                        reason = StringFrom(root, "reason", maxLen: 300),
                    });
                }
                break;

            default:
                // Unknown event — keep the hook fast and silent.
                break;
        }
        return 0;
    }

    // ---- permission cards --------------------------------------------------

    /// How long PermissionRequest waits for the room's answer before letting
    /// Claude Code show its own prompt. Under the hook's 590 s timeout so the
    /// exit is ours, not a kill.
    private static readonly TimeSpan PermWait = TimeSpan.FromSeconds(570);
    private static readonly TimeSpan PermPoll = TimeSpan.FromMilliseconds(250);

    /// The PermissionRequest hook body: tell the host what the bot wants to
    /// do, then poll for the owner's decision file. On `allow`/`deny`, print
    /// the decision JSON (the only thing cc reads); on timeout print nothing,
    /// so the normal prompt appears in the terminal. Exit 0 either way — a
    /// non-zero exit would make cc ignore even a valid decision.
    private static int HoldPermission(string pipeName, JsonElement? root, string agent)
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrWhiteSpace(paneId)) return 0;
            if (!File.Exists(Path.Combine(Path.GetTempPath(), $"perch-team-{paneId}.txt"))) return 0;   // not a bot's pane

            var id = Guid.NewGuid().ToString("N")[..12];
            var tool = StringFrom(root, "tool_name") ?? "tool";
            Send(pipeName, new
            {
                type = "perm.ask", id, tool,
                summary = ToolSummary(root, tool),
                input = ToolInputJson(root),
                suggestions = PermissionSuggestions(root),
            });

            var answerPath = Path.Combine(Path.GetTempPath(), $"perch-perm-{id}.txt");
            var deadline = DateTime.UtcNow + PermWait;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(answerPath))
                {
                    string answer;
                    try { answer = File.ReadAllText(answerPath).Trim().ToLowerInvariant(); }
                    catch { Thread.Sleep(PermPoll); continue; }   // the host may still be writing it
                    try { File.Delete(answerPath); } catch { }
                    if (answer is "allow" or "deny")
                    {
                        // Same field, two shapes: Claude Code takes the decision
                        // as a bare word, codex as an object with a `behavior`.
                        // Sending the wrong one is silent — the agent logs
                        // "invalid hook JSON output" and just shows its own
                        // prompt — so the shape follows the agent, not a guess.
                        object decision = agent == "codex"
                            ? new { behavior = answer }
                            : answer;
                        Console.Out.Write(JsonSerializer.Serialize(new
                        {
                            hookSpecificOutput = new { hookEventName = "PermissionRequest", decision },
                        }, JsonOpts));
                        Console.Out.Flush();
                    }
                    return 0;
                }
                Thread.Sleep(PermPoll);
            }
            Console.Error.WriteLine("perch hooks: no answer from the room; showing the prompt here");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch hooks: permission card failed: {ex.Message}");
            return 0;
        }
    }

    /// One line saying what the tool wants to do: the command for Bash, the
    /// file for the editing tools, the tool name otherwise.
    private static string ToolSummary(JsonElement? root, string tool)
    {
        string? s = tool switch
        {
            // codex's editing tool: its whole input is a patch script, and
            // dumping that into a one-line summary reads as noise ("Codex is
            // asking to run *** Begin Patch *** Add File: …"). The files it
            // touches are the answer to "what does it want to do".
            "apply_patch" => CodexPatch.Files(ToolInputString(root, "command")) is { Count: > 0 } fs
                ? "edit " + string.Join(", ", fs)
                : "apply a patch",
            "Bash" or "PowerShell" => ToolInputString(root, "command"),
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" or "Read" => ToolInputString(root, "file_path") ?? ToolInputString(root, "notebook_path"),
            "WebFetch" => ToolInputString(root, "url"),
            _ => ToolInputString(root, "description") ?? ToolInputString(root, "command"),
        };
        s = (s ?? tool).Trim().Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 300 ? s.Substring(0, 300) + "…" : s;
    }

    /// The raw tool_input object as JSON text, capped at 4 KB, for the card's
    /// details.
    private static string? ToolInputJson(JsonElement? root)
    {
        if (root is not JsonElement el) return null;
        foreach (var container in new[] { el, Wrapped(el, "hook_input"), Wrapped(el, "data") })
        {
            if (container is JsonElement c && c.TryGetProperty("tool_input", out var ti))
            {
                var raw = ti.GetRawText();
                return raw.Length > 4096 ? raw.Substring(0, 4096) + "…" : raw;
            }
        }
        return null;
    }

    private static string[]? PermissionSuggestions(JsonElement? root)
    {
        if (root is not JsonElement el || !el.TryGetProperty("permission_suggestions", out var arr)
            || arr.ValueKind != JsonValueKind.Array) return null;
        var list = new System.Collections.Generic.List<string>();
        foreach (var s in arr.EnumerateArray())
            if (s.ValueKind == JsonValueKind.Object && s.TryGetProperty("rule", out var r) && r.ValueKind == JsonValueKind.String)
                list.Add(r.GetString() ?? "");
        return list.Count == 0 ? null : list.ToArray();
    }

    private static JsonElement? Wrapped(JsonElement el, string name)
        => el.TryGetProperty(name, out var w) && w.ValueKind == JsonValueKind.Object ? w : null;

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// PreToolUse(Bash): if the command creates a billable GCP resource, rewrite
    /// it to carry agent-attribution labels, and tell the host so it can snapshot
    /// the pane's name + task into the ledger (the hook itself can't know either —
    /// it's a fresh process with no access to PaneNode).
    ///
    /// Prints Claude Code's `updatedInput` payload on stdout. We deliberately do
    /// NOT return a permissionDecision: that would auto-approve every cloud
    /// create, silently bypassing the user's permission prompt. We want to
    /// rewrite the command, not to authorize it.
    ///
    /// Any failure here must leave the command untouched — a bookkeeping label is
    /// never worth breaking the agent's actual work over.
    private static int StampGcloud(string pipeName, JsonElement? root)
    {
        try
        {
            if (StringFrom(root, "tool_name") is not "Bash") return 0;
            var command = ToolInputString(root, "command");
            var kind = GcloudLabels.Detect(command);
            if (kind == GcloudLabels.Kind.None) return 0;

            var session = StringFrom(root, "session_id") ?? "";
            var pane    = Environment.GetEnvironmentVariable("PERCH_PANE_ID") ?? "";
            var owner   = Environment.GetEnvironmentVariable("USERNAME")
                          ?? Environment.GetEnvironmentVariable("USER")
                          ?? "";

            var labels = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, string>>();
            // agent-owner is the HUMAN, and it's the filter key: without it, two
            // people running Perch against the same project would see (and could
            // delete) each other's machines.
            if (owner.Length   > 0) labels.Add(new("agent-owner",   owner));
            if (session.Length > 0) labels.Add(new("agent-session", session));
            if (pane.Length    > 0) labels.Add(new("agent-pane",    pane));
            if (labels.Count == 0) return 0;

            var stamped = GcloudLabels.Stamp(command!, labels);
            if (ReferenceEquals(stamped, command) || stamped == command) return 0;

            // Let the host record what the label can't hold: the pane's name and
            // the prompt behind this resource. Label values are capped at 63 chars
            // of [a-z0-9_-], so a sentence simply cannot go there.
            Send(pipeName, new
            {
                type = "cloud.stamped",
                session,
                kind = kind == GcloudLabels.Kind.Instance ? "instance" : "cluster",
            });

            // Echo back the whole tool_input with only `command` swapped, so we
            // don't drop sibling fields (description, timeout, …).
            var updated = new System.Collections.Generic.Dictionary<string, object?>();
            if (root is JsonElement el && el.TryGetProperty("tool_input", out var ti)
                && ti.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ti.EnumerateObject())
                    updated[p.Name] = p.Name == "command" ? stamped : JsonNode(p.Value);
            }
            updated["command"] = stamped;

            Console.Out.Write(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "PreToolUse",
                    updatedInput = updated,
                },
            }, JsonOpts));
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            // Never break the agent over a label.
            Console.Error.WriteLine($"perch hooks: gcloud stamp failed: {ex.Message}");
        }
        return 0;
    }

    /// Tell the agent its tab has a reference board, and/or that it is a bot
    /// on a team with these colleagues, by printing Claude Code's
    /// `additionalContext` on stdout.
    ///
    /// ## One object, or nothing
    ///
    /// Claude Code reads stdout as ONE JSON object. Two parts (board + roster)
    /// are therefore joined into a single additionalContext string rather than
    /// printed as two objects — the second would be a parse error at best and
    /// raw context at worst. No parts → print nothing (see the rules below).
    ///
    /// ## Why UserPromptSubmit and not SessionStart
    ///
    /// SessionStart fires once. A board created (or first filled in) AFTER
    /// `claude` started would never reach the agent until /clear — the same
    /// limitation ClaudeModelState has, which the app works around by typing
    /// into the PTY behind the setup overlay. Per-turn injection is what makes
    /// "keep throwing things on the board while it works" actually true, which
    /// is the entire point of the feature.
    ///
    /// ## Why only the PATH, never the contents
    ///
    /// One short line, so the per-turn token cost is negligible, and nothing
    /// can go stale: the board behind the path is always current, whereas
    /// injected contents would be a snapshot of whenever the turn started.
    ///
    /// ## The rules this code lives under
    ///
    /// It runs inside EVERY Claude pane, synchronously, on the agent's critical
    /// path, under a timeout, and its stderr goes to the PTY rather than to
    /// errors.log. So: catch everything, never throw, print nothing at all when
    /// there is no board, and keep every diagnostic on stderr — stray stdout
    /// from this hook becomes context.
    private static void EmitPromptContext()
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrWhiteSpace(paneId)) return;

            var parts = new System.Collections.Generic.List<string>(2);
            var board = BoardContextLine(paneId);
            if (board != null) parts.Add(board);
            var roster = TeamContextBlock(paneId);
            if (roster != null) parts.Add(roster);
            if (parts.Count == 0) return;   // silence is the contract

            Console.Out.Write(JsonSerializer.Serialize(new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "UserPromptSubmit",
                    additionalContext = string.Join("\n\n", parts),
                },
            }, JsonOpts));
            Console.Out.Flush();
        }
        catch (Exception ex)
        {
            // Never cost the user a turn over a context hint.
            Console.Error.WriteLine($"perch hooks: prompt context failed: {ex.Message}");
        }
    }

    /// The board hint: the PATH of the tab's board.md, never its contents.
    /// Null when this pane's tab has no (readable) board.
    private static string? BoardContextLine(string paneId)
    {
        try
        {
            var marker = Path.Combine(Path.GetTempPath(), $"perch-board-{paneId}.txt");
            if (!File.Exists(marker)) return null;

            var dir = File.ReadAllText(marker).Trim();
            if (dir.Length == 0 || !Directory.Exists(dir)) return null;

            var index = Path.Combine(dir, "board.md");
            if (!File.Exists(index)) return null;

            return $"This tab has a reference board at {index} — context the user collected for this task "
                + "(files, screenshots, cached pages, notes). Read it when you need background, and "
                + "re-read it later if you need to: the user adds to it while you work. "
                + "Paths inside it are relative to the repository root.";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch hooks: board context failed: {ex.Message}");
            return null;
        }
    }

    /// Cap on the roster text we inline per turn. The roster is a few lines
    /// per teammate plus etiquette — a 5-bot team is ~1.5 KB — so anything
    /// past this is a bug or a tampered file, and we cut rather than pay for
    /// it on every prompt.
    private const int RosterMaxBytes = 6 * 1024;

    /// The team roster: unlike the board this IS inlined, because it is
    /// small, changes when a teammate joins or leaves, and is the one thing
    /// a bot must have without being told to go and read a file. Null when
    /// this pane is not a bot. Same containment rule as the wrapper's brief
    /// pointer: the file must be a .md under a `.perch\team\` folder.
    private static string? TeamContextBlock(string paneId)
    {
        try
        {
            var marker = Path.Combine(Path.GetTempPath(), $"perch-team-{paneId}.txt");
            if (!File.Exists(marker)) return null;

            var path = File.ReadAllText(marker).Trim();
            if (path.Length == 0 || path.Length > 260) return null;
            foreach (var c in path)
                if (char.IsControl(c) || c is '"' or '\'') return null;
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
            if (path.IndexOf(@"\.perch\team\", StringComparison.OrdinalIgnoreCase) < 0
                && path.IndexOf("/.perch/team/", StringComparison.OrdinalIgnoreCase) < 0) return null;
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path).Trim();
            if (text.Length == 0) return null;
            if (Encoding.UTF8.GetByteCount(text) > RosterMaxBytes)
            {
                // Cut on characters, not bytes: never split a surrogate pair.
                var cut = Math.Min(text.Length, RosterMaxBytes);
                while (cut > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, cut)) > RosterMaxBytes) cut--;
                text = text.Substring(0, cut).TrimEnd() + "\n[roster truncated]";
            }
            return text;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch hooks: team context failed: {ex.Message}");
            return null;
        }
    }

    /// Re-materialize a JsonElement as something JsonSerializer will round-trip
    /// faithfully, so echoing tool_input back doesn't mangle non-string fields.
    private static object? JsonNode(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _ => JsonSerializer.Deserialize<JsonElement>(e.GetRawText()),
    };

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

    /// The raw JSON text of the hook's tool_response, whatever shape this cc
    /// version gives it (string, {stdout,...} object, array of content
    /// blocks). We only regex it for commit markers, so shape doesn't matter.
    private static string RawToolResponse(JsonElement? root)
    {
        if (root is not JsonElement el) return "";
        static string From(JsonElement h)
            => h.TryGetProperty("tool_response", out var tr) ? tr.GetRawText() : "";
        var s = From(el);
        if (s.Length == 0)
            foreach (var wrap in new[] { "hook_input", "data" })
                if (el.TryGetProperty(wrap, out var w) && w.ValueKind == JsonValueKind.Object)
                { s = From(w); if (s.Length > 0) break; }
        return s;
    }

    /// Short shas from `git commit`'s "[branch abc1234] subject" marker —
    /// also matches "[main (root-commit) abc1234]" and "[detached HEAD
    /// abc1234]". Requires whitespace or '(' before the hex run so a JSON
    /// array like [1234567] can't masquerade as one. Capped at 4: one Bash
    /// call rarely commits more, and a pathological match list must not
    /// flood the pipe.
    internal static System.Collections.Generic.List<string> CommitShaMarkers(string text)
    {
        var shas = new System.Collections.Generic.List<string>();
        if (string.IsNullOrEmpty(text)) return shas;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(text, @"[\s(]([0-9a-f]{7,40})\]"))
        {
            var sha = m.Groups[1].Value;
            if (!shas.Contains(sha)) shas.Add(sha);
            if (shas.Count >= 4) break;
        }
        return shas;
    }

    /// The target session name of a SendMessage tool call. cc has shipped the
    /// field under more than one name across versions, so probe the plausible
    /// spellings; "" when none is present (caller then stays silent).
    private static string PeerTarget(JsonElement? root)
    {
        foreach (var f in new[] { "to", "target", "session", "recipient", "session_name" })
        {
            var v = ToolInputString(root, f);
            if (!string.IsNullOrWhiteSpace(v)) return v!.Trim();
        }
        return "";
    }

    /// The message body of a SendMessage tool call, cut to a note-sized line.
    /// Same multi-spelling probe as PeerTarget; "" when absent.
    private static string PeerText(JsonElement? root)
    {
        foreach (var f in new[] { "summary", "message", "content", "prompt", "text" })
        {
            var v = ToolInputString(root, f);
            if (!string.IsNullOrWhiteSpace(v))
            {
                var s = v!.Trim().Replace('\n', ' ').Replace('\r', ' ');
                return s.Length > 140 ? s.Substring(0, 140) + "…" : s;
            }
        }
        return "";
    }

    /// The FULL body of a SendMessage tool call, newlines intact, for the
    /// team room. Capped well under the 64 KB stdin read so a huge message
    /// can't make the IPC line unbounded; null when absent. `summary` is
    /// deliberately NOT a fallback here — that is what PeerSummary is for.
    private static string? PeerFullText(JsonElement? root)
    {
        foreach (var f in new[] { "message", "content", "prompt" })
        {
            var v = ToolInputString(root, f);
            if (string.IsNullOrWhiteSpace(v)) continue;
            var s = v!.Trim();
            return s.Length > 16 * 1024 ? s.Substring(0, 16 * 1024) + "…" : s;
        }
        return null;
    }

    /// The sender's own one-line summary of a SendMessage, when it gave one.
    private static string? PeerSummary(JsonElement? root)
    {
        var v = ToolInputString(root, "summary");
        if (string.IsNullOrWhiteSpace(v)) return null;
        var s = v!.Trim().Replace('\n', ' ').Replace('\r', ' ');
        return s.Length > 200 ? s.Substring(0, 200) + "…" : s;
    }

    /// Did the send actually deliver? cc answers with an explicit `success`
    /// flag; PeerVerdict reads it (and falls back to a prose sniff for a
    /// response without one). Shared with the host so the room's row and this
    /// hook can't disagree — see PeerVerdict.cs for why the old sniff alone
    /// was wrong.
    private static bool PeerSendOk(JsonElement? root) => PeerVerdict.Ok(RawToolResponse(root));

    /// This pane's own peer name: the address other sessions use in
    /// SendMessage. Prefers the name wrap-claude recorded it ACTUALLY passed
    /// (perch-claude-launched-name-*), which honours a caller's own --name;
    /// falls back to the host's intended name (perch-claude-name-*) for a
    /// wrapper that predates the record. Null when neither exists.
    private static string? ReadOwnPeerName()
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrEmpty(paneId)) return null;
            foreach (var file in new[] { $"perch-claude-launched-name-{paneId}.txt", $"perch-claude-name-{paneId}.txt" })
            {
                var path = Path.Combine(Path.GetTempPath(), file);
                if (!File.Exists(path)) continue;
                var name = File.ReadAllText(path).Trim();
                if (name.Length is > 0 and <= 60) return name;
            }
            return null;
        }
        catch { return null; }
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

            // ---- codex's vocabulary ------------------------------------
            // Measured against codex-cli 0.153.2, not guessed (PERCH_HOOK_DUMP
            // above is how). Codex names its shell tool "Bash" and passes
            // `tool_input.command` as a plain string, so the Bash case above
            // already covers it exactly. The one tool that is genuinely its
            // own is apply_patch, whose entire input is a patch script:
            //
            //   *** Begin Patch
            //   *** Update File: C:\tmp\notes.md
            //   @@
            //   +line two
            //   *** End Patch
            case "apply_patch":
            {
                var files = CodexPatch.Files(ToolInputString(root, "command"));
                if (files.Count == 0) return "editing files";
                return files.Count == 1
                    ? $"editing {Base(files[0])}"
                    : $"editing {files.Count} files";
            }
            case "update_plan":
                return "planning";
            case "view_image":
                return "looking at an image";
            case "web_search":
                return "searching the web";

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
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        // Three tries. On macOS/Linux the pipe is a Unix socket whose path
        // can be missing for an instant while the host recycles its listener
        // (see PerchIpcServer._anchor for the fix on that side); a second
        // attempt 100 ms later lands. Windows never needs the retry.
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(2000);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return;
            }
            catch (Exception ex) { last = ex; }
            System.Threading.Thread.Sleep(100);
        }
        // Hooks must never break the agent. Log to stderr (which Claude
        // shows when verbose) and move on.
        Console.Error.WriteLine($"perch hooks: send failed: {last?.Message}");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
