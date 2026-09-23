using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Perch;

/// Project chats and their threads.
///
/// A PROJECT CHAT is a tab whose Claude acts as the user's chief of staff: it
/// is briefed in plain words, splits the work into THREADS and assembles what
/// comes back. A thread is an ordinary Perch tab — its own Claude, its own git
/// worktree and branch — so it can be opened, watched and steered like any
/// other. What this class adds is the wiring between them:
///
///   * `perch thread new|send|list|read|close` from the coordinator (and
///     `perch thread send lead …` from a thread), answered through a temp
///     file the CLI is waiting on;
///   * a thread's finished turn is captured from its transcript and the
///     coordinator is told, in one short line, to read it;
///   * every line typed into a Claude goes through LineDelivery, so it waits
///     for that Claude to be free and is confirmed as submitted.
internal sealed class ThreadController
{
    internal sealed class Host
    {
        public required Func<IEnumerable<Session>> Sessions { get; init; }
        public required Func<Guid, Session?> SessionById { get; init; }
        public required Func<Guid, Project?> ProjectById { get; init; }
        /// Make a Claude tab under a project: title, system prompt file, first
        /// prompt, own worktree or not. Null when it could not be made (the
        /// reason has been toasted).
        public required Func<Project, string, string, string?, bool, Task<Session?>> CreateClaudeTab { get; init; }
        /// The text of the tab's Claude's last finished turn, read from its
        /// transcript; null when there is none yet.
        public required Func<Session, Task<string?>> ReadLastReply { get; init; }
        public required Action<Guid> CloseSession { get; init; }
        public required Action<Guid> AcceptTrust { get; init; }
        public required Action Save { get; init; }
        public required Action PushState { get; init; }
        public required Action<string> Toast { get; init; }
        public required LineDelivery.Host Delivery { get; init; }
        /// A project chat drawn by the page: tell it something (a notice row
        /// and the text its next turn gets). False when the lead is an older
        /// terminal project chat, which is typed into instead.
        public Func<Session, string, string, bool>? NotifyChat { get; init; }
        /// Show a notice in a project chat without giving it to Claude — for
        /// things only the user can act on. `threadId` makes it openable.
        public Action<Session, string, Guid?>? InformChat { get; init; }
    }

    /// What a project chat and its threads may run without a permission
    /// prompt: their own `perch thread` commands, and nothing else.
    public const string AllowedTools = "Bash(perch thread:*)";

    /// The same rule for each shell tool Claude Code may run commands with:
    /// Bash everywhere, and PowerShell on Windows when it is enabled — a
    /// pattern for one never matches a command run through the other.
    internal static IEnumerable<string> ForShells(params string[] commands) =>
        commands.SelectMany(c => new[] { $"Bash({c}:*)", $"PowerShell({c}:*)" });

    /// What a thread may do without asking, on top of editing files (it runs
    /// with acceptEdits): its `perch thread` commands and committing on its
    /// own branch. A thread's worktree is its own, so none of this can touch
    /// the user's checkout; pushing, installing and the rest still ask.
    public static readonly string[] ThreadAllowedTools =
        ForShells("perch thread", "git add", "git commit", "git status", "git diff", "git log").ToArray();

    /// Threads last seen blocked on the user, so each wait is announced once.
    private readonly HashSet<Guid> _waiting = new();

    private readonly Host _h;
    public LineDelivery Delivery { get; }

    public ThreadController(Host host)
    {
        _h = host;
        Delivery = new LineDelivery(host.Delivery);
    }

    /// Where a project chat keeps its generated prompts. Machine-local and
    /// outside the repo: the prompts are Perch's, not the project's.
    public static string DirFor(Session lead) =>
        Path.Combine(AppPaths.DataRoot, "perch", "threads", lead.Id.ToString("N"));

    public IEnumerable<Session> ThreadsOf(Session lead) =>
        _h.Sessions().Where(s => s.ThreadOf == lead.Id).OrderBy(s => s.ThreadNumber);

    // ---- the coordinator ---------------------------------------------------

