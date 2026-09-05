using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Perch;

// Typed page → host protocol. These records are the C# mirror of the
// `OutMessage` union in src/web/src/bridge.ts — one record per message type,
// property names matching the wire (camelCase via JsonSerializerDefaults.Web).
// If you add/rename a field THERE, change it HERE, or the deserializer throws
// and the mismatch lands in the log with the offending payload — instead of a
// silently-default Guid/0/null propagating into a dead button.
//
// `required` marks the fields a handler cannot act without: a payload missing
// one fails deserialization loudly. Optional wire fields are nullable.

internal sealed record PaneRef
{
    public required Guid PaneId { get; init; }
}

internal sealed record SessionRef
{
    public required Guid Id { get; init; }
}

internal sealed record PaneInMsg
{
    public required Guid PaneId { get; init; }
    public required string B64 { get; init; }
}

internal sealed record PaneAckMsg
{
    public required Guid PaneId { get; init; }
    public required long Bytes { get; init; }
}

internal sealed record PaneResizeMsg
{
    public required Guid PaneId { get; init; }
    public required int Cols { get; init; }
    public required int Rows { get; init; }
}

internal sealed record RenderPongMsg
{
    public required int Id { get; init; }
}

internal sealed record PaneSplitMsg
{
    public required Guid PaneId { get; init; }
    /// "right" (default) or "down".
    public string? Dir { get; init; }
    /// When set the new leaf is a URL (WebView2) pane instead of a terminal.
    public string? Url { get; init; }
}

internal sealed record PaneChooserChooseMsg
{
    public required Guid PaneId { get; init; }
    /// "agent" | "same" | "default" | "cancel".
    public string? Choice { get; init; }
}

internal sealed record ResizeSplitMsg
{
    public required Guid SplitId { get; init; }
    public required double[] Weights { get; init; }
    /// False for throttled mid-drag updates; true/omitted on the final mouseup.
    public bool? Final { get; init; }
}

internal sealed record PaneMoveMsg
{
    public required Guid Src { get; init; }
    public required Guid Target { get; init; }
    /// "left" | "right" | "top" | "bottom" | "center".
    public required string Edge { get; init; }
}

internal sealed record PaneMoveDirMsg
{
    public required Guid PaneId { get; init; }
    /// "left" | "right" | "up" | "down".
    public required string Dir { get; init; }
}

/// Sidebar drag-reorder: place MovedId immediately before/after TargetId.
/// Kind "project" reorders project groups; "tab" reorders a session within its
/// project. Ids are Guid strings (parsed leniently against the store).
internal sealed record SidebarReorderMsg
{
    public required string Kind { get; init; }      // "project" | "tab"
    public required string MovedId { get; init; }
    public required string TargetId { get; init; }
    public required string Edge { get; init; }      // "before" | "after"
}

internal sealed record PaneRenameMsg
{
    public required Guid PaneId { get; init; }
    public required string Name { get; init; }
}

internal sealed record PaneRecolorMsg
{
    public required Guid PaneId { get; init; }
    public required int ColorIndex { get; init; }
}

internal sealed record PaneCwdMsg
{
    public required Guid PaneId { get; init; }
    public required string Cwd { get; init; }
}

/// Per-pane Claude Code model pick from the pane header's model menu. Model is
/// a CLI alias ("fable"/"opus"/"sonnet"/"haiku") or "" for the account default.
/// The host clamps anything else, persists it on the pane, writes the wrap-claude
/// state file, and — when cc is already running — types `/model <alias>` live.
internal sealed record PaneModelMsg
{
    public required Guid PaneId { get; init; }
    public required string Model { get; init; }
}

/// Page-side state reconciliation: the page watched a pane's terminal buffer
/// and it disagrees with the host's agent state. PermissionVisible=false —
/// cc's permission dialog left the screen (the exits that fire no hook: Esc,
/// deny-with-feedback; plus approve-then-slow-tool). BlockedVisible=true — a
/// blocked dialog sits on a pane the host thinks is done. BlockedVisible=
/// false — the dialog behind an inferred waiting left. Only the relevant
/// field is sent; the handler treats each independently and only ever
/// overrides INFERRED states (or Permission, whose exits are hook-less).
/// See Pane.probeTick in pane.ts.
internal sealed record PaneProbeMsg
{
    public required Guid PaneId { get; init; }
    public bool? PermissionVisible { get; init; }
    public bool? BlockedVisible { get; init; }
}

