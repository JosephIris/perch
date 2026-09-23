using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Perch;

/// The conversation of a project chat, drawn by the page instead of a
/// terminal.
///
/// Every turn is one headless run of the same Claude session: the first
/// creates it (`--session-id`), the rest continue it (`--resume`), so the
/// coordinator keeps its memory while the screen stays Perch's own. The run
/// streams its steps (`--output-format stream-json`) and each becomes a row:
/// what the user said, what Claude wrote, a compact line per tool it used,
/// and Perch's own notices — a thread finishing, a question from a thread —
/// which are NOT the user's words and are drawn as such.
///
/// What a headless run cannot do is ask for permission, so the coordinator
/// runs with a fixed allow-list: read and search the repo, run its
/// `perch thread` commands, look at git, and merge threads' branches. It edits
/// nothing itself: writing code and resolving merge clashes are threads' work.
///
/// One run at a time per chat. Anything that arrives meanwhile — the user's
/// next message, a thread's report — is queued and goes in together as the
/// next turn, which is also how a report reaches a coordinator that is busy.
internal sealed class ChatController : IDisposable
{
    internal sealed class Host
    {
        public required Func<Guid, (Session Lead, PaneNode Leaf)?> ChatByPane { get; init; }
        public required Func<Guid, Project?> ProjectById { get; init; }
        public required Action<object> Post { get; init; }
        public required Action<Session, Guid, ThreadMessage> ThreadCommand { get; init; }
        /// Show the chat as working (or not) in the sidebar and dashboard.
        public required Action<Session, PaneNode, bool> SetWorking { get; init; }
        public required Action Save { get; init; }
        public required IUiThread Ui { get; init; }
        /// A coordinator turn finished (it may have merged something).
        public Action<Session>? TurnEnded { get; init; }
        /// The chat's proposed threads and whether each has started, and the
        /// coordinator's current system prompt path (rewritten per turn).
        public Func<Session, object>? Suggestions { get; init; }
        public Func<Session, string>? PromptPath { get; init; }
    }

    /// Tools the coordinator may use without asking — the only ones it gets.
    public static readonly string[] AllowedTools = new[] { "Read", "Grep", "Glob", "LS", "WebSearch", "WebFetch", "TodoWrite" }
        .Concat(ThreadController.ForShells("perch thread", "git log", "git diff", "git status", "git show", "git branch",
            // Assembling the result: merging a thread's branch, and backing
            // out of a merge that clashes (a thread resolves it, not the chat).
            "git merge", "git rev-parse", "git worktree list"))
        .ToArray();

    /// Tools the coordinator must not use even though they need no
    /// permission: its way to delegate is a thread, so no subagents (which
    /// then wander into worktrees of their own), and no waking itself later —
    /// Perch starts its next turn when there is something to act on.
    public static readonly string[] DeniedTools =
    {
        "Agent", "Task", "EnterWorktree", "ExitWorktree", "ScheduleWakeup",
        "CronCreate", "CronDelete", "Monitor", "Edit", "Write", "NotebookEdit",
    };

    private sealed class Chat
    {
        public required Guid PaneId { get; init; }
        public PerchIpcServer? Ipc;
        public Process? Proc;
        public readonly List<string> Queue = new();
        public List<ChatEntry>? Entries;
        public bool Stopping;
    }

    internal sealed record ChatEntry(string Id, string Kind, string Text, string Tool, long AtMs);

    private readonly Host _h;
    private readonly Dictionary<Guid, Chat> _chats = new();

    public ChatController(Host host) { _h = host; }

    private static string LogPath(Session lead) => Path.Combine(ThreadController.DirFor(lead), "chat.jsonl");

    private Chat? Get(Guid paneId, out Session lead, out PaneNode leaf)
    {
        lead = null!; leaf = null!;
        if (_h.ChatByPane(paneId) is not { } found) return null;
        (lead, leaf) = found;
        if (!_chats.TryGetValue(paneId, out var chat))
            _chats[paneId] = chat = new Chat { PaneId = paneId };
        EnsureIpc(chat, lead);
        return chat;
    }

    /// The chat's own pipe, so `perch thread …` from its runs reaches the
    /// thread controller as coming from the project chat.
    private void EnsureIpc(Chat chat, Session lead)
    {
        if (chat.Ipc != null) return;
        var ipc = new PerchIpcServer(chat.PaneId, _h.Ui);
        ipc.OnThread += msg => _h.ThreadCommand(lead, chat.PaneId, msg);
        ipc.Start();
        chat.Ipc = ipc;
    }

