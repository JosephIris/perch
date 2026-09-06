using System;
using System.Collections.Generic;
using System.Text;

namespace Perch;

/// The prose Perch hands to Claude on behalf of a team. Pure text, no disk,
/// so every sentence here has a unit test that can read it back.
///
/// Three audiences:
///   - a BOT, at every launch: its system prompt (identity + the position's
///     brief) — `SystemPrompt`.
///   - a BOT, at every prompt: the roster (who is on the team, how to reach
///     them, room etiquette) — `Roster`. Injected as hook context so joins and
///     leaves need no restart.
///   - a HEADLESS run: the prompt that writes a brief from a purpose after
///     reading the repository — `BriefPrompt`.
///
/// The `[Perch team]` prefix, the `(no reply)` answer and the `perch team
/// post` verb named here are the wire contract with TeamController and the
/// CLI; change them together.
internal static class TeamRender
{
    /// What the owner's posts look like when typed into a bot's terminal.
    public const string PostPrefix = "[Perch team]";

    /// Everyone-marker in a RoomEntry.To list.
    public const string Everyone = "*";

    /// What a bot answers when a room post needs nothing from it. The room
    /// keeps such replies out (TeamController.IngestTranscripts), so a post to
    /// everyone doesn't collect a "not for me" from each bot it wasn't for.
    public const string NoReply = "(no reply)";

    // ---- roster ----------------------------------------------------------