internal sealed record UrlPaneLayoutMsg
{
    public required Guid PaneId { get; init; }
    public required string Url { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
}

/// Airspace fix: URL panes are native WebView2 child HWNDs that paint above the
/// host's HTML, so a DOM modal can't cover them. While a full-viewport modal is
/// up the page sends suppress=true (host hides every web pane); false on close.
internal sealed record WebPanesSuppressMsg
{
    public bool Suppress { get; init; }
}

/// Per-pane visibility, driven by stage switches. visible=false hides the pane's
/// native WebView2 (IsVisible=false) when its session is switched away from —
/// crucially it does NOT close it, so returning is instant with no reload;
/// visible=true re-shows it on return.
internal sealed record UrlPaneVisibleMsg
{
    public required Guid PaneId { get; init; }
    public required bool Visible { get; init; }
}

/// Add something to a board. `Kind` is "note" to force a typed note, "path" for
/// a file the user explicitly picked, or "auto" to let the host classify the
/// text (a URL, a file path, or — the fallback — a note). The page can't
/// classify it itself, because deciding whether a path is inside the repo needs
/// the repo root.
internal sealed record BoardAddMsg
{
    public required Guid PaneId { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    /// Optional caption to sit under a file/reference card.
    public string? Note { get; init; }
    public double X { get; init; }
    public double Y { get; init; }

    /// Who staged this: "user" for a deliberate human gesture (typing, pasting,
    /// dropping onto the canvas), anything else for everyone.
    ///
    /// This decides whether an out-of-repo path is allowed, and it defaults to
    /// NOT-user on purpose. board.md is handed to an agent as a list of files
    /// to open, so a path that escapes the project widens what the agent can
    /// read. A human choosing to stage a reference file is an informed
    /// decision; an agent staging one would be widening its own read scope,
    /// which is the thing containment exists to prevent. An omitted field
    /// (older page bundle, replayed message, anything synthesized) must
    /// therefore land on the restrictive side.
    public string? Origin { get; init; }

    public bool IsUserStaged => string.Equals(Origin, "user", StringComparison.Ordinal);
}

/// Replace a node's text from an inline edit on the card: a note's body, or the
/// caption under a file/image/reference.
internal sealed record BoardEditMsg
{
    public required Guid PaneId { get; init; }
    public required string NodeId { get; init; }
    public required string Text { get; init; }
}

/// Open a file picker and add what comes back at (X, Y). The DIALOG is the
/// host's job — the page has no way to browse the user's disk, and typing a
/// repo-relative path from memory is the kind of chore a board exists to avoid.
internal sealed record BoardPickFileMsg
{
    public required Guid PaneId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
}

/// A paste landed on a board at (X, Y). Carries NO payload — the host reads the
/// clipboard itself, because a multi-megabyte image as base64 over the page
/// bridge is several transient copies of itself.
internal sealed record BoardPasteMsg
{
    public required Guid PaneId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
}

/// A node identity: used by board.remove and board.image.
internal sealed record BoardNodeRefMsg
{
    public required Guid PaneId { get; init; }
    public required string NodeId { get; init; }
}

/// Drag a card. `Final` false is the continuous part of the gesture: the host
/// updates its model but does NOT write board.md or echo state back. Same
/// distinction the split-gutter drag makes, for the same reason — otherwise one
/// drag rewrites the file hundreds of times.
internal sealed record BoardMoveMsg
{
    public required Guid PaneId { get; init; }
    public required string NodeId { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public bool Final { get; init; } = true;
}

/// Resize a card. Same Final semantics as BoardMoveMsg.
internal sealed record BoardResizeMsg
{
    public required Guid PaneId { get; init; }
    public required string NodeId { get; init; }
    public double W { get; init; }
    public double H { get; init; }
    public bool Final { get; init; } = true;
}

internal sealed record SessionNewMsg
{
    public string? Shell { get; init; }

