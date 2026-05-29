using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CmuxWin;

internal sealed class SessionStore
{
    public ObservableCollection<Session> Sessions { get; } = new();
    public Guid? ActiveSessionId { get; set; }

    private static string StorePath => Path.Combine(
        AppPaths.DataRoot,
        "cmux-win",
        "sessions.json");

    public static SessionStore Load()
    {
        var store = new SessionStore();
        try
        {
            if (!File.Exists(StorePath)) return SeedDefault(store);

            var json = File.ReadAllText(StorePath);

            // Old-format sessions (with Panes : List<PaneState>) are incompatible with the new
            // recursive tree model. Detect and wipe — user accepts losing the list.
            if (json.Contains("\"Panes\""))
            {
                try { File.Delete(StorePath); } catch { }
                return SeedDefault(store);
            }

            var dto = JsonSerializer.Deserialize(json, SessionStoreJsonContext.Default.SessionStoreDto);
            if (dto?.Sessions is { Count: > 0 })
            {
                foreach (var s in dto.Sessions)
                {
                    // Defensive: any session without a Root (shouldn't happen) gets a fresh leaf
                    s.Root ??= new PaneNode();
                    // One-shot migration: pre-IsAutoTitle data has the
                    // flag at its default (true), but a user-renamed session
                    // shouldn't get its title overwritten by the auto-
                    // rename pipeline. Match the old regex once at load to
                    // decide who's still "auto" vs already-user-named.
                    if (!Regex.IsMatch(s.Title ?? "", @"^(main|session( \d+)?)$", RegexOptions.IgnoreCase))
                        s.IsAutoTitle = false;
                    foreach (var leaf in AllLeavesOf(s.Root))
                    {
                        if (!Regex.IsMatch(leaf.Name ?? "", @"^pane-\d+$", RegexOptions.IgnoreCase))
                            leaf.IsAutoName = false;
                    }
                    store.Sessions.Add(s);
                }
                store.ActiveSessionId = dto.ActiveSessionId;
                return store;
            }
        }
        catch { /* corrupt — fall through */ }

        return SeedDefault(store);
    }

    private static SessionStore SeedDefault(SessionStore store)
    {
        var first = new Session { Title = "main" };
        first.Root.ColorIndex = 0;
        store.Sessions.Add(first);
        store.ActiveSessionId = first.Id;
        return store;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            var dto = new SessionStoreDto
            {
                Sessions = Sessions.ToList(),
                ActiveSessionId = ActiveSessionId,
            };
            var json = JsonSerializer.Serialize(dto, SessionStoreJsonContext.Default.SessionStoreDto);
            File.WriteAllText(StorePath, json);
        }
        catch { }
    }

    public Session AddNew()
    {
        var s = new Session { Title = NextUntitled() };
        s.Root.ColorIndex = PickUnusedColor();
        Sessions.Add(s);
        return s;
    }

    /// Picks a color (0..5) not currently used by ANY leaf in ANY session.
    /// Falls back to round-robin once all six are taken. Both new-session
    /// roots and pane splits go through this so no two panes ever share a
    /// color until the palette is exhausted.
    public int PickUnusedColor()
    {
        var used = new HashSet<int>(
            Sessions.SelectMany(x => Leaves(x.Root)).Select(p => p.ColorIndex));
        for (int i = 0; i < 6; i++) if (!used.Contains(i)) return i;
        return Sessions.Sum(x => CountLeaves(x.Root)) % 6;
    }
    private static int CountLeaves(PaneNode n) =>
        n.IsLeaf ? 1 : n.Children.Sum(CountLeaves);
    private static IEnumerable<PaneNode> Leaves(PaneNode n)
    {
        if (n.IsLeaf) { yield return n; yield break; }
        foreach (var c in n.Children) foreach (var l in Leaves(c)) yield return l;
    }
    private static IEnumerable<PaneNode> AllLeavesOf(PaneNode n) => Leaves(n);

    public Session Remove(Session s)
    {
        var idx = Sessions.IndexOf(s);
        Sessions.Remove(s);

        if (Sessions.Count == 0)
        {
            var seeded = new Session { Title = "main" };
            Sessions.Add(seeded);
            ActiveSessionId = seeded.Id;
            return seeded;
        }

        var next = Sessions[Math.Max(0, Math.Min(idx, Sessions.Count - 1))];
        ActiveSessionId = next.Id;
        return next;
    }

    private string NextUntitled()
    {
        for (int n = 1; n < 999; n++)
        {
            var candidate = n == 1 ? "session" : $"session {n}";
            if (!Sessions.Any(s => string.Equals(s.Title, candidate, StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return $"session {Guid.NewGuid():N}".Substring(0, 14);
    }
}

internal sealed class SessionStoreDto
{
    public List<Session>? Sessions { get; set; }
    public Guid? ActiveSessionId { get; set; }
}

[JsonSerializable(typeof(SessionStoreDto))]
[JsonSerializable(typeof(PaneNode))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class SessionStoreJsonContext : JsonSerializerContext { }