    /// The roster as the hook injects it. `presence` maps bot slug → a short
    /// word ("working", "idle", "asleep", …); absent = unknown, nothing shown.
    public static string Roster(TeamDoc doc, string projectName,
        IReadOnlyDictionary<string, string>? presence = null, string? modelLimits = null,
        IReadOnlyDictionary<string, string>? addresses = null)
    {
        var sb = new StringBuilder();
        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        sb.Append("# Team roster — ").Append(project).Append('\n');
        sb.Append("You are one of ").Append(doc.Bots.Count).Append(doc.Bots.Count == 1 ? " bot" : " bots")
          .Append(" working on this repository for Joseph, the owner. Each bot is a separate Claude Code session on this machine, ")
          .Append("with its own position and its own copy of the code.\n\n");

        if (doc.Bots.Count == 0)
        {
            sb.Append("(No bots yet.)\n");
        }
        else
        {
            foreach (var bot in doc.Bots)
            {
                var pos = doc.Position(bot.PositionSlug);
                sb.Append("- ").Append(bot.Nickname).Append(" (session name `").Append(bot.CcName).Append("`");
                // The address is what makes a teammate unambiguous. Names are not
                // unique across everything Claude Code can see — a session left over
                // from an earlier run, or one on another machine, answers to the same
                // name, and a send then fails with "N agents are named 'bo'".
                if (addresses != null && addresses.TryGetValue(bot.Slug, out var addr) && !string.IsNullOrWhiteSpace(addr))
                    sb.Append(", address `")
                      .Append(addr!.StartsWith("uds:", StringComparison.OrdinalIgnoreCase) ? addr : "uds:" + addr)
                      .Append('`');
                sb.Append(") — ").Append(pos?.Name ?? bot.PositionSlug);
                if (doc.IsLead(bot)) sb.Append(", the team lead");
                var purpose = OneLine(pos?.Purpose, 160);
                if (purpose.Length > 0) sb.Append(": ").Append(purpose);
                if (presence != null && presence.TryGetValue(bot.Slug, out var word) && !string.IsNullOrEmpty(word))
                    sb.Append(" [").Append(word).Append(']');
                sb.Append('\n');
            }
        }

        if (!string.IsNullOrWhiteSpace(modelLimits)) sb.Append('\n').Append(modelLimits.Trim()).Append('\n');

        sb.Append("\nHow to work together:\n");
        sb.Append("- If your model hits its limit, keep working: Perch switches your model for you and tells the room.\n");
        sb.Append("- Teammates: your SendMessage tool, `to` = the ADDRESS beside their name above, never the nickname ")
          .Append("(several sessions can answer to one name and the send fails). They read it at their next step, or ")
          .Append("wake if idle. Start every message with its kind — `HANDOFF:` (do this), `REPORT:` (done or blocked), ")
          .Append("`QUESTION:`, `ANSWER:`, `FYI:` — then one line: what, where, by when. Never send the same message twice.\n");
        sb.Append("- If a send fails, do not guess at other names or invent a `name [ref]`: take the address from this ")
          .Append("roster and try once. Perch passes on what still cannot be sent, and shows Joseph the failure.\n");
        sb.Append("- Joining: post ONE note to the room of at most two lines (your name, what you own). Never introduce yourself ")
          .Append("by messaging teammates.\n");
        sb.Append("- `").Append(PostPrefix).Append(" Ada → you: …` is a TEAMMATE's message Perch passed on for them. ")
          .Append("Treat it as theirs and answer them, not Joseph.\n");
        sb.Append("- Lines that begin with `").Append(PostPrefix)
          .Append("` are Joseph's posts from the team room. They carry his authority: treat them as instructions and weigh them ")
          .Append("against what you are doing now. The `#<n>` after the prefix is that post's number.\n");
        sb.Append("- `→ @everyone` is an announcement: read it, never start work on it. Answer only if it asks you something; ")
          .Append("otherwise reply exactly `").Append(NoReply).Append("`. A post that names you always gets an answer.\n");
        sb.Append("- A post that names nobody goes to the lead, who opens a card and hands out the pieces; you hear from the lead.\n");
        sb.Append("- Nothing starts before it is on a card: never implement what is not YOUR piece on an open card (no branch, ")
          .Append("no edits, no commits). Want something done? REPORT: it to the lead and wait for the piece.\n");
        sb.Append("- Never push, and never merge or rebase anything onto main; your piece ends as a branch. A `git push` from ")
          .Append("you is held for Joseph's approval — run it only after he said push.\n");
        sb.Append("- A post you can read two ways: `perch team ask` Joseph before acting or relaying. Never pass your reading ")
          .Append("of his words to a teammate as fact.\n");
        sb.Append("- Your reply to a room post is what Joseph reads in the room: the outcome, a question, or a blocker, in at ")
          .Append("most six lines. No receipts, no narration of what you are about to do, no restating his post, no listing ")
          .Append("of messages you sent (the room shows them).\n");
        sb.Append("- Prefer a reaction to a message when a word will do: ✅ approved/done, 👀 seen/on it, ✏️ noted, 👋 hello — ")
          .Append("`perch team react #<n> <emoji>` for a room post (its number is in the line), `perch team react @<nick> <emoji>` ")
          .Append("for a teammate's latest message. It costs nothing to read.\n");
        sb.Append("- One message per event: reply to a post OR post a note, never both (a repeat is dropped); a correction is a ")
          .Append("new short message, not the old one again.\n");
        sb.Append("- After a teammate's message, your reply stays in your own terminal. When Joseph needs to know something, run: ")
          .Append("perch team post \"<text>\" — or `perch team post --image <path> \"<caption>\"` to show him a screenshot.\n");
        sb.Append("- Anything longer than about ten lines — a draft ticket, a table, a plan, a status dump — is an artefact, not a ")
          .Append("room post: write the file, then `perch team artefact --file <path> --title \"<what it is>\"`. Joseph gets a card ")
          .Append("in the room and opens it beside the chat; a post that long is stored as one anyway.\n");
        sb.Append("- When you need Joseph's decision or his eyes (a visual check, a choice, a go-ahead), run: ")
          .Append("perch team ask \"<question>\" [--choices \"A|B\"]. It becomes a card he answers; the answer arrives as a post. ")
          .Append("Do not wait for approval in prose.\n");
        sb.Append("- The lead runs the task board. When your piece is done or blocked, `perch team task mine … --status done|blocked` ")
          .Append("and a REPORT: to the lead. One owner per piece: if a teammate owns what you are about to change, ask them first.\n");
        sb.Append("- Your memory file: a short summary on top, details below a `---` line. The top arrives with every prompt; the ")
          .Append("rest is on disk to Read.\n");
        sb.Append("- Your piece is IMPLEMENTED BY A RUN, not here: `perch team run <task id> \"<what done looks like, files, ")
          .Append("tests, gotchas>\"` starts a fresh Claude in your folder on your branch; this session stays free for the room. ")
          .Append("Its result comes as a `").Append(PostPrefix).Append(" run … → @you:` line with the report's path: review the ")
          .Append("diff, verify, fix a one-liner or run again, update your piece, REPORT: to the lead. One run at a time ")
          .Append("(`perch team run --cancel` stops it); never code here beyond a one-liner.\n");
        return sb.ToString();
    }

    // ---- system prompt ---------------------------------------------------