    // ---- page ------------------------------------------------------------

    public void OnRequest(Guid paneId)
    {
        var chat = Get(paneId, out var lead, out _);
        if (chat == null) return;
        _h.Post(new
        {
            type = "chat.history",
            paneId = paneId.ToString("D"),
            entries = Entries(chat, lead).Select(View).ToArray(),
            running = chat.Proc != null,
            queued = chat.Queue.Count,
        });
        PostMeta(lead);
    }

    /// The chat's goal, instructions, memory and proposed threads: what the
    /// Overview's About tab and the suggestion cards show. Sent with the
    /// history and again whenever any of it changes.
    public void PostMeta(Session lead)
    {
        var leaf = PaneTree.AllLeaves(lead.Root).FirstOrDefault(p => p.IsChat);
        if (leaf == null) return;
        _h.Post(new
        {
            type = "chat.meta",
            paneId = leaf.Id.ToString("D"),
            sessionId = lead.Id.ToString("D"),
            goal = lead.ChatGoal,
            instructions = lead.ChatInstructions,
            memory = ThreadController.ReadMemory(lead).ToArray(),
            suggestions = _h.Suggestions?.Invoke(lead) ?? Array.Empty<object>(),
        });
    }

    /// A row that stands for something rather than saying it: a thread that
    /// was started ("thread", tool = "thread:<id>") or proposed ("suggest",
    /// tool = "suggest:<id>"). The page draws it as a live card.
    public void Card(Session lead, string kind, string text, string tool)
    {
        var leaf = PaneTree.AllLeaves(lead.Root).FirstOrDefault(p => p.IsChat);
        if (leaf == null) return;
        var chat = Get(leaf.Id, out _, out _);
        if (chat == null) return;
        Append(chat, lead, kind, text, tool);
    }

    /// A new chat with a goal takes the first turn itself, the way a chief of
    /// staff would: look at the project and propose where to start.
    public void Kickoff(Session lead)
    {
        var leaf = PaneTree.AllLeaves(lead.Root).FirstOrDefault(p => p.IsChat);
        if (leaf == null) return;
        var chat = Get(leaf.Id, out _, out _);
        if (chat == null) return;
        Append(chat, lead, "notice", "Getting started: looking at the project to propose the first work.");
        chat.Queue.Add("[Perch] This project chat was just created. Look at the project, then propose the first one to three threads that move the goal forward with `perch thread suggest` (don't start them), and tell the user in a few lines what you propose and why.");
        Pump(chat, lead, leaf);
    }

    public void OnSend(Guid paneId, string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return;
        var chat = Get(paneId, out var lead, out var leaf);
        if (chat == null) return;
        Append(chat, lead, "user", text);
        chat.Queue.Add(text);
        Pump(chat, lead, leaf);
    }

    public void OnStop(Guid paneId)
    {
        var chat = Get(paneId, out var lead, out _);
        if (chat?.Proc == null) return;
        chat.Stopping = true;
        chat.Queue.Clear();
        try { chat.Proc.Kill(entireProcessTree: true); } catch { }
        Append(chat, lead, "notice", "Stopped.");
    }

    /// Something Perch has to tell the coordinator (a thread finished, a
    /// thread asks): `shown` is the notice row, `forClaude` what the next turn
    /// is given — the full report, so the coordinator needn't go and read it.
    public void Notice(Session lead, string shown, string forClaude)
    {
        var leaf = PaneTree.AllLeaves(lead.Root).FirstOrDefault(p => p.IsChat);
        if (leaf == null) return;
        var chat = Get(leaf.Id, out _, out _);
        if (chat == null) return;
        Append(chat, lead, "notice", shown);
        chat.Queue.Add("[Perch] " + forClaude);
        Pump(chat, lead, leaf);
    }

    /// A notice for the user only (a thread waiting on them): shown, not fed
    /// to Claude. `threadId` lets the row open that thread.
    public void Inform(Session lead, string text, Guid? threadId)
    {
        var leaf = PaneTree.AllLeaves(lead.Root).FirstOrDefault(p => p.IsChat);
        if (leaf == null) return;
        var chat = Get(leaf.Id, out _, out _);
        if (chat == null) return;
        Append(chat, lead, "notice", text, threadId is Guid t ? "thread:" + t.ToString("D") : "");
    }

    // ---- turns -----------------------------------------------------------

