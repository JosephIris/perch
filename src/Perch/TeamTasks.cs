using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Perch;

/// The team's task board: ONE main task at a time, set by the lead (or the
/// owner), with a piece per bot that the bot keeps current. Lives beside
/// team.json as `tasks.json`, shared through the repository like the team.
///
/// ## Why one task
///
/// The board is the unit of a bot's context. A bot's conversation grows
/// with every turn until Claude Code compacts it; the only clean moment to
/// start fresh is when the thing everyone was working on is DONE. So a task
/// finishing is not a status change, it is the cue: bots write what the
/// next task needs into their memory, and their contexts are cleared.
/// Several tasks in flight would mean no such moment.
///
/// ## Status
///
/// open → review (the lead asked the owner to confirm) → done (the owner
/// confirmed; bots are wrapping up) → archived (every running bot has been
/// reset; the board moves to `Done` and `Current` is null again).
internal sealed class TaskDoc
{
    public int V { get; set; } = 1;
    public TaskBoard? Current { get; set; }
    /// Finished boards, newest last; capped so the file stays a file.
    public List<TaskBoard> Done { get; set; } = new();

    public const int DoneKept = 20;
}

internal sealed class TaskBoard
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// "open" | "review" | "done".
    public string Status { get; set; } = "open";
    /// Bot slug, or "you" for the owner.
    public string SetBy { get; set; } = "";
    public long CreatedAtMs { get; set; }
    /// Who asked for confirmation, and when (status "review").
    public string? ReviewBy { get; set; }
    public long? ReviewAtMs { get; set; }
    public long? DoneAtMs { get; set; }
    public List<TaskItem> Items { get; set; } = new();

    public TaskItem? ItemOf(string botSlug) =>
        Items.Find(i => string.Equals(i.Bot, botSlug, StringComparison.OrdinalIgnoreCase));
}

/// One bot's piece of the task. A bot has at most one; the lead may set it
/// for them, the bot keeps it current.
internal sealed class TaskItem
{
    public string Bot { get; set; } = "";
    public string Title { get; set; } = "";
    /// "todo" | "doing" | "done" | "blocked".
    public string Status { get; set; } = "todo";
    /// One line of progress, in the bot's words.
    public string Note { get; set; } = "";
    public long UpdatedAtMs { get; set; }

    public static readonly string[] Statuses = { "todo", "doing", "done", "blocked" };
}

[JsonSerializable(typeof(TaskDoc))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class TaskJsonContext : JsonSerializerContext { }
