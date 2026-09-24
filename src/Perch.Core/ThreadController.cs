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
        /// A thread was started from this chat (a card goes under the turn).
        public Action<Session, Session>? ThreadStarted { get; init; }
        /// The coordinator proposed a thread instead of starting it.
        public Action<Session, Suggestion>? Suggested { get; init; }
        /// What a thread on a permission prompt asks to do: its transcript's
        /// last tool call, in words. Null when it can't be read.
        public Func<Session, Task<string?>>? ReadPendingTool { get; init; }
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
        new[] { "TaskCreate", "TaskUpdate", "TaskList", "TaskGet" }
        .Concat(ForShells("perch thread", "git add", "git commit", "git status", "git diff", "git log")).ToArray();

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

    /// Write the coordinator's system prompt; returns its path. Rewritten at
    /// every turn, so a changed goal, instruction or memory note is in force
    /// from the next one.
    public string WriteCoordinatorPrompt(Session lead) =>
        lead.ProjectId is Guid pid && _h.ProjectById(pid) is Project proj ? WriteCoordinatorPrompt(lead, proj) : Path.Combine(DirFor(lead), "coordinator.md");

    public static string WriteCoordinatorPrompt(Session lead, Project proj)
    {
        var dir = DirFor(lead);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "coordinator.md");
        AtomicFile.WriteAllText(path, CoordinatorPrompt(proj, lead.ChatGoal, lead.ChatInstructions, ReadMemory(lead)));
        return path;
    }

    internal static string CoordinatorPrompt(Project proj, string goal = "", string instructions = "", IReadOnlyList<string>? memory = null) => $"""
        You are the coordinator of a project chat in Perch, for the project "{proj.Name}" at `{proj.Path}`.
        The user briefs you the way they would brief a chief of staff. You turn what they ask for into work, hand the work to threads, check what comes back, and put the result together for them. You see what threads report back, not every step they take.
        {Section("The goal", goal)}{Section("The user's instructions for this project", instructions)}{MemorySection(memory)}
        ## Routing each message
        - A quick question: answer it here.
        - New work: start a thread for it, or pass it to a thread already working in that area (`perch thread send`). Say which.
        - Several unrelated tasks in one message: a separate thread for each.
        - If the user wants to review threads before they run (they may say so, or it's in the memory above), propose them with `perch thread suggest` instead of starting them. Also propose rather than start when the request is large or unclear.

        ## Threads
        A thread is a separate Claude Code session with its own git worktree and branch of this repo. It works on its own, commits on its branch, and reports back when it finishes a turn. The user sees threads beside this chat, grouped by what needs them, and can open one to read or steer it. Run these with Bash:

        - `perch thread new "<title>" --brief "<brief>"` starts a thread; prints its number. The brief is everything the thread knows, so make it complete: the goal, the context and files that matter, constraints, what "done" looks like, and what to report back. For a long brief write it to a file and use `--brief-file <path>`.
        - `perch thread suggest "<title>" --brief "<brief>"` proposes a thread; the user starts it with a click.
        - `perch thread send <n> "<message>"` steers a running thread; delivered when it is free.
        - `perch thread list` / `perch thread read <n>` / `perch thread close <n>`.
        - `perch thread remember "<note>"` saves a note to project memory (a decision, a requirement, how the user likes to work); `perch thread forget <k>` removes one; `perch thread memory` lists them. Save the user's working preferences as they come up ("run at most two threads", "shorter updates").

        When a thread finishes a turn, a message starting with `[Perch` arrives with its report. Check it, then decide: follow up with that thread, start others, or report to the user.

        Never wait for threads: no sleeping, no scheduling a wake-up, no checking `perch thread list` over and over. Once threads are running, tell the user briefly what is running and end your turn. Perch starts your next turn when a thread reports or asks you something. A thread waiting for the user's permission is shown to the user directly.

        ## How to work
        - You can read and search this repo, look at git, and search the web, but you can't edit files or run other commands. Anything that changes something is a thread's job, and so is anything substantial. Give independent pieces separate threads so they run in parallel.
        - Threads commit on their own branches. Assembling the result is yours: when the user asks you to merge (or asked you to finish the whole job), check the thread's work (`git log`, `git diff <here>...<branch>`) and merge its branch into the branch checked out here with `git merge --no-ff <branch>`. Tell the user which order to merge in when it matters. Never push.
        - If a merge clashes, run `git merge --abort` and send that thread: `perch thread send <n> "Merge <this branch> into your branch, resolve the conflicts, commit, and report."` When it reports, merge again.
        - Keep the user posted briefly and lead with results. Messages starting with `[Perch` come from Perch, not from the user; several may arrive in one turn with what the user wrote.

        ## How your replies look
        - Short: a few sentences of plain prose, like a colleague's chat message. No headings, no tables, no status reports; a short list only when there really are several items. The user sees each thread's state and progress beside this chat, so don't repeat it.
        - Name a thread as `#<n>` (for example "#3 found the slow query"). Perch shows it as a link with the thread's title, so don't write the title next to it.
        """;

    private static string Section(string title, string body) =>
        string.IsNullOrWhiteSpace(body) ? "" : $"\n## {title}\n{body.Trim()}\n";

    private static string MemorySection(IReadOnlyList<string>? memory) =>
        memory is { Count: > 0 } ? $"\n## Project memory\n{string.Join("\n", memory.Select((m, i) => $"{i + 1}. {m}"))}\n" : "";

    // ---- a thread ----------------------------------------------------------

    internal static string ThreadPrompt(int n, string title, string brief, string instructions = "", IReadOnlyList<string>? memory = null) => $"""
        You are thread {n} of a project chat in Perch: "{title}". A coordinator Claude gave you this task and will review your result.

        - You work in your own git worktree on your own branch (`git branch --show-current`). Commit your work there. Don't push or merge unless the brief says to.
        - When you finish, or you are blocked, end your turn with a short report: first line, the outcome in one sentence; then what you did, what's left, and anything the coordinator has to decide. That report goes to the coordinator on its own.
        - To ask the coordinator something mid-task, run `perch thread send lead "<question>"` and carry on with what you can.
        - To save something every later thread should know (a decision, a pitfall), run `perch thread remember "<note>"`.
        - Messages from the coordinator or the user arrive as lines starting with `[Perch #…]`.
        - Keep a short task list with your task tools — TaskCreate for each step before you start, TaskUpdate as each one starts and completes: two to six tasks, each a few words. The user follows your progress by it. When you get new work from the coordinator or the user, add tasks for it.
        {Section("The user's instructions for this project", instructions)}{MemorySection(memory)}
        ## Your brief
        {brief}
        """;

    /// A thread's first prompt. The task list comes first because the user
    /// follows a thread by it (the Overview's "2/3", its checklist), and the
    /// task tools are loaded on demand — without saying so, models skip them.
    internal const string Kickoff =
        "Start on the task in your brief. First write your plan as a task list with TaskCreate (load the task tools with ToolSearch if you need to), then work through it, marking each task in progress and completed with TaskUpdate.";

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
                var (tab, error) = await StartThreadAsync(lead, (m.Title ?? "").Trim(), m.Brief ?? "");
                return tab == null ? "error\n" + error : $"ok\n{tab.ThreadNumber}\n";
            }
            case "suggest":
            {
                if (fromThread) return "error\nOnly the project chat proposes threads.";
                var s = new Suggestion { Id = Guid.NewGuid().ToString("N")[..10], Title = (m.Title ?? "").Trim(), Brief = m.Brief ?? "" };
                var all = LoadSuggestions(lead);
                all.Add(s);
                SaveSuggestions(lead, all);
                _h.Suggested?.Invoke(lead, s);
                return "ok\nProposed. It starts when the user clicks Start on it in the chat.\n";
            }
            case "remember":
            {
                var text = (m.Text ?? "").Trim();
                if (text.Length == 0) return "error\nNothing to remember.";
                var mem = ReadMemory(lead);
                mem.Add($"{text} ({DateTime.Now:yyyy-MM-dd})");
                WriteMemory(lead, mem);
                if (!fromThread) WriteCoordinatorPrompt(lead);
                return $"ok\nRemembered as note {mem.Count}.\n";
            }
            case "forget":
            {
                var mem = ReadMemory(lead);
                if (!int.TryParse((m.Target ?? "").Trim(), out var k) || k < 1 || k > mem.Count)
                    return $"error\nNo note {m.Target}. Run perch thread memory to see them numbered.";
                mem.RemoveAt(k - 1);
                WriteMemory(lead, mem);
                return $"ok\nForgot note {k}.\n";
            }
            case "memory":
            {
                var mem = ReadMemory(lead);
                return mem.Count == 0 ? "ok\nNo notes yet.\n"
                    : "ok\n" + string.Join("\n", mem.Select((x, i) => $"{i + 1}. {x}")) + "\n";
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

    /// Start a thread: its own worktree tab under the project, primed with
    /// the brief (plus the chat's instructions and memory), shown as a card
    /// in the chat. Shared by `perch thread new` and starting a suggestion.
    public async Task<(Session? Tab, string Error)> StartThreadAsync(Session lead, string title, string brief)
    {
        var proj = lead.ProjectId is Guid pid ? _h.ProjectById(pid) : null;
        if (proj == null) return (null, "This project chat's project is no longer registered.");
        if (title.Length == 0) title = "Thread";
        var n = ++lead.ThreadNumber;   // on the lead: the last number handed out
        _h.Save();
        var dir = DirFor(lead);
        Directory.CreateDirectory(dir);
        var promptPath = Path.Combine(dir, $"thread-{n}.md");
        AtomicFile.WriteAllText(promptPath, ThreadPrompt(n, title, brief, lead.ChatInstructions, ReadMemory(lead)));
        var tab = await _h.CreateClaudeTab(proj, title, promptPath, Kickoff, true);
        if (tab == null) return (null, "Perch couldn't make the thread's tab (see the toast in Perch).");
        tab.ThreadOf = lead.Id;
        tab.ThreadNumber = n;
        _h.Save();
        _h.PushState();
        _h.ThreadStarted?.Invoke(lead, tab);
        Log.Info("Thread.new", $"lead={lead.Id:N} n={n} session={tab.Id:N}");
        return (tab, "");
    }

    // ---- suggestions -------------------------------------------------------

    internal sealed class Suggestion
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Brief { get; set; } = "";
        /// The thread started from it, once someone clicked Start.
        public Guid? ThreadId { get; set; }
        /// Waved away by the user; kept so the chat's history still reads.
        public bool Dismissed { get; set; }
    }

    private static string SuggestionsPath(Session lead) => Path.Combine(DirFor(lead), "suggestions.json");

    public List<Suggestion> LoadSuggestions(Session lead)
    {
        try
        {
            var path = SuggestionsPath(lead);
            if (File.Exists(path))
                return System.Text.Json.JsonSerializer.Deserialize<List<Suggestion>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception ex) { Log.Error("Thread.suggestions", ex); }
        return new();
    }

    private static void SaveSuggestions(Session lead, List<Suggestion> all)
    {
        Directory.CreateDirectory(DirFor(lead));
        AtomicFile.WriteAllText(SuggestionsPath(lead), System.Text.Json.JsonSerializer.Serialize(all));
    }

    /// The user clicked Start on a proposed thread (or Start all).
    public async Task<string?> StartSuggestionAsync(Session lead, string id)
    {
        var all = LoadSuggestions(lead);
        var s = all.FirstOrDefault(x => x.Id == id);
        if (s == null) return "That suggestion is gone.";
        if (s.ThreadId != null) return null;   // already started
        s.Dismissed = false;
        var (tab, error) = await StartThreadAsync(lead, s.Title, s.Brief);
        if (tab == null) return error;
        all = LoadSuggestions(lead);
        if (all.FirstOrDefault(x => x.Id == id) is { } again) again.ThreadId = tab.Id;
        SaveSuggestions(lead, all);
        return null;
    }

    public void DismissSuggestion(Session lead, string id)
    {
        var all = LoadSuggestions(lead);
        if (all.FirstOrDefault(x => x.Id == id) is not { ThreadId: null } s) return;
        s.Dismissed = true;
        SaveSuggestions(lead, all);
    }

    // ---- memory ------------------------------------------------------------
    // The chat's shared memory: short notes (decisions, requirements, how the
    // user likes to work) that the coordinator and every new thread are given.
    // Kept by Perch through `perch thread remember|forget|memory`, so neither
    // the coordinator nor a thread needs write access to a file outside its
    // own folder.

    private static string MemoryPath(Session lead) => Path.Combine(DirFor(lead), "memory.md");

    public static List<string> ReadMemory(Session lead)
    {
        try
        {
            var path = MemoryPath(lead);
            if (!File.Exists(path)) return new();
            return File.ReadAllLines(path).Where(l => l.StartsWith("- ")).Select(l => l[2..].Trim()).Where(l => l.Length > 0).ToList();
        }
        catch { return new(); }
    }

    public static void WriteMemory(Session lead, List<string> notes)
    {
        Directory.CreateDirectory(DirFor(lead));
        AtomicFile.WriteAllText(MemoryPath(lead), "# Project memory\n\n" + string.Join("", notes.Select(n => $"- {n.Replace('\n', ' ')}\n")));
    }

    // ---- threads' state ----------------------------------------------------

    public void Resolve(Session thread, bool resolved)
    {
        thread.ThreadResolved = resolved;
        _h.Save();
        _h.PushState();
    }

    /// How many of each thread's commits the project's checked-out branch
    /// doesn't have yet. Runs after a thread's turn and after the chat's (the
    /// coordinator may just have merged). A thread whose work all landed and
    /// that has nothing left to do is resolved on its own.
    public async Task RefreshUnmergedAsync(Session lead)
    {
        var proj = lead.ProjectId is Guid pid ? _h.ProjectById(pid) : null;
        if (proj == null || !Directory.Exists(proj.Path)) return;
        var changed = false;
        foreach (var t in ThreadsOf(lead).Where(t => t.WorktreeBranch.Length > 0).ToList())
        {
            var (code, stdout, _) = await ProcRunner.RunAsync("git", $"rev-list --count HEAD..\"{t.WorktreeBranch}\"", "thread.unmerged",
                workingDir: proj.Path, timeoutMs: 10000);
            if (code != 0 || !int.TryParse(stdout.Trim(), out var n)) continue;
            if (n != t.ThreadUnmerged)
            {
                // Its commits just landed: that thread's job is done.
                if (n == 0 && t.ThreadUnmerged > 0 && !Busy(t)) t.ThreadResolved = true;
                t.ThreadUnmerged = n;
                changed = true;
            }
        }
        if (changed) { _h.Save(); _h.PushState(); }
    }

    private static bool Busy(Session s) => PaneTree.AllLeaves(s.Root).Any(p => p.IsTerminal &&
        p.AgentState is AgentState.Working or AgentState.Permission or AgentState.Waiting);

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
        if (msg.State != "permission") sess.ThreadAsk = "";
        else if (sess.ThreadOf != null) _ = ReadAskAsync(sess);
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

    /// A thread stopped on a permission prompt: say what it asks for. The
    /// tool call is in its transcript before the prompt shows; a moment's
    /// wait lets the line reach the disk.
    private async Task ReadAskAsync(Session thread)
    {
        await Task.Delay(400);
        string? ask = null;
        try { ask = _h.ReadPendingTool == null ? null : await _h.ReadPendingTool(thread); }
        catch (Exception ex) { Log.Error("Thread.ask", ex); }
        var stillAsking = PaneTree.AllLeaves(thread.Root).Any(p => p.IsTerminal && p.AgentState == AgentState.Permission);
        Log.Info("Thread.ask", $"session={thread.Id:N} asking={stillAsking} ask={(ask ?? "(none)")}");
        if (!stillAsking || string.IsNullOrEmpty(ask) || ask == thread.ThreadAsk) return;
        thread.ThreadAsk = ask;
        _h.PushState();
    }

    /// The tool call a transcript is stopped on: the last `tool_use` with no
    /// `tool_result` after it. `lines` are the transcript's last JSONL rows
    /// (a torn first row is skipped). Pure.
    internal static (string Verb, string Target)? PendingTool(IEnumerable<string> lines)
    {
        var open = new List<(string Id, string Verb, string Target)>();
        foreach (var line in lines)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("message", out var msg) || !msg.TryGetProperty("content", out var content)
                    || content.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                foreach (var block in content.EnumerateArray())
                {
                    var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
                    if (type == "tool_use")
                    {
                        var id = block.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
                        var name = block.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        block.TryGetProperty("input", out var input);
                        open.Add((id, name, ChatController.ToolTarget(name, input)));
                    }
                    else if (type == "tool_result" && block.TryGetProperty("tool_use_id", out var r))
                        open.RemoveAll(x => x.Id == r.GetString());
                }
            }
            catch (System.Text.Json.JsonException) { }
        }
        return open.Count == 0 ? null : (open[^1].Verb, open[^1].Target);
    }

    /// A tool call in words, for "It asks to …". Pure.
    internal static string DescribeTool(string verb, string target)
    {
        target = (target ?? "").Trim();
        return verb switch
        {
            "Bash" or "PowerShell" => target.Length > 0 ? $"Run {target}" : "Run a command",
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => target.Length > 0 ? $"Edit {target}" : "Edit a file",
            "Read" => target.Length > 0 ? $"Read {target}" : "Read a file",
            "WebFetch" => target.Length > 0 ? $"Fetch {target}" : "Fetch a web page",
            "WebSearch" => target.Length > 0 ? $"Search the web for {target}" : "Search the web",
            _ => target.Length > 0 ? $"Use {verb}: {target}" : $"Use {verb}",
        };
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
        if (thread.ThreadOf is Guid owner && _h.SessionById(owner) is Session chatLead) await RefreshUnmergedAsync(chatLead);
        if (string.IsNullOrWhiteSpace(reply) || reply == thread.ThreadLastReply) return;
        thread.ThreadResolved = false;   // it did something new: back in play
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
