using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Perch;

/// Project chat presets: markdown files in the project's repo at
/// `.perch/presets/<slug>.md`. A chat switches any of them on; each one that
/// is on goes into the coordinator's and every new thread's prompt as its own
/// "## Mode: <name>" section, read fresh each time a prompt is written, so an
/// edit to the file (or a toggle) is in force from the next turn.
///
/// Name is the file's first `# ` heading (the slug when it has none); the
/// summary is its first line of prose, like a team skill's.
internal static class Presets
{
    /// One preset as the page lists it.
    internal sealed record Preset(string Slug, string Name, string Summary);

    /// One switched-on preset as a prompt carries it.
    internal sealed record Loaded(string Slug, string Name, string Body, string RelPath);

    /// A preset's body is cut here in a prompt (characters, ~24 KB).
    internal const int MaxBodyChars = 24 * 1024;

    internal static string Dir(string repo) => Path.Combine(repo, ".perch", "presets");

    internal static string RelPath(string slug) => $".perch/presets/{slug}.md";

    private static readonly Regex SlugRx = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant);

    /// A slug that names a file in the presets folder and nothing outside it.
    internal static bool IsSlug(string? s) => s is { Length: > 0 and <= 100 } && SlugRx.IsMatch(s) && !s.Contains("..");

    /// The slugs a chat keeps: valid, trimmed, each once, in the order given.
    internal static string[] Clean(IEnumerable<string>? slugs) =>
        (slugs ?? Array.Empty<string>()).Select(s => (s ?? "").Trim()).Where(IsSlug)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    // The list ships with every state push, so it is cached per folder and
    // re-parsed only when a file's size or time changes.
    private sealed record Cached(string Key, IReadOnlyList<Preset> List);
    private static readonly Dictionary<string, Cached> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    /// The presets in a repo, by slug. Empty when the folder isn't there.
    public static IReadOnlyList<Preset> List(string repo)
    {
        if (string.IsNullOrEmpty(repo)) return Array.Empty<Preset>();
        try
        {
            var dir = new DirectoryInfo(Dir(repo));
            if (!dir.Exists) return Array.Empty<Preset>();
            var files = dir.EnumerateFiles("*.md")
                .Where(f => IsSlug(Path.GetFileNameWithoutExtension(f.Name)))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var key = string.Join("|", files.Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
            lock (_gate)
                if (_cache.TryGetValue(dir.FullName, out var c) && c.Key == key) return c.List;
            var list = new List<Preset>();
            foreach (var f in files)
            {
                var slug = Path.GetFileNameWithoutExtension(f.Name);
                string text;
                try { text = File.ReadAllText(f.FullName); }
                catch (Exception ex) { Log.Error("Presets.read", ex); continue; }
                var (name, summary) = Describe(text, slug);
                list.Add(new Preset(slug, name, summary));
            }
            lock (_gate) _cache[dir.FullName] = new Cached(key, list);
            return list;
        }
        catch (Exception ex) { Log.Error("Presets.List", ex); return Array.Empty<Preset>(); }
    }

    /// A preset's name (first `# ` heading) and summary (first line of prose,
    /// at most 160 characters). Pure.
    internal static (string Name, string Summary) Describe(string text, string slug)
    {
        string? name = null, summary = null;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var l = raw.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith("# ")) { name ??= l[2..].Trim(); continue; }
            if (l.StartsWith('#')) continue;                        // a lower heading
            if (l.StartsWith('_') && l.EndsWith('_')) continue;     // a provenance line
            summary ??= TeamRender.OneLine(l, 160);
            if (name != null) break;
        }
        return (string.IsNullOrEmpty(name) ? slug : name, summary ?? "");
    }

    /// The switched-on presets' full text, read now. A file that isn't there
    /// is skipped (and logged); a long one is cut at MaxBodyChars.
    public static IReadOnlyList<Loaded> Load(string? repo, IEnumerable<string>? slugs)
    {
        var list = new List<Loaded>();
        if (string.IsNullOrEmpty(repo)) return list;
        foreach (var slug in Clean(slugs))
        {
            var path = Path.Combine(Dir(repo), slug + ".md");
            string text;
            try
            {
                if (!File.Exists(path)) { Log.Info("Presets.missing", path); continue; }
                text = File.ReadAllText(path);
            }
            catch (Exception ex) { Log.Error("Presets.load", ex); continue; }
            var (name, _) = Describe(text, slug);
            list.Add(new Loaded(slug, name, Body(text), RelPath(slug)));
        }
        return list;
    }

    /// The file without its title heading (the section has its own), cut to
    /// MaxBodyChars with a note saying so. Pure.
    internal static string Body(string text)
    {
        var body = text.Replace("\r", "").Trim();
        if (body.StartsWith("# "))
        {
            var nl = body.IndexOf('\n');
            body = nl < 0 ? "" : body[(nl + 1)..].Trim();
        }
        if (body.Length > MaxBodyChars)
            body = body[..MaxBodyChars].TrimEnd() + "\n\n[This preset is longer than 24 KB and was cut here. Read the file for the rest.]";
        return body;
    }

    /// The prompt sections for the switched-on presets, one each. `reread`
    /// says whether the prompt is rewritten every turn (the coordinator's) or
    /// once, when the thread started.
    internal static string Sections(IReadOnlyList<Loaded>? presets, bool reread)
    {
        if (presets is not { Count: > 0 }) return "";
        var sb = new StringBuilder();
        foreach (var p in presets)
        {
            sb.Append("\n## Mode: ").Append(p.Name).Append('\n');
            if (p.Body.Length > 0) sb.Append(p.Body).Append("\n\n");
            sb.Append("(Preset file: `").Append(p.RelPath).Append(reread
                ? "` - it is re-read every turn.)\n"
                : "` - as it was when this thread started.)\n");
        }
        return sb.ToString();
    }
}