    private void Pump(Chat chat, Session lead, PaneNode leaf)
    {
        if (chat.Proc != null || chat.Queue.Count == 0) return;
        var prompt = string.Join("\n\n", chat.Queue);
        chat.Queue.Clear();
        try { Start(chat, lead, leaf, prompt); }
        catch (Exception ex)
        {
            Log.Error("Chat.start", ex);
            Append(chat, lead, "error", "Couldn't start Claude: " + ex.Message);
            Status(chat);
        }
    }

    private void Start(Chat chat, Session lead, PaneNode leaf, string prompt)
    {
        var claude = ClaudeHeadless.ResolveClaude()
            ?? throw new InvalidOperationException("Claude Code isn't installed (no `claude` on PATH).");
        var proj = lead.ProjectId is Guid pid ? _h.ProjectById(pid) : null;
        var cwd = proj != null && Directory.Exists(proj.Path) ? proj.Path : lead.Cwd;
        var promptPath = _h.PromptPath?.Invoke(lead)
            ?? (proj != null ? ThreadController.WriteCoordinatorPrompt(lead, proj) : Path.Combine(ThreadController.DirFor(lead), "coordinator.md"));
        if (string.IsNullOrEmpty(leaf.ClaudeSessionId)) leaf.ClaudeSessionId = Guid.NewGuid().ToString();

        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose" };
        args.AddRange(lead.ChatStarted
            ? new[] { "--resume", leaf.ClaudeSessionId! }
            : new[] { "--session-id", leaf.ClaudeSessionId! });
        args.Add("--append-system-prompt-file"); args.Add(promptPath);
        args.Add("--allowedTools"); args.AddRange(AllowedTools);
        args.Add("--disallowedTools"); args.AddRange(DeniedTools);
        // Ends the variadic list above, and is what a headless run without a
        // prompt to show would do anyway.
        args.Add("--permission-mode"); args.Add("default");

        var (file, arguments) = ClaudeHeadless.Command(claude, args);
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments,
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // The chat's pipe, so its `perch thread` commands land here; nothing
        // else of a pane's environment applies to a headless run.
        psi.Environment["PERCH_PIPE"] = OperatingSystem.IsWindows() ? $@"\\.\pipe\perch\{chat.PaneId:N}" : $@"perch\{chat.PaneId:N}";
        psi.Environment["PERCH_PANE_ID"] = chat.PaneId.ToString("N");
        psi.Environment.Remove("PERCH_RUN");

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();
        chat.Proc = proc;
        chat.Stopping = false;
        _h.SetWorking(lead, leaf, true);
        Status(chat);
        Log.Info("Chat.turn", $"lead={lead.Id:N} pid={proc.Id} resume={lead.ChatStarted} chars={prompt.Length}");

        _ = Task.Run(async () =>
        {
            try { await proc.StandardInput.WriteAsync(prompt); proc.StandardInput.Close(); }
            catch (Exception ex) { Log.Info("Chat.stdin", ex.Message); }
        });
        var stderr = new StringBuilder();
        _ = Task.Run(async () =>
        {
            try { stderr.Append(await proc.StandardError.ReadToEndAsync()); } catch { }
        });
        _ = Task.Run(async () =>
        {
            var sawResult = false;
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                {
                    var rows = ParseLine(line, out var isResult, out var resultError);
                    if (isResult) sawResult = true;
                    var err = resultError;
                    _h.Ui.Post(() =>
                    {
                        foreach (var (kind, text, tool) in rows) Append(chat, lead, kind, text, tool);
                        if (err != null) Append(chat, lead, "error", err);
                    });
                }
                await proc.WaitForExitAsync();
            }
            catch (Exception ex) { Log.Error("Chat.read", ex); }
            var code = SafeExitCode(proc);
            _h.Ui.Post(() => Finished(chat, lead, leaf, code, sawResult, stderr.ToString()));
        });
    }

    private void Finished(Chat chat, Session lead, PaneNode leaf, int code, bool sawResult, string stderr)
    {
        chat.Proc?.Dispose();
        chat.Proc = null;
        if (sawResult && !lead.ChatStarted) { lead.ChatStarted = true; _h.Save(); }
        if (!sawResult && !chat.Stopping)
        {
            var why = stderr.Trim();
            if (why.Length > 400) why = why[^400..];
            Append(chat, lead, "error", why.Length > 0 ? why : $"Claude stopped without an answer (exit {code}).");
        }
        chat.Stopping = false;
        _h.SetWorking(lead, leaf, false);
        _h.TurnEnded?.Invoke(lead);
        Log.Info("Chat.turn.end", $"lead={lead.Id:N} code={code} result={sawResult}");
        Status(chat);
        Pump(chat, lead, leaf);
    }

    private static int SafeExitCode(Process p) { try { return p.ExitCode; } catch { return -1; } }

    /// One line of stream-json → the rows it adds. Assistant prose becomes a
    /// "claude" row, a tool call a "tool" row; the final `result` line marks
    /// the turn complete (and carries an error when the run failed).
    internal static List<(string Kind, string Text, string Tool)> ParseLine(string line, out bool isResult, out string? error)
    {
        isResult = false; error = null;
        var rows = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(line)) return rows;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var type = Str(root, "type");
            if (type == "result")
            {
                isResult = true;
                var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                if (isError) error = Str(root, "result") is { Length: > 0 } r ? r : (Str(root, "subtype") ?? "Claude reported an error.");
                return rows;
            }
            if (type != "assistant" || !root.TryGetProperty("message", out var msg)
                || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                return rows;
            foreach (var block in content.EnumerateArray())
            {
                switch (Str(block, "type"))
                {
                    case "text":
                        var t = (Str(block, "text") ?? "").Trim();
                        if (t.Length > 0) rows.Add(("claude", t, ""));
                        break;
                    case "tool_use":
                        var name = Str(block, "name") ?? "";
                        block.TryGetProperty("input", out var input);
                        rows.Add(("tool", ToolTarget(name, input), name));
                        break;
                }
            }
        }
        catch (JsonException) { }
        return rows;
    }

    /// The one thing worth showing about a tool call.
    internal static string ToolTarget(string name, JsonElement input)
    {
        string? S(string k) => input.ValueKind == JsonValueKind.Object ? Str(input, k) : null;
        var v = name switch
        {
            "Bash" => S("command"),
            "Read" or "Edit" or "Write" => S("file_path") is { } f ? Path.GetFileName(f) : null,
            "Grep" or "Glob" => S("pattern"),
            "WebFetch" => S("url"),
            "WebSearch" => S("query"),
            _ => null,
        } ?? "";
        v = v.Replace('\n', ' ').Trim();
        // Claude prefixes most commands with `cd "<the repo>" &&`; the row
        // should say what it ran, not where.
        v = System.Text.RegularExpressions.Regex.Replace(v, @"^cd\s+(""[^""]*""|'[^']*'|\S+)\s*(&&|;)\s*", "");
        return v.Length > 160 ? v[..160] + "…" : v;
    }

    // ---- history ---------------------------------------------------------

    private List<ChatEntry> Entries(Chat chat, Session lead)
    {
        if (chat.Entries != null) return chat.Entries;
        chat.Entries = new List<ChatEntry>();
        try
        {
            var path = LogPath(lead);
            if (File.Exists(path))
                foreach (var line in File.ReadLines(path))
                {
                    try { if (JsonSerializer.Deserialize<ChatEntry>(line) is { } e) chat.Entries.Add(e); }
                    catch (JsonException) { }
                }
        }
        catch (Exception ex) { Log.Error("Chat.history", ex); }
        return chat.Entries;
    }

    private void Append(Chat chat, Session lead, string kind, string text, string tool = "")
    {
        var e = new ChatEntry(Guid.NewGuid().ToString("N")[..12], kind, text, tool, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Entries(chat, lead).Add(e);
        try
        {
            Directory.CreateDirectory(ThreadController.DirFor(lead));
            File.AppendAllText(LogPath(lead), JsonSerializer.Serialize(e) + "\n");
        }
        catch (Exception ex) { Log.Error("Chat.append", ex); }
        _h.Post(new { type = "chat.entry", paneId = chat.PaneId.ToString("D"), entry = View(e) });
    }

    private void Status(Chat chat) => _h.Post(new
    {
        type = "chat.status",
        paneId = chat.PaneId.ToString("D"),
        running = chat.Proc != null,
        queued = chat.Queue.Count,
    });

    private static object View(ChatEntry e) => new { id = e.Id, kind = e.Kind, text = e.Text, tool = e.Tool, atMs = e.AtMs };

    private static string? Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        foreach (var c in _chats.Values)
        {
            try { c.Proc?.Kill(entireProcessTree: true); } catch { }
            try { c.Ipc?.Dispose(); } catch { }
        }
        _chats.Clear();
    }
}
