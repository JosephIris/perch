using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Perch;

/// One model a codex pane can be set to.
internal readonly record struct CodexModel(string Slug, string Label);

/// The model list the codex picker offers.
///
/// Codex keeps its own catalogue at `$CODEX_HOME/models_cache.json`, refreshed
/// from the service — so Perch reads that rather than hardcoding a list that
/// would rot the first time OpenAI ships a model. Entries marked
/// `visibility: "hide"` are internal (an auto-review model, a reserve model)
/// and never offered; the rest are ordered by codex's own `priority`, so the
/// menu matches the order codex itself shows.
///
/// Everything here is best-effort: no cache file, or an unreadable one, simply
/// means the picker has nothing to offer for a codex pane, which is exactly the
/// behaviour before this existed.
internal static class CodexModels
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);
    private static IReadOnlyList<CodexModel> _cached = Array.Empty<CodexModel>();
    private static DateTime _readAtUtc = DateTime.MinValue;
    private static DateTime _fileAtUtc = DateTime.MinValue;

    /// The offerable models, newest catalogue wins. Re-read at most every
    /// {@link Ttl}, and only when the file actually changed.
    public static IReadOnlyList<CodexModel> List()
    {
        try
        {
            var path = Path.Combine(CodexTranscripts.Home(), "models_cache.json");
            if (!File.Exists(path)) return _cached;
            var stamp = File.GetLastWriteTimeUtc(path);
            if (DateTime.UtcNow - _readAtUtc < Ttl && stamp == _fileAtUtc) return _cached;
            _readAtUtc = DateTime.UtcNow;
            _fileAtUtc = stamp;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("models", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return _cached;

            var list = new List<(int Priority, CodexModel Model)>();
            foreach (var m in arr.EnumerateArray())
            {
                var slug = Str(m, "slug");
                if (slug.Length == 0) continue;
                if (Str(m, "visibility") == "hide") continue;
                var label = Str(m, "display_name");
                var priority = m.TryGetProperty("priority", out var p)
                               && p.ValueKind == JsonValueKind.Number
                               && p.TryGetInt32(out var n) ? n : int.MaxValue;
                list.Add((priority, new CodexModel(slug, label.Length > 0 ? label : slug)));
            }
            _cached = list.OrderBy(x => x.Priority).Select(x => x.Model).ToArray();
        }
        catch (Exception ex) { Log.Info("CodexModels", $"catalogue unreadable: {ex.Message}"); }
        return _cached;
    }

    /// Whether a slug is one codex would accept. The picker's value ends up on
    /// a command line (`codex -m <slug>`), so it is checked against the
    /// catalogue rather than a character class.
    public static bool IsKnown(string? slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && List().Any(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase));

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
