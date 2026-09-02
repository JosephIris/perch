using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Perch;

/// A project's team: the positions it has defined and the bots filling them.
///
/// ## Positions versus bots
///
/// A POSITION is the durable thing — "Frontend dev", a purpose in the owner's
/// words, and a standing brief that Claude wrote after reading the repository.
/// A BOT is one Claude Code session holding that position under a nickname.
/// Two bots may share a position ("Ada" and "Bo" are both Frontend devs); they
/// share the brief but have their own session, tab, worktree, and address.
///
/// The brief itself is NOT in this document. It lives beside the position as
/// `positions/&lt;slug&gt;/brief.md` so a human can edit it in place and a diff
/// reads as prose, not as a JSON string with escaped newlines.
///
/// ## Identity and addresses
///
/// `CcName` is the `--name` the bot's session actually runs under, which is
/// what teammates put in SendMessage. It is derived from the nickname
/// (slugified) and made unique across the whole app at creation time, so the
/// address a teammate is told is the address that works.
internal sealed class TeamDoc
{
    public int V { get; set; } = 1;
    public List<TeamPosition> Positions { get; set; } = new();
    public List<TeamBot> Bots { get; set; } = new();

    public TeamPosition? Position(string slug) =>
        Positions.Find(p => string.Equals(p.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public TeamBot? Bot(string slug) =>
        Bots.Find(b => string.Equals(b.Slug, slug, StringComparison.OrdinalIgnoreCase));

    public TeamBot? BotBySession(Guid sessionId) =>
        Bots.Find(b => b.SessionId == sessionId);

    public TeamBot? BotByCcName(string ccName) =>
        Bots.Find(b => string.Equals(b.CcName, ccName, StringComparison.OrdinalIgnoreCase));

    /// True when at least one bot holds the position — the guard against
    /// deleting a brief that a running session is built on.
    public bool PositionInUse(string slug) =>
        Bots.Exists(b => string.Equals(b.PositionSlug, slug, StringComparison.OrdinalIgnoreCase));
}

internal sealed class TeamPosition
{
    /// Folder key under `positions/`; slugified from Name, deduped "-2", "-3".
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    /// What the owner typed. Plain language; the brief is generated from it.
    public string Purpose { get; set; } = "";
    /// Absolute path Claude reads when writing the brief. Normally the project
    /// folder; may be a different repo when the position is "about" one.
    public string ReferenceRepo { get; set; } = "";
    /// Default model alias for bots in this position. "" = the app default.
    public string Model { get; set; } = "";
    public long CreatedAtMs { get; set; }
    /// 0 when the brief was hand-written (or never written).
    public long BriefGeneratedAtMs { get; set; }
    /// The model that generated the brief, for the "regenerate" affordance.
    public string BriefModel { get; set; } = "";
}

internal sealed class TeamBot
{
    /// Folder key under `bots/`; slugified from Nickname, deduped within the team.
    public string Slug { get; set; } = "";
    /// What the owner sees and @-mentions.
    public string Nickname { get; set; } = "";
    public string PositionSlug { get; set; } = "";
    /// The session name the bot ACTUALLY runs under (`claude --name`), unique
    /// app-wide. This is the SendMessage address teammates are told.
    public string CcName { get; set; } = "";
    /// The Perch tab hosting the bot. Null once the tab is gone — the bot is
    /// then "not running" and can be relaunched under the same nickname.
    public Guid? SessionId { get; set; }
    public bool Worktree { get; set; } = true;
    /// Per-bot model override; "" defers to the position's model.
    public string Model { get; set; } = "";
    public long CreatedAtMs { get; set; }
}

[JsonSerializable(typeof(TeamDoc))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class TeamJsonContext : JsonSerializerContext { }
