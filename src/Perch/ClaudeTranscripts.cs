using System;
using System.IO;
using System.Linq;

namespace Perch;

/// Where Claude Code keeps its per-project transcripts, and the probe the
/// resume flow uses to decide whether `claude --resume <id>` can actually
/// succeed. Pure path logic apart from the disk probe, so the sanitization
/// rule (which must track Claude's own) is unit-testable.
internal static class ClaudeTranscripts
{
    /// Best-effort check that Claude has a saved transcript for this session id
    /// under the given cwd, so we only `--resume` something that exists.
    /// Claude stores transcripts at
    /// <c>~/.claude/projects/&lt;sanitized-cwd&gt;/&lt;id&gt;.jsonl</c>. We try the
    /// cwd-scoped path first, then fall back to matching the file anywhere under
    /// projects (the sanitization rule can drift across Claude versions). On ANY
    /// uncertainty — projects dir missing, IO error — we return true so the
    /// check never blocks a genuine resume; it only suppresses the clearly-absent
    /// case.
    public static bool Exists(string sessionId, string cwd)
    {
        try
        {
            // Claude's config root is ~/.claude unless CLAUDE_CONFIG_DIR overrides
            // it — honor the same override so we look where Claude actually wrote.
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var baseDir = string.IsNullOrWhiteSpace(configDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configDir;
            var projects = Path.Combine(baseDir, "projects");
            if (!Directory.Exists(projects)) return true; // unknown layout — don't block
            var scoped = Path.Combine(projects, SanitizeCwd(cwd), sessionId + ".jsonl");
            if (File.Exists(scoped)) return true;
            return Directory.EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories).Any();
        }
        catch { return true; }
    }

    /// The transcript file for this session, or null if we can't find one.
    /// Same two-step lookup as Exists — cwd-scoped path first, then a search
    /// under projects/ (the sanitization rule has drifted across Claude
    /// versions before, and a rename would silently blank the Inspector).
    /// Unlike Exists, "unknown" here means null, not true: the Inspector has
    /// nothing to show without a real path, so guessing helps no one.
    public static string? Locate(string sessionId, string cwd)
    {
        try
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            var baseDir = string.IsNullOrWhiteSpace(configDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : configDir;
            var projects = Path.Combine(baseDir, "projects");
            if (!Directory.Exists(projects)) return null;
            var scoped = Path.Combine(projects, SanitizeCwd(cwd), sessionId + ".jsonl");
            if (File.Exists(scoped)) return scoped;
            return Directory
                .EnumerateFiles(projects, sessionId + ".jsonl", SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// Claude's project-dir key: path separators and the drive colon become '-'
    /// (e.g. C:\Users\josep\dev-projects\cmux-win → C--Users-josep-dev-projects-cmux-win).
    public static string SanitizeCwd(string cwd)
    {
        var sb = new System.Text.StringBuilder(cwd.Length);
        foreach (var ch in cwd) sb.Append(ch is '\\' or '/' or ':' ? '-' : ch);
        return sb.ToString();
    }
}
