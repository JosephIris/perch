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
        sb.Append("- Close each: when every piece of a task is done and you have checked the result, run ")
          .Append("`perch team task done <id>`. That asks Joseph to confirm. Do not say a task is done in prose; the command is ")
          .Append("what the room shows.\n");
        sb.Append("- After Joseph confirms a task, the bots whose work was all on it (you included, if so) write what the next task ")
          .Append("needs into their memory file and are reset; a bot with a piece on another open task carries on. Expect to ")
          .Append("start a fresh task with only your brief, the roster, the board and your memory.\n");
        return sb.ToString();
    }

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
    public static string Context(string roster, TeamBot bot, string memory, string memoryPath, string? taskBlock = null)
    {
        var sb = new StringBuilder();
        sb.Append(roster.TrimEnd()).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(taskBlock)) sb.Append(taskBlock.TrimEnd()).Append("\n\n");
        sb.Append("# Your memory\n");
        sb.Append("Your notes, kept in `").Append(memoryPath).Append("` and shared through the repository, so a future ")
          .Append(bot.Nickname).Append(" on any machine reads them. Edit the file with your tools when you learn something ")
          .Append("that must outlive this session: decisions, where things are, who owns what, what you were in the middle of. ")
          .Append("Keep a short summary on top and the details below a line that is exactly `---`: only the top (up to 4 KB) ")
          .Append("arrives here; the rest is on disk to Read when you need it. Keep the top current — replace, don't append forever.\n\n");
        var body = (memory ?? "").Trim();
        sb.Append(body.Length > 0 ? body : "(Empty so far.)").Append('\n');
        return sb.ToString();
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
