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
    /// "claude" (default) | "codex" | "shell".
    public string? Agent { get; init; }
    /// Cut a git worktree for this tab. Null/false → open in the repo itself.
    public bool? Worktree { get; init; }
    /// Claude model alias for the new tab ("fable"/"opus"/"sonnet"/"haiku").
    /// Null/absent/"" → account default. Only meaningful when Agent is
    /// "claude"; the host clamps unknown values to default.
    public string? Model { get; init; }
}

internal sealed record SessionRenameMsg
{
    public required Guid Id { get; init; }
    public required string Title { get; init; }
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
}

internal sealed record SettingsSaveMsg
{
    public string? DefaultShell { get; init; }
    public string? DefaultCwd { get; init; }
    public int? FontSize { get; init; }
    public bool? ResumeAgentsOnLaunch { get; init; }
    /// Parent folders scanned one level deep for repos to offer as projects.
    /// Null = key absent (leave as-is); an empty array explicitly clears them.
    public List<string>? ProjectScanRoots { get; init; }
    /// Where project tabs' worktrees are created. "" = the built-in default.
    public string? WorktreeRoot { get; init; }
    /// Default list of things seeded into a new worktree (a project can override).
    public List<string>? WorktreeSeedPaths { get; init; }
}

/// Edit a registered project: rename it, or override what gets seeded into its
/// worktrees. Every field optional — only the keys present are applied.
internal sealed record ProjectUpdateMsg
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    /// Empty list = "inherit the global seed list" (not "seed nothing"): a
    /// project that wants nothing seeded is vanishingly rare next to one where
    /// the user just cleared the box.
    public List<string>? SeedPaths { get; init; }
}

/// Close a session. `removeWorktree` additionally reclaims its worktree folder
/// — which makes the close PERMANENT (it can't go to "Recently closed" and be
/// restored into a directory that no longer exists). The branch always survives.
internal sealed record SessionCloseMsg
{
    public required Guid Id { get; init; }
    public bool? RemoveWorktree { get; init; }
}

/// Deserialization boundary for page + control-pipe messages. Web defaults
/// (camelCase, case-insensitive) plus two leniencies the control pipe needs:
/// numbers and bools may arrive as strings, because `perch test` ships every
/// flag as a string. This is what lets the control path share the page
/// handlers without per-verb payload rewriting.
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
