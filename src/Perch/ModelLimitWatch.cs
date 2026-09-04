using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Perch;

/// The LOCAL half of "which model is out of headroom".
///
/// <see cref="UsageService"/> is the account-wide answer, and in practice it
/// has none: the OAuth usage endpoint has returned 429 on every call since we
/// added it (see its own header, and `UsageService: 429` in errors.log), so
/// <c>CurrentLimits()</c> is permanently empty. Everything downstream of it —
/// the picker's disabled models, and the team's "fable is at its limit, Ada
/// switched to opus" move — therefore never fired at all.
///
/// The signal we DO have is the one Claude Code writes into the pane's own
/// transcript the moment a request is refused: a synthetic assistant row
/// carrying <c>"error":"rate_limit"</c> and the text "You've reached your
/// Fable limit. Run /usage-credits to continue or switch models with /model."
/// That row is local, free to read, and names the model that was refused.
///
/// So this watch tails each agent pane's transcript (by byte offset, like
/// <see cref="TranscriptReader"/>) and remembers, per alias, when it was last
/// refused. The refusal carries NO reset time, so a limit is held for
/// <see cref="Hold"/> and then simply lapses: the bot moves back to its
/// preferred model, and if the account is still out of headroom the next
/// refusal re-arms the hold within seconds. Guessing wrong in that direction
/// costs one failed turn; guessing wrong the other way pins a bot on the
/// fallback model for hours.
internal sealed class ModelLimitWatch
{
    /// How long one observed refusal keeps a model marked at-limit. Claude's
    /// refusal row carries no reset time, so this is the whole expiry story.
    internal static readonly TimeSpan Hold = TimeSpan.FromMinutes(60);

    /// Cap on how much of a transcript one scan will read. A pane that has
    /// been appending while we weren't looking (the app was closed, the
    /// inspector never opened it) can have megabytes pending; the refusal we
    /// care about is always at the END, and this keeps the UI-thread read
    /// bounded regardless.
    private const int MaxScanBytes = 512 * 1024;

    /// The clock, swappable so a test can step past the hold.
    internal Func<DateTimeOffset> Now = () => DateTimeOffset.UtcNow;

    private readonly Dictionary<string, DateTimeOffset> _refusedAt =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _offsets =
        new(StringComparer.OrdinalIgnoreCase);

    /// Read whatever has been appended to `path` since the last scan and note
    /// any rate-limit refusal in it. `fallbackAlias` is the model the pane is
    /// known to be on — used only when the refusal's own text doesn't name a
    /// model we recognize. Returns true when a refusal was recorded.
    public bool Scan(string? path, string? fallbackAlias)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string text;
        long length;
        try
        {
            using var fs = new FileStream(path!, FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite | FileShare.Delete);
            length = fs.Length;
            var from = _offsets.TryGetValue(path!, out var prev) ? prev : 0;
            // Truncated / rotated (a new session wrote over the path): start over.
            if (from > length) from = 0;
            if (from > 0 && length - from > MaxScanBytes) from = length - MaxScanBytes;
            else if (from == 0 && length > MaxScanBytes) from = length - MaxScanBytes;
            if (length <= from) { _offsets[path!] = length; return false; }
            fs.Seek(from, SeekOrigin.Begin);
            var buf = new byte[length - from];
            var n = fs.Read(buf, 0, buf.Length);
            text = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            _offsets[path!] = from + n;
        }
        catch { return false; }

        return Ingest(text, fallbackAlias);
    }

    /// Note every refusal in a chunk of transcript text. Split out from the
    /// file read so the parsing rules are unit-testable without a transcript
    /// on disk.
    internal bool Ingest(string text, string? fallbackAlias)
    {
        var found = false;
        foreach (var line in text.Split('\n'))
        {
            // Cheap gate first: only a refusal row carries this marker, and it
            // is the same string in every Claude Code build we've seen.
            if (line.IndexOf("\"error\":\"rate_limit\"", StringComparison.Ordinal) < 0) continue;
            var alias = AliasFromRefusal(line) ?? Normalize(fallbackAlias);
            if (alias == null) continue;
            _refusedAt[alias] = Now();
            found = true;
            Log.Info("ModelLimit", $"{alias} refused (rate_limit) — held for {Hold.TotalMinutes:0}m");
        }
        return found;
    }

    /// The models still inside their hold, shaped like the account-wide list so
    /// the two merge without a special case downstream. No reset time: the
    /// refusal doesn't carry one, and inventing one would put a wrong "until
    /// 14:05" in the room.
    public IReadOnlyList<ModelUsageLimit> Current()
    {
        var now = Now();
        // Lapsed entries are dropped here rather than on a timer — Current() is
        // the only reader, so this IS the expiry.
        foreach (var stale in _refusedAt.Where(kv => now - kv.Value >= Hold).Select(kv => kv.Key).ToList())
        {
            _refusedAt.Remove(stale);
            Log.Info("ModelLimit", $"{stale} hold lapsed — treating it as free again");
        }
        return _refusedAt.Keys.Select(a => new ModelUsageLimit(a, true, null)).ToArray();
    }

    /// The alias named by a refusal row's own text ("You've reached your Fable
    /// limit…"), or null when it names none we know. Matched against the line
    /// as a whole: the row's model field reads "&lt;synthetic&gt;" on a refusal,
    /// so the prose is the only place the real model appears.
    internal static string? AliasFromRefusal(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(
            line, @"reached your ([^""]{1,40}?) limit",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var said = m.Groups[1].Value.ToLowerInvariant();
        return TeamController.ModelFallback.FirstOrDefault(a => said.Contains(a));
    }

    private static string? Normalize(string? alias)
    {
        var a = alias?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(a) ? null : TeamController.ModelFallback.Contains(a) ? a : null;
    }

    /// The account-wide list plus the locally-observed one, one entry per
    /// alias. The account-wide entry wins on a tie because it's the only one
    /// that can carry a reset time.
    public static IReadOnlyList<ModelUsageLimit> Merge(
        IReadOnlyList<ModelUsageLimit>? account, IReadOnlyList<ModelUsageLimit>? local)
    {
        var byAlias = new Dictionary<string, ModelUsageLimit>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in local ?? Array.Empty<ModelUsageLimit>()) byAlias[l.Alias] = l;
        foreach (var a in account ?? Array.Empty<ModelUsageLimit>()) byAlias[a.Alias] = a;
        return byAlias.Values.ToArray();
    }
}
