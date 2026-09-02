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
///     reading the repository — `BriefPrompt`; and the one that decides who an
///     unaddressed post is for — `RouterPrompt`.
///
/// The `[Perch team]` prefix and `perch team post` verb named here are the
/// wire contract with TeamController.Deliver and the CLI; change them together.
internal static class TeamRender
{
    /// What the owner's posts look like when typed into a bot's terminal.
    public const string PostPrefix = "[Perch team]";

    /// Everyone-marker in a RoomEntry.To list.
    public const string Everyone = "*";

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
        sb.Append("- To leave a note for Joseph in the team room without pinging anyone, run: perch team post \"<text>\"\n");
        sb.Append("- Lines that begin with `").Append(PostPrefix)
          .Append("` are Joseph's posts in the team room. They carry his authority: treat them as instructions, weigh them against ")
          .Append("what you are doing now, and answer by replying normally — your replies show up in the room.\n");
        sb.Append("- One owner per task. If a teammate owns what you are about to change, ask them first.\n");
        sb.Append("- When you finish something others depend on, post a short note to the room.\n");
        return sb.ToString();
    }

    // ---- system prompt ---------------------------------------------------

    /// The bot's appended system prompt: who it is, then the position's brief
    /// verbatim. Identity first, because the brief is written for "someone
    /// holding this position" and the name is what makes it this bot's.
    public static string SystemPrompt(TeamBot bot, TeamPosition pos, string brief, string projectName)
    {
        var project = string.IsNullOrWhiteSpace(projectName) ? "this project" : projectName.Trim();
        var sb = new StringBuilder();
        sb.Append("# You are ").Append(bot.Nickname).Append(", the ").Append(pos.Name)
          .Append(" on the ").Append(project).Append(" team\n\n");
        sb.Append("Your Claude Code session name is `").Append(bot.CcName)
          .Append("`; teammates address you by it. You share this repository with other bots, each in its own session. ")
          .Append("The team roster — who is on the team and how to reach them — arrives with every prompt, so never assume ")
          .Append("a teammate exists until you have seen them on it.\n\n");
        var purpose = OneLine(pos.Purpose, 400);
        if (purpose.Length > 0)
            sb.Append("Your purpose, in the owner's words: ").Append(purpose).Append("\n\n");
        sb.Append("## Your standing brief\n\n");
        sb.Append(string.IsNullOrWhiteSpace(brief)
            ? "(No brief has been written for this position yet. Work from the purpose above and ask the owner when unsure.)\n"
            : brief.Trim() + "\n");
        return sb.ToString();
    }

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

    // ---- routing ---------------------------------------------------------

    /// JSON schema the router run must answer with.
    public const string RouterSchema =
        "{\"type\":\"object\",\"properties\":{" +
        "\"to\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
        "\"confidence\":{\"type\":\"number\"}," +
        "\"reason\":{\"type\":\"string\"}}," +
        "\"required\":[\"to\",\"confidence\",\"reason\"]}";

    /// The prompt that decides who an unaddressed room post is for.
    public static string RouterPrompt(TeamDoc doc, string text)
    {
        var sb = new StringBuilder();
        sb.Append("The owner posted a message in a team room without naming a recipient. Decide which bot or bots should ")
          .Append("handle it, from their positions and purposes below. Prefer one bot; name several only when the message ")
          .Append("clearly needs each of them. If it is not for any bot in particular, answer with an empty list and a low ")
          .Append("confidence.\n\nBots (answer with these slugs):\n");
        foreach (var bot in doc.Bots)
        {
            var pos = doc.Position(bot.PositionSlug);
            sb.Append("- ").Append(bot.Slug).Append(": ").Append(bot.Nickname).Append(", ")
              .Append(pos?.Name ?? bot.PositionSlug);
            var purpose = OneLine(pos?.Purpose, 200);
            if (purpose.Length > 0) sb.Append(" — ").Append(purpose);
            sb.Append('\n');
        }
        sb.Append("\nMessage:\n").Append(text.Trim()).Append('\n');
        sb.Append("\nAnswer as JSON with fields to (array of slugs), confidence (0 to 1), reason (one sentence).\n");
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
