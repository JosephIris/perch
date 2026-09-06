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
/// cue a finished task gives: a bot that worked on it writes what the next
/// one needs into its memory and has its context cleared — but only once it
/// is FREE, with nothing of its own left on any open card, and in one go for
/// every card it finished since it was last reset. Confirming a card records
/// the fact (`DoneAtMs`, and later `WrappedBy` per bot); the harness decides
/// when each bot acts on it (TeamController.SweepWraps).
///
/// ## Status
///
/// open → review (the lead asked the owner to confirm) → done (the owner
/// confirmed; the board moves to `Done` at once, and each bot that worked
/// on it is added to `WrappedBy` when it has written it up and been reset).
internal sealed class TaskDoc
{
    public int V { get; set; } = 3;
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

    /// An archived board by id (the reopen path).
    public TaskBoard? Archived(string? id) =>
        id == null ? null : Done.Find(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));

    /// Confirmed boards this bot worked on and has not yet written into its
    /// memory — what its next wrap-up covers. Oldest first.
    public IEnumerable<TaskBoard> Unwritten(string botSlug) =>
        Done.Where(b => b.Worked(botSlug) && !b.WrittenUpBy(botSlug));

    /// Whether the bot has work of its own in flight on the open board, so a
    /// wrap-up would cut into it. A piece the bot has STARTED counts (any
    /// status but "todo"); one just handed out does not — the wrap-up goes
    /// first and the piece waits, fresh context and all. The lead runs every
    /// open card, so any open card is the lead's work.
    public bool HoldsWork(string botSlug, bool isLead) =>
        Open.Any(b => isLead || b.ItemOf(botSlug) is { } i && i.Status != "todo");

    /// Fold a v1 `current` into Open; mark every board archived before v3 as
    /// written up, so the first run of the wrap-up sweep has nothing old to
    /// ask about. Idempotent.
    public void Migrate()
    {
        if (LegacyCurrent != null)
        {
            if (Board(LegacyCurrent.Id) == null) Open.Insert(0, LegacyCurrent);
            LegacyCurrent = null;
        }
        if (V < 3)
        {
            foreach (var b in Done) b.WrappedBy = new List<string> { TaskBoard.WrappedByAll };
            V = 3;
        }
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
    /// Once archived: the bots that have written this board into their memory
    /// and been reset (or had nothing to write from — a closed tab). `*`
    /// means everyone: a card the owner removed, or one archived before the
    /// sweep existed.
    public List<string> WrappedBy { get; set; } = new();

    public const string WrappedByAll = "*";

    public TaskItem? ItemOf(string botSlug) =>
        Items.Find(i => string.Equals(i.Bot, botSlug, StringComparison.OrdinalIgnoreCase));

    /// Whether the bot had a hand in this board: a piece, or it opened or
    /// closed the card (the lead's orchestration is work too).
    public bool Worked(string botSlug) =>
        ItemOf(botSlug) != null
        || string.Equals(SetBy, botSlug, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ReviewBy, botSlug, StringComparison.OrdinalIgnoreCase);

    public bool WrittenUpBy(string botSlug) =>
        WrappedBy.Any(w => w == WrappedByAll || string.Equals(w, botSlug, StringComparison.OrdinalIgnoreCase));

    public void MarkWrittenUp(string botSlug)
    {
        if (!WrittenUpBy(botSlug)) WrappedBy.Add(botSlug);
    }
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
