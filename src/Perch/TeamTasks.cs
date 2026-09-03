using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Perch;

/// The team's task board: the OPEN tasks, each with a piece per bot that the
/// bot keeps current, set by the lead (or the owner). Lives beside team.json
/// as `tasks.json`, shared through the repository like the team.
///
/// ## Why several tasks, and what "done" still means
///
/// A team rarely has exactly one thing in flight: the owner hands work to
/// one bot while the lead runs a bigger piece with two others. Each such
/// thing is a card in the room. What stays from the one-task design is the
/// cue a finished task gives: the bots whose work was ALL on that task write
/// what the next one needs into their memory and have their contexts
/// cleared. A bot with a piece on another open task is told and carries on.
///
/// ## Status
///
/// open → review (the lead asked the owner to confirm) → done (the owner
/// confirmed; its bots are wrapping up) → archived (every one of them has
/// been reset; the board moves to `Done`).
internal sealed class TaskDoc
{
    public int V { get; set; } = 2;
    /// Boards not yet archived, oldest first.
    public List<TaskBoard> Open { get; set; } = new();
    /// Finished boards, newest last; capped so the file stays a file.
    public List<TaskBoard> Done { get; set; } = new();

    /// The v1 file's single board. Read once by TeamStore and folded into
    /// Open; never written again.
    [JsonPropertyName("current")] public TaskBoard? LegacyCurrent { get; set; }

    public const int DoneKept = 20;

    public TaskBoard? Board(string? id) =>
        id == null ? null : Open.Find(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// Boards that are still being worked (not done).
    public IEnumerable<TaskBoard> Active => Open.Where(b => b.Status != "done");

    /// The boards a bot has a piece on.
    public IEnumerable<TaskBoard> For(string botSlug) => Open.Where(b => b.ItemOf(botSlug) != null);

    /// Fold a v1 `current` into Open. Idempotent.
    public void Migrate()
    {
        if (LegacyCurrent != null)
        {
            if (Board(LegacyCurrent.Id) == null) Open.Insert(0, LegacyCurrent);
            LegacyCurrent = null;
        }
        if (V < 2) V = 2;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
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

/// One bot's piece of a task. A bot has at most one per task; the lead may
/// set it for them, the bot keeps it current.
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