    /// Write the coordinator's system prompt for a new project chat; returns
    /// its path.
    public static string WriteCoordinatorPrompt(Session lead, Project proj)
    {
        var dir = DirFor(lead);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "coordinator.md");
        AtomicFile.WriteAllText(path, CoordinatorPrompt(proj));
        return path;
    }

    internal static string CoordinatorPrompt(Project proj) => $"""
        You are the coordinator of a project chat in Perch, for the project "{proj.Name}" at `{proj.Path}`.
        The user briefs you the way they would brief a chief of staff. You turn what they ask for into work, hand the work to threads, check what comes back, and put the result together for them.

        ## Threads
        A thread is a separate Claude Code session with its own git worktree and branch of this repo. The user sees them in the Threads panel beside you and can open any of them. Run these with Bash:

        - `perch thread new "<title>" --brief "<brief>"` starts a thread and prints its number. The brief is everything the thread knows, so make it complete: the goal, the context and files that matter, constraints, what "done" looks like, and what to report back. For a long brief write it to a file and use `--brief-file <path>`.
        - `perch thread send <n> "<message>"` steers a running thread. It is delivered when that thread is free.
        - `perch thread list` shows every thread with its state and last report.
        - `perch thread read <n>` prints a thread's last full report.
        - `perch thread close <n>` closes a thread's tab; its branch and commits stay.

        When a thread finishes a turn, a message starting with `[Perch` arrives saying so, usually with its report. Check the report (`perch thread read <n>` if it isn't included), then decide: follow up with that thread, start others, or report to the user. A thread can also ask you something mid-task the same way.

        Never wait for threads: no sleeping, no scheduling a wake-up, no checking `perch thread list` over and over. Once the threads are started, tell the user briefly what is running and end your turn. Perch starts your next turn when a thread reports or asks you something. If a thread is stuck waiting for the user's permission, the user is told directly; you don't need to watch for it.

        ## How to work
        - Scope the request first. If what the user wants is unclear, ask before starting threads.
        - You can read and search this repo, look at git history and diffs, and search the web, but you can't edit files or run other commands. Anything that changes something is a thread's job, and so is anything substantial. Answer quick questions yourself. Give independent pieces separate threads so they run in parallel. Ask the user before running more than four at once.
        - Threads commit on their own branches. You assemble the result: when the user asks you to merge, or asked you to finish the whole job, merge each thread's branch into the branch checked out here with `git merge --no-ff <branch>`. Never push.
        - If a merge clashes, run `git merge --abort` (you don't edit files, so you can't resolve it yourself) and send that thread: `perch thread send <n> "Merge <this branch> into your branch, resolve the conflicts, commit, and report."` When it reports, merge again.
        - Before merging, check what a thread did (`git log`, `git diff <main>...<branch>`) and tell the user if something looks wrong instead of merging it.
        - Keep the user posted briefly and lead with results. Messages starting with `[Perch` come from Perch, not from the user; you may get several in one turn, together with what the user wrote.
        """;

    // ---- a thread ----------------------------------------------------------

    internal static string ThreadPrompt(int n, string title, string brief) => $"""
        You are thread {n} of a project chat in Perch: "{title}". A coordinator Claude gave you this task and will review your result.

        - You work in your own git worktree on your own branch (`git branch --show-current`). Commit your work there. Don't push or merge unless the brief says to.
        - When you finish, or you are blocked, end your turn with a short report: first line, the outcome in one sentence; then what you did, what's left, and anything the coordinator has to decide. That report goes to the coordinator on its own.
        - To ask the coordinator something mid-task, run `perch thread send lead "<question>"` and carry on with what you can.
        - Messages from the coordinator arrive as lines starting with `[Perch #…]`.

        ## Your brief
        {brief}
        """;

    // ---- perch thread … ----------------------------------------------------

    public void OnThreadCommand(Session sess, Guid paneId, ThreadMessage m) => _ = HandleAsync(sess, m);

    private async Task HandleAsync(Session sess, ThreadMessage m)
    {
        string reply;
        try { reply = await RunAsync(sess, m); }
        catch (Exception ex)
        {
            Log.Error("Thread.command", ex);
            reply = "error\n" + ex.Message;
        }
        WriteReply(m.ReqId, reply);
    }

    private async Task<string> RunAsync(Session sess, ThreadMessage m)
    {
        var lead = sess.IsLead ? sess : sess.ThreadOf is Guid lid ? _h.SessionById(lid) : null;
        if (lead == null)
            return "error\nThis tab isn't a project chat or one of its threads.";
        var fromThread = !sess.IsLead;

        switch (m.Op)
        {
            case "new":
            {
                if (fromThread) return "error\nOnly the project chat starts threads. Ask it with: perch thread send lead \"…\"";
                var proj = lead.ProjectId is Guid pid ? _h.ProjectById(pid) : null;
                if (proj == null) return "error\nThis project chat's project is no longer registered.";
                var title = (m.Title ?? "").Trim();
                var n = ++lead.ThreadNumber;   // on the lead: the last number handed out
                _h.Save();
                var dir = DirFor(lead);
                Directory.CreateDirectory(dir);
                var promptPath = Path.Combine(dir, $"thread-{n}.md");
                AtomicFile.WriteAllText(promptPath, ThreadPrompt(n, title, m.Brief ?? ""));
                var tab = await _h.CreateClaudeTab(proj, title, promptPath, "Start on the task in your brief.", true);
                if (tab == null) return "error\nPerch couldn't make the thread's tab (see the toast in Perch).";
                tab.ThreadOf = lead.Id;
                tab.ThreadNumber = n;
                _h.Save();
                _h.PushState();
                Log.Info("Thread.new", $"lead={lead.Id:N} n={n} session={tab.Id:N}");
                return $"ok\n{n}\n";
            }
            case "send":
            {
                var text = (m.Text ?? "").Trim();
                if (!string.IsNullOrEmpty(m.File))
                    text = (text.Length > 0 ? text + " " : "") + $"(The full message is in {m.File} — read it.)";
                if (text.Length > LineDelivery.MaxChars)
                {
                    var path = Path.Combine(DirFor(lead), $"message-{Guid.NewGuid():N}.md");
                    Directory.CreateDirectory(DirFor(lead));
                    AtomicFile.WriteAllText(path, text);
                    text = $"(A long message — read it in {path}.)";
                }
                if ((m.Target ?? "").Equals("lead", StringComparison.OrdinalIgnoreCase))
                {
                    if (!fromThread) return "error\nYou are the project chat.";
                    TellLead(lead, $"Thread {sess.ThreadNumber} ({sess.Title}) asks: {text}",
                        $"Thread {sess.ThreadNumber} ({sess.Title}) asks: {text}");
                    return "ok\nQueued for the project chat.\n";
                }
                if (fromThread) return "error\nA thread can only message the project chat: perch thread send lead \"…\"";
                var t = Find(lead, m.Target);
                if (t == null) return $"error\nNo thread {m.Target}. Run perch thread list.";
                Delivery.Enqueue(t.Id, $"From the project chat: {text}");
                return $"ok\nQueued for thread {t.ThreadNumber}; it goes in when that thread is free.\n";
            }
            case "list":
            {
                var sb = new StringBuilder("ok\n");
                var any = false;
                foreach (var t in ThreadsOf(lead))
                {
                    any = true;
                    var head = FirstLine(t.ThreadLastReply, 120);
                    sb.Append($"{t.ThreadNumber}. {t.Title} — {StateWord(t)}");
                    if (t.WorktreeBranch.Length > 0) sb.Append($" — branch {t.WorktreeBranch}");
                    sb.Append('\n');
                    if (head.Length > 0) sb.Append($"   last report: {head}\n");
                }
                if (!any) sb.Append("No threads yet. Start one with perch thread new \"<title>\" --brief \"…\"\n");
                return sb.ToString();
            }
            case "read":
            {
                var t = fromThread ? null : Find(lead, m.Target);
                if (t == null) return $"error\nNo thread {m.Target}. Run perch thread list.";
                var fresh = await _h.ReadLastReply(t);
                if (!string.IsNullOrWhiteSpace(fresh) && fresh != t.ThreadLastReply) Record(t, fresh);
                return t.ThreadLastReply.Length > 0
                    ? $"ok\nThread {t.ThreadNumber} ({t.Title}), {StateWord(t)}:\n\n{t.ThreadLastReply}\n"
                    : $"ok\nThread {t.ThreadNumber} ({t.Title}) hasn't reported yet ({StateWord(t)}).\n";
            }
            case "close":
            {
                var t = fromThread ? null : Find(lead, m.Target);
                if (t == null) return $"error\nNo thread {m.Target}. Run perch thread list.";
                _h.CloseSession(t.Id);
                return $"ok\nClosed thread {t.ThreadNumber}. Its branch {t.WorktreeBranch} and its commits are kept.\n";
            }
            default:
                return "error\nUnknown thread command.";
        }
    }

    private Session? Find(Session lead, string? target)
    {
        var s = (target ?? "").Trim().TrimStart('#');
        return int.TryParse(s, out var n) ? ThreadsOf(lead).FirstOrDefault(t => t.ThreadNumber == n) : null;
    }

    private static string StateWord(Session s)
    {
        if (s.Dormant) return "asleep";
        var states = PaneTree.AllLeaves(s.Root).Where(p => p.IsTerminal).Select(p => p.AgentState).ToList();
        if (states.Contains(AgentState.Permission)) return "waiting for a permission";
        if (states.Contains(AgentState.Waiting)) return "waiting";
        if (states.Contains(AgentState.Working)) return "working";
        if (states.Contains(AgentState.Done)) return "finished its turn";
        return "idle";
    }

    internal static string FirstLine(string text, int max)
    {
        // A one-line summary is shown as plain text, so drop Markdown's marks.
        var plain = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\*\*|__|`|^#+\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        var l = plain.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0) ?? "";
        return l.Length > max ? l[..max].TrimEnd() + "…" : l;
    }

    private static void WriteReply(string? reqId, string reply)
    {
        if (string.IsNullOrEmpty(reqId) || reqId.Any(c => !char.IsLetterOrDigit(c))) return;
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"perch-thread-{reqId}.txt");
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, reply);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) { Log.Error("Thread.reply", ex); }
    }

    // ---- hooks -------------------------------------------------------------

    /// A status hook from a tab's agent (before AppController applies it).
    public void OnAgentStatus(Session sess, StatusMessage msg)
    {
        switch (msg.State)
        {
            case "working":
                _waiting.Remove(sess.Id);
                if (!string.IsNullOrEmpty(msg.Detail)) Delivery.OnPromptSubmitted(sess.Id, msg.Detail);
                break;
            case "done":
                Delivery.OnFree(sess.Id);
                _waiting.Remove(sess.Id);
                if (sess.ThreadOf != null) _ = CaptureAsync(sess);
                break;
            case "permission":
            case "waiting":
                // A thread stuck on a question only the user can answer: say so
                // in the chat, once per wait, with a way to go and answer it.
                if (sess.ThreadOf is Guid lid && _waiting.Add(sess.Id) && _h.SessionById(lid) is Session lead)
                    _h.InformChat?.Invoke(lead,
                        msg.State == "permission"
                            ? $"Thread {sess.ThreadNumber} ({sess.Title}) is waiting for your permission."
                            : $"Thread {sess.ThreadNumber} ({sess.Title}) is waiting for you.",
                        sess.Id);
                break;
            case "idle":
                Delivery.OnFree(sess.Id);
                break;
        }
    }

    /// The idle watchdog decided a silent Claude has finished its turn. Only
    /// a chance to deliver: the watchdog also fires on a long silent tool call
    /// mid-turn, so a thread's report is taken from the Stop hook alone.
    public void OnPaneIdle(Session sess) => Delivery.OnFree(sess.Id);

    public void OnAgentUp(Session sess) => Delivery.OnAgentUp(sess.Id);

    /// Claude stopped on a start-up question (the trust prompt for a new
    /// folder). A thread's folder is a worktree of the user's own repo, made
    /// by Perch a moment ago, so the answer is yes.
    public void OnPromptStuck(Session sess, Guid paneId)
    {
        if (sess.ThreadOf == null && !sess.IsLead) return;
        Log.Info("Thread.trust", $"session={sess.Id:N} pane={paneId:N}");
        _h.AcceptTrust(paneId);
    }

    /// A thread finished a turn: keep its report and tell the coordinator.
    private async Task CaptureAsync(Session thread)
    {
        // The Stop hook can beat the transcript's last lines to disk, so read
        // until two reads a moment apart agree on a report.
        string? reply = null, prev = null;
        for (int i = 0; i < 6; i++)
        {
            await Task.Delay(1500);
            try { reply = await _h.ReadLastReply(thread); }
            catch (Exception ex) { Log.Error("Thread.capture", ex); return; }
            if (reply != null && reply == prev) break;
            prev = reply;
        }
        if (string.IsNullOrWhiteSpace(reply) || reply == thread.ThreadLastReply) return;
        Record(thread, reply);
        if (thread.ThreadOf is Guid lid && _h.SessionById(lid) is Session lead)
        {
            var shown = $"Thread {thread.ThreadNumber} ({thread.Title}) finished its turn: \"{FirstLine(reply, 160)}\"";
            TellLead(lead,
                shown + $" Full report: perch thread read {thread.ThreadNumber}",
                $"Thread {thread.ThreadNumber} ({thread.Title}) finished its turn. Its report:\n\n{reply.Trim()}",
                shown);
        }
    }

    /// Tell the project chat something: its page conversation when it has
    /// one, else type it into its terminal. `line` is what a terminal gets
    /// typed; `full` what a chat's next turn gets; `shown` the chat's notice
    /// (defaults to `line`).
    private void TellLead(Session lead, string line, string full, string? shown = null)
    {
        if (_h.NotifyChat?.Invoke(lead, shown ?? line, full) == true) return;
        Delivery.Enqueue(lead.Id, line);
    }

    private void Record(Session thread, string reply)
    {
        thread.ThreadLastReply = reply.Trim();
        thread.ThreadReplyAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _h.Save();
        _h.PushState();
    }
}
