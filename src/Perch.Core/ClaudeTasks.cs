using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Perch;

/// A Claude session's task list — what its TaskCreate / TaskUpdate tools
/// keep. Claude Code writes one JSON file per task under
/// <c>~/.claude/tasks/&lt;session id&gt;/&lt;n&gt;.json</c> (or under
/// CLAUDE_CONFIG_DIR), each with an id, a subject, an optional activeForm
/// ("Running the tests") and a status: pending, in_progress or completed.
///
/// A project chat's Overview shows a thread's progress from it ("2/3") and
/// its open thread lists it as a checklist. Read-only, and every failure is
/// "no tasks": a list we can't read must never break the Overview.
internal static class ClaudeTasks
{
    internal sealed record Item(string Id, string Subject, string ActiveForm, string Status);

    private static readonly string[] Statuses = { "pending", "in_progress", "completed" };

    public static string Dir(string sessionId)
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var baseDir = string.IsNullOrWhiteSpace(configDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configDir;
        return Path.Combine(baseDir, "tasks", sessionId);
    }

    public static IReadOnlyList<Item> Read(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return Array.Empty<Item>();
        return ReadDir(Dir(sessionId));
    }

    /// Every task in the folder, in the order they were made (by id).
    internal static IReadOnlyList<Item> ReadDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return Array.Empty<Item>();
            var items = new List<Item>();
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    var status = Str(root, "status");
                    var subject = Str(root, "subject").Trim();
                    if (subject.Length == 0 || !Statuses.Contains(status)) continue;
                    var id = Str(root, "id");
                    if (id.Length == 0) id = Path.GetFileNameWithoutExtension(file);
                    items.Add(new Item(id, subject, Str(root, "activeForm").Trim(), status));
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            }
            return items
                .OrderBy(i => int.TryParse(i.Id, out var n) ? n : int.MaxValue)
                .ThenBy(i => i.Id, StringComparer.Ordinal)
                .ToList();
        }
        catch { return Array.Empty<Item>(); }
    }

    /// The same list with every task completed — what an emptied list means.
    public static IReadOnlyList<Item> AllDone(IReadOnlyList<Item> items) =>
        items.Select(i => i with { Status = "completed" }).ToList();

    /// Done / total, and what it is doing now: the task in progress (its
    /// activeForm when it has one), else "".
    public static (int Done, int Total, string Now) Summary(IReadOnlyList<Item> items)
    {
        var now = items.FirstOrDefault(i => i.Status == "in_progress");
        return (items.Count(i => i.Status == "completed"), items.Count,
            now == null ? "" : now.ActiveForm.Length > 0 ? now.ActiveForm : now.Subject);
    }

    private static string Str(JsonElement el, string name)
        => el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