    /// When present, the new session is a tab of that project: it's filed under
    /// the project in the sidebar and opens in the project's directory.
    /// Absent (the plain "New session" button) → unfiled, default cwd.
    public Guid? ProjectId { get; init; }
}

/// Which sidebar mode to show — "sessions" or "projects". Anything else is
/// ignored (the host clamps), so a stale page can't wedge the sidebar into a
/// mode that doesn't render.
internal sealed record UiModeMsg
{
    public required string Mode { get; init; }
}

/// The cloud panel opened or closed. Drives the poll cadence — fast while you're
/// looking at it, slow (5 min) otherwise, since every tick is a gcloud subprocess.
internal sealed record CloudPanelMsg
{
    public required bool Open { get; init; }
}

/// Delete one cloud resource. `Id` is the poller's stable key — "cluster/<name>"
/// or "<zone>/<name>" — never a raw resource name, because a VM and a Dataproc
/// cluster of the same name take entirely different delete commands.
internal sealed record CloudDeleteMsg
{
    public required string Id { get; init; }
}

/// The local dev-servers panel opened or closed. Drives the scan cadence — fast
/// while you're looking at it, slow otherwise, since every scan is a subprocess.
internal sealed record LocalPanelMsg
{
    public required bool Open { get; init; }
}

/// Kill one local server. `Pid` is the exact owning process id from the scan —
/// never a name, because a stale dev server and a real service can share a
/// process name, and killing by name is how you take down the wrong one.
internal sealed record LocalKillMsg
{
    public required int Pid { get; init; }
}

/// Open http://localhost:&lt;Port&gt; in the system browser. Host-side so it lands in
/// the real default browser, not a webview popup.
internal sealed record LocalOpenMsg
{
    public required int Port { get; init; }
}

internal sealed record ProjectAddMsg
{
    public required string Path { get; init; }
    /// Optional display name; defaults to the folder's basename.
    public string? Name { get; init; }
}

internal sealed record ProjectRef
{
    public required Guid Id { get; init; }
}

/// Create a tab under a project: a named, colored agent session, optionally in
/// its own git worktree.
internal sealed record ProjectTabNewMsg
{
    public required Guid ProjectId { get; init; }
    /// User-typed tab name. Also becomes the branch (slugified) and the cc
    /// session's display name. Empty falls back to the project's name.
    public string? Name { get; init; }
    /// "claude" (default) | "codex" | "shell" | "browser".
    public string? Agent { get; init; }
    /// Cut a git worktree for this tab. Null/false → open in the repo itself.
    public bool? Worktree { get; init; }
    /// Claude model alias for the new tab ("fable"/"opus"/"sonnet"/"haiku").
    /// Null/absent/"" → account default. Only meaningful when Agent is
    /// "claude"; the host clamps unknown values to default.
    public string? Model { get; init; }
    /// Normalized URL for a browser tab. Only set when Agent is "browser": the
    /// tab's root leaf renders a webview pointed here instead of a terminal, and
    /// no PTY/worktree is created. Name is optional in that case.
    public string? Url { get; init; }
}

internal sealed record SessionRenameMsg
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
}

/// Pair two tabs for cross-session messaging (sidebar row context menu). The
/// host sets the symmetric PairedWithId on both and introduces each agent to
/// the other; "session.unpair" (a plain SessionRef) breaks it from either side.
internal sealed record SessionPairMsg
{
    public required Guid Id { get; init; }
    public required Guid PartnerId { get; init; }
}

internal sealed record ResumeDecisionMsg
{
    /// Deliberately optional: a malformed/absent accept must degrade to
    /// "declined" (spawns release as plain shells), never to parked-forever.
    public bool? Accept { get; init; }
}

internal sealed record UrlOpenMsg
{
    public required string Url { get; init; }
}

internal sealed record PrefsSetMsg
{
    public int? FontSize { get; init; }
    /// Whether the Inspector rail is showing. Nullable so the page can send a
    /// font-size-only update without also asserting a rail state.
    public bool? InspectorOpen { get; init; }
    /// Wide layout mode: both side rails widen, terminal narrows. Nullable so
    /// the page can update one pref without asserting the others.
    public bool? WideLayout { get; init; }
    /// Local panel "Perch only" filter — count/show only servers Perch started.
    /// Nullable so the page can update one pref without asserting the others.
    public bool? LocalPerchOnly { get; init; }
    /// Bot faces in colour (the bird and its circle take the bot's tag hue)
    /// rather than plain ink. Off by default.
    public bool? TeamFacesColor { get; init; }
}

internal sealed record SettingsSaveMsg
{
    public string? DefaultShell { get; init; }
    public string? DefaultCwd { get; init; }
    public int? FontSize { get; init; }
    public bool? ResumeAgentsOnLaunch { get; init; }
    /// Team bot faces in colour (see Settings.TeamFacesColor).
    public bool? TeamFacesColor { get; init; }
    /// Where a new tab lands in its project: "top" or "bottom".
    public string? NewTabPosition { get; init; }
    /// Parent folders scanned one level deep for repos to offer as projects.
    /// Null = key absent (leave as-is); an empty array explicitly clears them.
    public List<string>? ProjectScanRoots { get; init; }
    /// Where project tabs' worktrees are created. "" = the built-in default.
    public string? WorktreeRoot { get; init; }
    /// Default list of things seeded into a new worktree (a project can override).
    public List<string>? WorktreeSeedPaths { get; init; }
}

/// Edit a registered project: rename it, hide/show it in the sidebar, or
/// override what gets seeded into its worktrees. Every field optional — only
/// the keys present are applied.
internal sealed record ProjectUpdateMsg
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    /// Empty list = "inherit the global export file" (not "seed nothing"): a
    /// project that wants nothing seeded is vanishingly rare next to one where
    /// the user just cleared the box.
    public List<string>? SeedPaths { get; init; }
    /// Fold the project into (or out of) the sidebar's "Hidden" drawer. The
    /// registration itself is untouched — see Project.Hidden.
    public bool? Hidden { get; init; }
}

/// Close a session. `removeWorktree` additionally reclaims its worktree folder
/// — which makes the close PERMANENT (it can't go to "Recently closed" and be
/// restored into a directory that no longer exists). The branch always survives.
internal sealed record SessionCloseMsg
{
    public required Guid Id { get; init; }
    public bool? RemoveWorktree { get; init; }
}

/// Inspector rail: fetch one conversation image's bytes on demand. The journal
/// rows only carry image IDs (see TranscriptReader) — the page asks for pixels
/// when a thumbnail scrolls in ("thumb") or the lightbox opens ("full").
internal sealed record InspectorImageMsg
{
    public required Guid PaneId { get; init; }
    public required string ImageId { get; init; }
    /// "thumb" (default) or "full".
    public string? Variant { get; init; }
}

/// Deserialization boundary for page + control-pipe messages. Web defaults
/// (camelCase, case-insensitive) plus two leniencies the control pipe needs:
/// numbers and bools may arrive as strings, because `perch test` ships every
/// flag as a string. This is what lets the control path share the page
/// handlers without per-verb payload rewriting.
// ---- team ------------------------------------------------------------------
// The team room and the new-bot dialog. Mirrors the `team.*` members of the
// OutMessage union; the replies (`team.data`, `team.brief.progress`,
// `team.brief.result`, `team.reference.picked`) are built by TeamController.

/// The room asking for entries newer than `sinceSeq` (absent = everything the
/// host is willing to send, newest 500).
internal sealed record TeamRequestMsg
{
    public required Guid ProjectId { get; init; }
    public long? SinceSeq { get; init; }
}

/// The owner's post. `to` is polymorphic on the wire — an array of nicknames,
/// the string "everyone", or null for a post that names nobody (which goes to
/// everyone) — so it arrives as a raw JsonElement and TeamController
/// interprets it.
internal sealed record TeamPostMsg
{
    public required Guid ProjectId { get; init; }
    public required string Text { get; init; }
    public JsonElement? To { get; init; }
    /// Page-generated id echoed on the ledger entry so the optimistic row can
    /// be reconciled.
    public required string ClientId { get; init; }
    /// A picture pasted into the composer (absolute path the host saved it
    /// under, from `team.paste`). Travels on the row and is named in the line
    /// typed to the bots, which Read the file when they need to see it.
    public string? Image { get; init; }
}

/// The owner pasted a picture into the room's composer: read the clipboard on
/// the host, save it under the team's local folder, answer with the path
/// (`team.paste.data`). The page never sees the bytes.
internal sealed record TeamPasteMsg
{
    public required Guid ProjectId { get; init; }
}

/// A new position, sent inline with the bot that first fills it. `brief` is
/// the text the owner accepted (generated or hand-written).
internal sealed record TeamPositionSpec
{
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public string? ReferencePath { get; init; }
    public string? Model { get; init; }
    public string? Brief { get; init; }
}

/// Create a bot: a nickname plus either an existing position (`positionSlug`)
/// or a new one (`position`). The host mints the session name, opens the tab
/// (in its own worktree unless told otherwise) and writes the bot's files.
internal sealed record TeamBotCreateMsg
{
    public required Guid ProjectId { get; init; }
    public required string Nickname { get; init; }
    public bool? Worktree { get; init; }
    public string? PositionSlug { get; init; }
    public TeamPositionSpec? Position { get; init; }
}

/// Start a brief-generation job (a headless `claude -p` over the reference
/// folder). `jobId` is page-generated so a stale reply can be dropped.
internal sealed record TeamBriefGenerateMsg
{
    public required string JobId { get; init; }
    public required Guid ProjectId { get; init; }
    public required string PositionName { get; init; }
    public required string Purpose { get; init; }
    public string? ReferencePath { get; init; }
    public string? Model { get; init; }
}

internal sealed record TeamBriefCancelMsg
{
    public required string JobId { get; init; }
}

/// Edit a position in place. Only the keys present are applied.
internal sealed record TeamPositionUpdateMsg
{
    public required Guid ProjectId { get; init; }
    public required string Slug { get; init; }
    public string? Brief { get; init; }
    public string? Purpose { get; init; }
    public string? Name { get; init; }
}

/// Remove a bot from the team. `closeTab` also closes its session (archived to
/// Recently closed like any close); `removeWorktree` additionally reclaims the
/// folder, which makes that close permanent.
internal sealed record TeamBotRemoveMsg
{
    public required Guid ProjectId { get; init; }
    public required string BotId { get; init; }
    public bool? CloseTab { get; init; }
    public bool? RemoveWorktree { get; init; }
}

/// Start a bot that has no tab on this machine — one that was created
/// elsewhere and arrived with a pull, or whose tab was closed here.
internal sealed record TeamBotStartMsg
{
    public required Guid ProjectId { get; init; }
    public required string BotId { get; init; }
}

/// The owner answering a bot's start-up question from the room's card.
/// `answer` is "trust" (Yes, I trust this folder) or "exit" (the dialog's
/// default, No, exit).
internal sealed record TeamBotAnswerMsg
{
    public required Guid ProjectId { get; init; }
    public required string BotId { get; init; }
    public required string Answer { get; init; }
}

/// Make a bot the team's one lead (replacing the current one).
internal sealed record TeamLeadSetMsg
{
    public required Guid ProjectId { get; init; }
    public required string BotId { get; init; }
}

/// The owner opens a new task from the room (a card on the board).
internal sealed record TeamTaskSetMsg
{
    public required Guid ProjectId { get; init; }
    public required string Title { get; init; }
}

/// The owner renames an open task.
internal sealed record TeamTaskRenameMsg
{
    public required Guid ProjectId { get; init; }
    public required string TaskId { get; init; }
    public required string Title { get; init; }
}

/// The owner confirms a task is done: the bots whose work was all on it
/// wrap up and reset.
internal sealed record TeamTaskConfirmMsg
{
    public required Guid ProjectId { get; init; }
    public required string TaskId { get; init; }
}

/// "Send again" on a post a bot never took: the same line, typed into that
/// bot again, with no second post in the room.
internal sealed record TeamDeliverRetryMsg
{
    public required Guid ProjectId { get; init; }
    public required long Seq { get; init; }
    public required string BotId { get; init; }
}

/// The owner takes a card off the board by hand, whatever state it is in:
/// nothing is asked of the bots, nothing is reset. The escape hatch for a
/// card that is finished in all but name, or that nobody will finish.
internal sealed record TeamTaskCloseMsg
{
    public required Guid ProjectId { get; init; }
    public required string TaskId { get; init; }
}

/// The owner says a task is not done yet (after the lead asked): back to
/// open, with a note the lead gets.
internal sealed record TeamTaskRejectMsg
{
    public required Guid ProjectId { get; init; }
    public required string TaskId { get; init; }
    public string? Note { get; init; }
}

/// The owner answering a bot's permission card. `decision` is "allow" or
/// "deny"; the host writes it where the waiting hook polls.
internal sealed record TeamPermAnswerMsg
{
    public required Guid ProjectId { get; init; }
    public required string Id { get; init; }
    public required string Decision { get; init; }
}

/// The owner answering a bot's ask card; the answer is delivered to the bot
/// as a post.
internal sealed record TeamAskAnswerMsg
{
    public required Guid ProjectId { get; init; }
    public required string Id { get; init; }
    public required string Answer { get; init; }
}

/// The room asking for a picture's bytes (a screenshot a bot attached or
/// mentioned). Answered with `team.image.data`.
internal sealed record TeamImageMsg
{
    public required Guid ProjectId { get; init; }
    public required string Path { get; init; }
}

/// The room opening an artefact — a bot's long piece of work — by its id.
/// Answered with `team.artefact.data`. The id names a file Perch itself
/// wrote, so no path from a bot is ever opened on the page's say-so.
internal sealed record TeamArtefactOpenMsg
{
    public required Guid ProjectId { get; init; }
    public required string Id { get; init; }
}

/// The room asking what artefacts it still has, for the menu above the
/// artefact panel. Answered with `team.artefact.index`.
internal sealed record TeamArtefactListMsg
{
    public required Guid ProjectId { get; init; }
}

/// "Open in a tab" on the artefact the room is showing. The page sends the
/// finished document because it is the side that owns the markdown renderer
/// and the theme; the host only writes it and opens a browser tab on it.
/// `Html` is page-authored (never a bot's raw text), and it is written to a
/// file under Perch's own data dir, so nothing here lets a bot choose a path.
internal sealed record TeamArtefactTabMsg
{
    public required Guid ProjectId { get; init; }
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Html { get; init; }
    /// "tab" (default, a browser tab inside Perch) or "window" (the default
    /// browser's own window, so the document can sit on another screen).
    public string? Where { get; init; }
}

/// The owner reacting to a room row with an emoji.
internal sealed record TeamReactMsg
{
    public required Guid ProjectId { get; init; }
    public required long Seq { get; init; }
    public required string Emoji { get; init; }
}

/// The dialog's "Browse…" for a reference folder. Answered with
/// `team.reference.picked { requestId, path | null }`.
internal sealed record TeamReferenceBrowseMsg
{
    public required string RequestId { get; init; }
    public required Guid ProjectId { get; init; }
}

/// The room opened or closed for a project — a cadence hint: while open, the
/// host pushes new entries as they land instead of waiting to be polled.
internal sealed record TeamRoomMsg
{
    public required Guid ProjectId { get; init; }
    public required bool Open { get; init; }
}

internal static class PageJson
{
    public static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        o.Converters.Add(new LenientBoolConverter());
        return o;
    }

    public static T Deserialize<T>(JsonElement root) =>
        root.Deserialize<T>(Options)
        ?? throw new JsonException($"null payload for {typeof(T).Name}");
}

/// Accepts JSON true/false AND the strings "true"/"false" (case-insensitive).
internal sealed class LenientBoolConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.String when bool.TryParse(reader.GetString(), out var b) => b,
            _ => throw new JsonException($"cannot read {reader.TokenType} as bool"),
        };

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) =>
        writer.WriteBooleanValue(value);
}
