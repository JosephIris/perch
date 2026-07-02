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
}

internal sealed record SettingsSaveMsg
{
    public string? DefaultShell { get; init; }
    public string? DefaultCwd { get; init; }
    public int? FontSize { get; init; }
    public bool? ResumeAgentsOnLaunch { get; init; }
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
