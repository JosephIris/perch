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
        IReadOnlyDictionary<string, string>? presence = null)
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
                sb.Append("- ").Append(bot.Nickname).Append(" (session name `").Append(bot.CcName).Append("`) — ")
                  .Append(pos?.Name ?? bot.PositionSlug);
                var purpose = OneLine(pos?.Purpose, 160);
                if (purpose.Length > 0) sb.Append(": ").Append(purpose);
                if (presence != null && presence.TryGetValue(bot.Slug, out var word) && !string.IsNullOrEmpty(word))
                    sb.Append(" [").Append(word).Append(']');
                sb.Append('\n');
            }
        }

        sb.Append("\nHow to work together:\n");
        sb.Append("- To message a teammate, use your SendMessage tool with `to` set to their session name (for example `")
          .Append(doc.Bots.Count > 0 ? doc.Bots[0].CcName : "ada")
          .Append("`). They read it at their next step, or wake up if idle. Say what you need, where things are, and by when.\n");
        sb.Append("- To tell everyone, send one short message to each teammate. Never send the same message twice.\n");
        sb.Append("- Lines that begin with `").Append(PostPrefix)
          .Append("` are Joseph's posts from the team room. They carry his authority: treat them as instructions and weigh them ")
          .Append("against what you are doing now.\n");
        sb.Append("- `→ @everyone` means Joseph named no one. Decide whether it is for you: answer if it concerns your position or ")
          .Append("your current work; otherwise reply with exactly `").Append(NoReply)
          .Append("` and nothing else. A post that names you always gets an answer.\n");
        sb.Append("- Your reply to a room post is what Joseph reads in the room. Give the outcome, a question, or a blocker, in a ")
          .Append("few lines. Do not narrate messages you sent to teammates (the room shows them), do not confirm receipt, and do ")
          .Append("not say that nothing is pending.\n");
        sb.Append("- After a teammate's message, your reply stays in your own terminal. When Joseph needs to know something, run: ")
          .Append("perch team post \"<text>\"\n");
        sb.Append("- One owner per task. If a teammate owns what you are about to change, ask them first.\n");
        sb.Append("- When you finish something others depend on, post a short note to the room.\n");
        return sb.ToString();
    }

    // ---- system prompt ---------------------------------------------------

    /// The bot's appended system prompt: who it is, then the position's brief
    /// verbatim. Identity first, because the brief is written for "someone
    /// holding this position" and the name is what makes it this bot's.
    /// `memoryPath` names the file the bot keeps its notes in; the notes
    /// themselves ride in with every prompt (Context), not here, so an edit
    /// mid-session is seen at the next turn rather than the next launch.
    public static string SystemPrompt(TeamBot bot, TeamPosition pos, string brief, string projectName, string? memoryPath = null)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        var sb = new StringBuilder();
        sb.Append("# You are ").Append(bot.Nickname).Append(", the ").Append(pos.Name)
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
        return sb.ToString();
    }

    // ---- per-prompt context ----------------------------------------------

    /// What the prompt hook inlines for one bot: the shared roster, then that
    /// bot's own memory with the rule for keeping it. One file per bot
    /// (local/bots/&lt;slug&gt;/context.md) because the memory is the bot's alone.
    public static string Context(string roster, TeamBot bot, string memory, string memoryPath)
    {
        var sb = new StringBuilder();
        sb.Append(roster.TrimEnd()).Append("\n\n");
        sb.Append("# Your memory\n");
        sb.Append("Your notes, kept in `").Append(memoryPath).Append("` and shared through the repository, so a future ")
          .Append(bot.Nickname).Append(" on any machine reads them. Edit the file with your tools when you learn something ")
          .Append("that must outlive this session: decisions, where things are, who owns what, what you were in the middle of. ")
          .Append("Keep it under 2 KB and current — replace, don't append forever.\n\n");
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