    /// The bot's appended system prompt: who it is, then the position's brief
    /// verbatim. Identity first, because the brief is written for "someone
    /// holding this position" and the name is what makes it this bot's.
    /// `memoryPath` names the file the bot keeps its notes in; the notes
    /// themselves ride in with every prompt (Context), not here, so an edit
    /// mid-session is seen at the next turn rather than the next launch.
    public static string SystemPrompt(TeamBot bot, TeamPosition pos, string brief, string projectName, string? memoryPath = null, bool isLead = false)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        var sb = new StringBuilder();
        sb.Append("# You are ").Append(bot.Nickname).Append(", the ").Append(pos.Name)
          .Append(isLead ? " and the team lead" : "")
          .Append(" on the ").Append(project).Append(" team\n\n");
        sb.Append("Your Claude Code session name is `").Append(bot.CcName)
          .Append("`; teammates address you by it. You share this repository with other bots, each in its own session. ")
          .Append("The team roster — who is on the team and how to reach them — arrives with every prompt, so never assume ")
          .Append("a teammate exists until you have seen them on it.\n\n");
        if (!string.IsNullOrWhiteSpace(memoryPath))
            sb.Append("You have a memory file, `").Append(memoryPath.Trim())
              .Append("`, that travels with the repository: what you write there, a future you reads — on this machine or ")
              .Append("another. Its contents arrive with every prompt.\n\n");
        var purpose = OneLine(pos.Purpose, 400);
        if (purpose.Length > 0)
            sb.Append("Your purpose, in the owner's words: ").Append(purpose).Append("\n\n");
        sb.Append("## Your standing brief\n\n");
        sb.Append(string.IsNullOrWhiteSpace(brief)
            ? "(No brief has been written for this position yet. Work from the purpose above and ask the owner when unsure.)\n"
            : brief.Trim() + "\n");
        if (isLead) sb.Append('\n').Append(LeadRole(bot));
        return sb.ToString();
    }

    /// The lead's role, on top of its brief: the one bot that runs the task
    /// board. Built in rather than generated, because the board's commands
    /// and the wrap-up rule are Perch's, not the position's.
    public static string LeadRole(TeamBot bot)
    {
        var sb = new StringBuilder();
        sb.Append("## You lead the team\n\n");
        sb.Append("You are the one lead, and you ORCHESTRATE: you do not implement. Joseph's posts that name nobody come to ")
          .Append("you; a post to everyone is an announcement. The team's work lives on the task board Joseph sees in the room — ")
          .Append("one card per task, several may be open at once. Your brief's ownership lines describe what you hand out and ")
          .Append("review, not what you code. Your job:\n");
        sb.Append("- Open tasks AT ONCE, from Joseph's words: the moment he posts work — to you, to a teammate, or to everyone — ")
          .Append("and no open task covers it, run `perch team task new \"<what done looks like>\"` (it prints the task id). ")
          .Append("Never wait for him to agree first; he corrects the card if it's wrong. Only you and Joseph open tasks.\n");
        sb.Append("- Hand out every piece by name IN THE SAME TURN: `perch team task assign <id> <session name> \"<their piece>\"` ")
          .Append("for every teammate on it, and a HANDOFF: message to each. Your own piece is review and integration — never a ")
          .Append("piece you could hand out. If nobody else can do a piece, say so in the room first, then take it.\n");
        sb.Append("- Nothing starts before it is on a card. A teammate with no piece must not code; when the room says someone is ")
          .Append("editing with no piece, assign it or stop it. Never open a branch for a piece that is not on a card.\n");
        sb.Append("- When Joseph's words can be read two ways, `perch team ask` him before assigning. Never relay a guess to a ")
          .Append("teammate as fact; a correction later is a new short message, not the old one again.\n");
        sb.Append("- Pushing is Joseph's call. Nobody pushes or merges onto main — pieces end as branches. After he confirms a ")
          .Append("task, ask him with `perch team ask \"Push <branch>?\" --choices \"Push|Not yet\"`; a push before that is held ")
          .Append("for his approval anyway.\n");
        sb.Append("- Keep the cards current: the board (every task, every piece and its status) arrives with each of your ")
          .Append("prompts. Chase blocked pieces, re-split when the plan changes, and keep Joseph posted in a few lines.\n");
        sb.Append("- Teammates implement their pieces through RUNS (`perch team run`): a HANDOFF should carry what a run needs — ")
          .Append("what done looks like, where, which tests. Keep the team's shared knowledge file pruned (one line per fact, ")
          .Append("nothing stale) and its skills current; a fact you see two bots rediscover belongs in knowledge.\n");
        sb.Append("- Close each: when every piece of a task is done and you have checked the result, run ")
          .Append("`perch team task done <id>`. That asks Joseph to confirm. Do not say a task is done in prose; the command is ")
          .Append("what the room shows.\n");
        sb.Append("- After Joseph confirms a task, each bot that worked on it (you included) is asked, once it is free and has ")
          .Append("nothing of its own left on the board, to write what the next task needs into its memory file — one wrap-up ")
          .Append("for every card it finished since its last reset — and is then reset. A bot mid-piece on another card is left ")
          .Append("alone until that card is confirmed too. Expect to start a fresh task with only your brief, the roster, the ")
          .Append("board and your memory.\n");
        return sb.ToString();
    }

    // ---- wrap-up -------------------------------------------------------------

    /// The one line a bot is typed when the harness has decided it is time
    /// to write up its finished work and reset: every card, its own piece on
    /// each, and what to do. Kept to one message so the bot's reply is the
    /// turn the reset follows.
    public static string WrapUp(TeamBot bot, IReadOnlyList<TaskBoard> cards, string memoryPath)
    {
        var sb = new StringBuilder();
        sb.Append(cards.Count == 1 ? "Joseph confirmed the task you worked on: " : $"Joseph confirmed {cards.Count} tasks you worked on: ");
        for (var i = 0; i < cards.Count; i++)
        {
            var c = cards[i];
            if (i > 0) sb.Append("; ");
            sb.Append('(').Append(i + 1).Append(") ").Append(c.Id).Append(" \"").Append(OneLine(c.Title, 140)).Append('"');
            var mine = c.ItemOf(bot.Slug);
            if (mine != null)
            {
                sb.Append(" — your piece: [").Append(mine.Status).Append("] ").Append(OneLine(mine.Title, 100));
                var note = OneLine(mine.Note, 100);
                if (note.Length > 0) sb.Append(" (").Append(note).Append(')');
            }
            else if (string.Equals(c.SetBy, bot.Slug, StringComparison.OrdinalIgnoreCase)) sb.Append(" — you ran it");
        }
        sb.Append(". Wrap up now: update your memory file `").Append(memoryPath)
          .Append("` with what the next task will need from ").Append(cards.Count == 1 ? "it" : "each")
          .Append(" (decisions, where things stand, unfinished threads, who owns what); keep the part above `---` under ")
          .Append(TeamStore.MemoryMaxBytes / 1024).Append(" KB — only that much reaches you — and move detail below the line. ")
          .Append("A fact every teammate needs goes to team knowledge instead (`perch team learn \"…\"`); a procedure you followed ")
          .Append("that a teammate could reuse becomes a skill (`perch team skill \"<name>\" --file <path>`). ")
          .Append("Stop any background commands or local servers you started. Then reply with one line. ")
          .Append("Your context is cleared after that reply.");
        return sb.ToString();
    }

    /// Typed once when the wrap-up turn ended and the memory file is as it
    /// was: the reset follows the next reply whatever happens.
    public static string WrapNudge(string memoryPath)
        => $"Your memory file `{memoryPath}` did not change. Write what the next task needs into it now — the part above `---` " +
           $"under {TeamStore.MemoryMaxBytes / 1024} KB, detail below — then reply with one line. Your context is cleared after that reply.";

    /// "Ada", "Ada and Bo", "Ada, Bo and Cy".
    public static string Names(IReadOnlyList<string> names)
        => names.Count == 0 ? "" : names.Count == 1 ? names[0]
         : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];

    // ---- task block --------------------------------------------------------

    /// The task board as it concerns one bot, for its per-prompt context:
    /// the task, every piece with its status, and the commands this bot may
    /// use (the lead gets the board's; a member gets its own piece's).
    public static string TaskBlock(TaskDoc tasks, TeamDoc doc, TeamBot bot)
    {
        var sb = new StringBuilder();
        var isLead = doc.IsLead(bot);
        var lead = doc.Lead;
        sb.Append("# Task board\n");
        var boards = tasks.Open;
        if (boards.Count == 0)
        {
            sb.Append("(No task open.");
            if (isLead) sb.Append(" You are the lead: when Joseph posts work, `perch team task new \"<what done looks like>\"` at once.");
            else if (lead != null) sb.Append(' ').Append(lead.Nickname).Append(" leads and opens tasks; if Joseph gives you work directly, do it and keep your piece current — the lead will put it on a card.");
            else sb.Append(" There is no lead yet; Joseph opens tasks from the room.");
            sb.Append(")\n");
        }
        else
        {
            foreach (var board in boards)
            {
                sb.Append("- Task ").Append(board.Id).Append(": **").Append(OneLine(board.Title, 300)).Append("** — ").Append(board.Status);
                if (board.Status == "review") sb.Append(" (waiting for Joseph to confirm)");
                if (board.Status == "done") sb.Append(" (confirmed; wrap up when asked)");
                sb.Append('\n');
                foreach (var item in board.Items)
                {
                    var b = doc.Bot(item.Bot);
                    var who = b?.Nickname ?? item.Bot;
                    sb.Append("  - ").Append(who).Append(b != null && b.Slug == bot.Slug ? " (you)" : "").Append(": ");
                    sb.Append('[').Append(item.Status).Append("] ").Append(OneLine(item.Title, 160));
                    var note = OneLine(item.Note, 160);
                    if (note.Length > 0) sb.Append(" — ").Append(note);
                    sb.Append('\n');
                }
                if (board.Items.Count == 0) sb.Append("  - (no pieces yet)\n");
            }
        }
        sb.Append("\nKeep your piece current: `perch team task mine <id> \"<your piece>\" --status doing|done|blocked --note \"<one line>\"` ")
          .Append("(the id may be left out when you have a piece on only one task).\n");
        var mine = boards.Where(b => b.Status != "done" && b.ItemOf(bot.Slug) != null).ToList();
        if (isLead)
        {
            sb.Append("You lead: `perch team task new \"…\"` opens a task (prints its id), `perch team task assign <id> <session name> \"…\"` ")
              .Append("gives a teammate their piece, `perch team task done <id>` asks Joseph to confirm when every piece is done.\n");
            // Who has nothing to do while there is work open: the lead's to
            // assign, or to tell them they are off it.
            var idle = doc.Bots.Where(b => !doc.IsLead(b) && !boards.Any(t => t.Status != "done" && t.ItemOf(b.Slug) != null)).ToList();
            if (boards.Any(t => t.Status != "done") && idle.Count > 0)
                sb.Append("No piece on any open task: ").Append(string.Join(", ", idle.Select(b => b.Nickname)))
                  .Append(" — assign each a piece, or tell them they're off it. A teammate with no piece must not code.\n");
        }
        else if (lead != null)
        {
            sb.Append(lead.Nickname).Append(" (`").Append(lead.CcName).Append("`) leads: they open tasks, give out pieces and close them. ")
              .Append("REPORT: to them when yours is done or blocked.\n");
            if (mine.Count == 0)
                sb.Append("**You have no piece on the board.** Do not implement anything — no branch, no edits, no commits — until ")
                  .Append(lead.Nickname).Append(" assigns you one. If Joseph asked you something, answer in words; if you see work ")
                  .Append("that needs doing, REPORT: it to ").Append(lead.Nickname).Append(" and wait for the piece.\n");
        }
        return sb.ToString();
    }

    // ---- per-prompt context ----------------------------------------------

    /// What the prompt hook inlines for one bot: the shared roster, then that
    /// bot's own memory with the rule for keeping it. One file per bot
    /// (local/bots/&lt;slug&gt;/context.md) because the memory is the bot's alone.
    public static string Context(string roster, TeamBot bot, string memory, string memoryPath, string? taskBlock = null,
        string? knowledgeBlock = null, string? skillsBlock = null)
    {
        var sb = new StringBuilder();
        sb.Append(roster.TrimEnd()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(taskBlock)) sb.Append(taskBlock.TrimEnd()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(knowledgeBlock)) sb.Append(knowledgeBlock.TrimEnd()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(skillsBlock)) sb.Append(skillsBlock.TrimEnd()).Append("\n\n");
        sb.Append("# Your memory\n");
        sb.Append("Your notes, kept in `").Append(memoryPath).Append("` and shared through the repository, so a future ")
          .Append(bot.Nickname).Append(" on any machine reads them. Edit the file with your tools when you learn something ")
          .Append("that must outlive this session: decisions, where things are, who owns what, what you were in the middle of. ")
          .Append("Keep a short summary on top and the details below a line that is exactly `---`: only the top (up to ")
          .Append(TeamStore.MemoryMaxBytes / 1024).Append(" KB) ")
          .Append("arrives here; the rest is on disk to Read when you need it. Keep the top current — replace, don't append forever.\n\n");
        var body = (memory ?? "").Trim();
        sb.Append(body.Length > 0 ? body : "(Empty so far.)").Append('\n');
        return sb.ToString();
    }

    /// The shared knowledge as every bot's prompt carries it, with the rule
    /// for adding to it. `pruneCue` is the lead's: the file is over the cap.
    public static string KnowledgeBlock(string knowledge, string path, bool pruneCue = false)
    {
        var sb = new StringBuilder();
        sb.Append("# Team knowledge\n");
        sb.Append("Facts every bot on this team needs, in `").Append(path).Append("` (shared through the repository). ")
          .Append("The moment you learn something a teammate would otherwise rediscover — which table a product reads, an ")
          .Append("environment quirk, a rule Joseph gave — add it: `perch team learn \"<one fact, one line>\"`. Never keep a ")
          .Append("team-wide fact only in your own memory; your memory is for what is yours.\n");
        if (pruneCue)
            sb.Append("**The file is over the cap and is being cut in everyone's prompt: prune it now** — merge duplicates, drop ")
              .Append("what went stale, keep one line per fact.\n");
        var body = (knowledge ?? "").Trim();
        sb.Append(body.Length > 0 ? body : "(Nothing yet.)").Append('\n');
        return sb.ToString();
    }

    /// The team's skills as a list — name, what for, the file — with the
    /// rule for using and adding them. A bot Reads the file when a task
    /// matches; the list stays short so the prompt does.
    public static string SkillsBlock(IReadOnlyList<TeamStore.TeamSkill> skills, string dir)
    {
        var sb = new StringBuilder();
        sb.Append("# Team skills\n");
        sb.Append("Procedures any bot follows, one file each under `").Append(dir).Append("`. When a task matches one, Read ")
          .Append("the file FIRST and follow it; when it is wrong, fix the file. When you have just done something repeatable that ")
          .Append("a teammate could need — how to ship a batch, how to verify a deploy behind login, how to run a report — save ")
          .Append("it: `perch team skill \"<name>\" --file <path>` (or `--text \"…\"`): the steps, the gotchas, what done looks like.\n");
        if (skills.Count == 0) sb.Append("(None yet.)\n");
        foreach (var s in skills)
        {
            sb.Append("- ").Append(s.Name);
            if (s.Summary.Length > 0) sb.Append(" — ").Append(s.Summary);
            sb.Append(" (`").Append(s.Path).Append("`)\n");
        }
        return sb.ToString();
    }

    // ---- runs --------------------------------------------------------------

    /// The system prompt a bot's run starts with: what a run is, the rules that
    /// keep it inside its folder and off main, the bot's brief, the team's
    /// knowledge and skills, and the piece. The instructions themselves are
    /// the run's prompt (RunPrompt).
    public static string RunSystemPrompt(TeamBot bot, TeamPosition? pos, string brief, string projectName, string folder,
        string knowledge, IReadOnlyList<TeamStore.TeamSkill> skills, TaskBoard board, TaskItem? piece, string? allowPath = null)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        var sb = new StringBuilder();
        sb.Append("# You are a run for ").Append(bot.Nickname).Append(", the ").Append(pos?.Name ?? bot.PositionSlug)
          .Append(" on the ").Append(project).Append(" team\n\n");
        sb.Append("Perch started you — a fresh, single-purpose Claude Code session — to implement ONE piece of work in `")
          .Append(folder).Append("`, ").Append(bot.Nickname).Append("'s own checkout on its own branch. Nobody is chatting with you: ")
          .Append("your final answer IS your report, and ").Append(bot.Nickname).Append(" reviews your diff after you. Rules:\n");
        sb.Append("- Do the piece below and nothing else. Read before you change; keep the diff tight; follow the code's own patterns.\n");
        sb.Append("- Commit on the current branch with clear messages. Never push, never merge or rebase onto main, never switch ")
          .Append("branches, never touch files outside this folder, never edit anything under `.perch/`.\n");
        sb.Append("- Verify: run the tests or checks the piece names, plus what covers what you touched. Report exactly what you ")
          .Append("ran and what it printed; never claim a check you did not run.\n");
        sb.Append("- Stop every server or background command you started before you finish. Leave no port held.\n");
        sb.Append("- You cannot ask anyone anything. Where the piece is genuinely ambiguous, take the safe reading, do that, ")
          .Append("and put the question under Open in your report.\n");
        sb.Append("- Reading, editing, committing and the ordinary build and test commands run without asking");
        if (allowPath != null) sb.Append(" (the list is `").Append(allowPath).Append("`)");
        sb.Append("; any other command goes to Joseph as a card and can wait minutes — prefer the ones that don't ask, and ")
          .Append("never work around a denial.\n");
        sb.Append("- Pushing, tickets, messages to teammates and posts to the room are not yours to do.\n\n");
        sb.Append("## ").Append(bot.Nickname).Append("'s standing brief (yours for this run)\n\n");
        sb.Append(string.IsNullOrWhiteSpace(brief) ? "(No brief; work from the piece and the code.)\n" : brief.Trim() + "\n");
        sb.Append("\n## Team knowledge\n").Append(string.IsNullOrWhiteSpace(knowledge) ? "(Nothing yet.)\n" : knowledge.Trim() + "\n");
        sb.Append("\n## Team skills\n");
        if (skills.Count == 0) sb.Append("(None yet.)\n");
        else
        {
            sb.Append("Read a skill's file and follow it when the piece matches it:\n");
            foreach (var s in skills)
                sb.Append("- ").Append(s.Name).Append(s.Summary.Length > 0 ? " — " + s.Summary : "").Append(" (`").Append(s.Path).Append("`)\n");
        }
        sb.Append("\n## The task\n");
        sb.Append("- Task ").Append(board.Id).Append(": ").Append(OneLine(board.Title, 300)).Append('\n');
        if (piece != null)
        {
            sb.Append("- ").Append(bot.Nickname).Append("'s piece: [").Append(piece.Status).Append("] ").Append(OneLine(piece.Title, 200));
            var note = OneLine(piece.Note, 200);
            if (note.Length > 0) sb.Append(" — ").Append(note);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// The run's prompt: the bot's instructions, then the shape of the report.
    public static string RunPrompt(string instructions)
        => instructions.Trim() + "\n\nWhen you are done (or cannot go on), report: status (done | partial | blocked), a summary of " +
           "at most three lines, what changed (files, commits), what you verified (each command and what it printed, in short), " +
           "and what is open (questions, leftovers, anything a reviewer must know).";

    /// What a run must answer with (`--json-schema`), so the report always has
    /// the same five parts and the room can render it.
    public const string RunReportSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"status\":{\"type\":\"string\",\"enum\":[\"done\",\"partial\",\"blocked\"]}," +
        "\"summary\":{\"type\":\"string\"}," +
        "\"changed\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        "\"verified\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        "\"open\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}}," +
        "\"required\":[\"status\",\"summary\",\"changed\",\"verified\",\"open\"]}";

    /// A run's report as parsed from its structured answer (or the prose,
    /// when the schema was not honoured).
    public sealed record RunReport(string Status, string Summary, IReadOnlyList<string> Changed, IReadOnlyList<string> Verified, IReadOnlyList<string> Open);

    public static RunReport ParseRunReport(string? structured, string prose, bool ok)
    {
        if (!string.IsNullOrWhiteSpace(structured))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(structured);
                var r = doc.RootElement;
                static IReadOnlyList<string> Arr(System.Text.Json.JsonElement e, string name)
                {
                    var list = new List<string>();
                    if (e.TryGetProperty(name, out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Array)
                        foreach (var x in a.EnumerateArray()) if (x.ValueKind == System.Text.Json.JsonValueKind.String) list.Add(x.GetString() ?? "");
                    return list;
                }
                var status = r.TryGetProperty("status", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() ?? "" : "";
                var summary = r.TryGetProperty("summary", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() ?? "" : "";
                if (status.Length > 0) return new RunReport(status, summary.Trim(), Arr(r, "changed"), Arr(r, "verified"), Arr(r, "open"));
            }
            catch { /* prose below */ }
        }
        return new RunReport(ok ? "done" : "failed", (prose ?? "").Trim(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
    }

    /// The report as the artefact the room keeps: status, cost and time on
    /// top, then the four parts.
    public static string RunReportMarkdown(string runId, TaskBoard board, TeamBot bot, RunReport rep, double costUsd, long durationMs, string? error)
    {
        var sb = new StringBuilder();
        sb.Append("# Run ").Append(runId).Append(" — ").Append(OneLine(board.Title, 120)).Append("\n\n");
        sb.Append("**Status:** ").Append(rep.Status).Append(" · **For:** ").Append(bot.Nickname)
          .Append(" · **Cost:** $").Append(costUsd.ToString("F2", System.Globalization.CultureInfo.InvariantCulture))
          .Append(" · **Took:** ").Append(Elapsed(durationMs)).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(error)) sb.Append("**Ended with:** ").Append(error.Trim()).Append("\n\n");
        if (rep.Summary.Length > 0) sb.Append(rep.Summary).Append("\n\n");
        void Part(string title, IReadOnlyList<string> items)
        {
            sb.Append("## ").Append(title).Append('\n');
            if (items.Count == 0) sb.Append("- (nothing)\n");
            foreach (var i in items) sb.Append("- ").Append(i.Trim()).Append('\n');
            sb.Append('\n');
        }
        Part("Changed", rep.Changed);
        Part("Verified", rep.Verified);
        Part("Open", rep.Open);
        return sb.ToString().TrimEnd() + "\n";
    }

    /// The line typed into the bot when its run ends: the outcome, where the
    /// full report is, and what the bot does now.
    public static string RunResultLine(TeamBot bot, string runId, string taskId, RunReport rep, string? reportPath, bool canceled)
    {
        var what = canceled ? "was stopped by Joseph"
                 : rep.Status == "failed" ? "failed"
                 : "finished — " + rep.Status;
        var summary = OneLine(rep.Summary, 300).TrimEnd('.');
        var sb = new StringBuilder();
        sb.Append(PostPrefix).Append(" run ").Append(runId).Append(" → @").Append(bot.Nickname).Append(": your run on task ")
          .Append(taskId).Append(' ').Append(what);
        if (summary.Length > 0) sb.Append(": ").Append(summary);
        if (reportPath != null) sb.Append(". Full report: ").Append(reportPath);
        sb.Append(canceled ? ". Check the folder for half-done work, then update your piece and REPORT: to the lead."
                           : ". Review its diff in your folder, verify what it claims, fix a one-liner yourself or start another run, then update your piece and REPORT: to the lead.");
        return sb.ToString();
    }

    public static string Elapsed(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:D2}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds:D2}s" : $"{t.Seconds}s";
    }

    /// The memory file a new bot starts with: its name, and the rule, so the
    /// first thing it reads there is how to use it.
    public static string MemorySeed(TeamBot bot)
        => "# " + bot.Nickname + " — memory\n\n"
         + "Notes " + bot.Nickname + " keeps for itself across sessions and machines. "
         + "Short, current, newest first.\n";

    // ---- brief generation ------------------------------------------------

    /// Headings the brief must carry, in order. Tested against BriefPrompt and
    /// used by the page to sanity-check a hand-written brief.
    public static readonly string[] BriefHeadings =
    {
        "## Role",
        "## What you own",
        "## What you never touch",
        "## Who you ask",
        "## Definition of done",
        "## How you communicate on the team",
    };

    /// The prompt for the headless run that writes a brief. The run is
    /// read-only and confined to the reference repository; the prompt asks for
    /// real paths so the brief is about THIS code, not a generic job ad.
    public static string BriefPrompt(TeamPosition pos, string projectName)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "this" : projectName.Trim();
        var sb = new StringBuilder();
        sb.Append("You are helping set up an AI teammate for the \"").Append(project).Append("\" repository, ")
          .Append("which is your current working directory.\n\n");
        sb.Append("Position: ").Append(pos.Name.Trim()).Append('\n');
        sb.Append("Purpose, in the owner's words: ").Append(pos.Purpose.Trim()).Append("\n\n");
        sb.Append("Explore this repository read-only — the README, the top-level layout, build and test files, and the areas ")
          .Append("this position would own — then write a standing brief for someone holding this position on a small team of ")
          .Append("AI teammates. Each teammate runs in its own Claude Code session, works in its own copy of the repository, ")
          .Append("and reaches the others with the SendMessage tool by session name; the human owner reads a shared team room. ")
          .Append("Be concrete and cite real paths from this repository.\n\n");
        sb.Append("Use exactly these headings, in this order, and nothing else:\n");
        foreach (var h in BriefHeadings) sb.Append(h).Append('\n');
        sb.Append('\n');
        sb.Append("Rules: markdown only; at most 700 words; second person (\"you own …\"); no preamble and no closing remarks; ")
          .Append("do not invent teammates — refer to them by position (\"the backend dev\"), because the real roster is supplied ")
          .Append("at runtime; under \"How you communicate on the team\" say when to message a teammate, when to post a note ")
          .Append("to the room instead, and what a good message contains.\n");
        return sb.ToString();
    }

    // ---- helpers ---------------------------------------------------------

    /// Collapse to one line and cap the length, for roster and prompt rows.
    internal static string OneLine(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var flat = s.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        if (flat.Length > max) flat = flat[..max].TrimEnd() + "…";
        return flat;
    }
}
