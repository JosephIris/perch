using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// The outcome of one `claude -p` run. `Text` is the model's answer (the
/// `result` field); `RawJson` is kept for the log line when parsing fails.
internal sealed record HeadlessResult(
    bool Ok, string Text, string? Error, double CostUsd, long DurationMs, string RawJson,
    /// The `structured_output` object (as JSON text) when the run used
    /// --json-schema; null otherwise. Some builds put the object here and
    /// leave `result` as prose, so a router must look here first.
    string? Structured = null);

/// Runs Claude Code headless (`claude -p`) from the host, for jobs that are
/// not a pane: writing a position's brief after reading a repository, or
/// deciding who an unaddressed room post is for.
///
/// Three things make this a class rather than a one-liner over ProcRunner:
///
///   - Resolution. The host prefixes its own PATH with the app's tools dir
///     (App.xaml.cs), where `claude.cmd` is OUR shim. Resolve the real binary
///     with the same skip-every-perch-dir rule the shim uses, or the host
///     would call its own wrapper.
///   - Environment. A dev build launched from inside a Perch pane inherits
///     PERCH_PIPE / PERCH_PANE_ID; the shim treats those as "I'm in a pane"
///     and injects hooks that would report this job as pane activity. Strip
///     them.
///   - Quoting. The real `claude` is usually an npm `.cmd`, which cmd.exe
///     parses, so the prompt goes on STDIN and only short flag values travel
///     on the command line.
///
/// Every run goes through ProcRunner so the spawn budget counts it, under a
/// site tag ("claude.headless.brief") so a runaway shows up by name.
internal static class ClaudeHeadless
{
    /// Where the real `claude` lives, or null when it isn't installed.
    public static string? ResolveClaude() => ResolveOverride != null ? ResolveOverride() : PerchCli.BinResolver.FindOnPathSkippingSelf("claude");

    /// Tests point this at a fake binary (or at nothing) instead of editing
    /// the process PATH, which every other test's git shell-out shares.
    internal static Func<string?>? ResolveOverride;

    /// Build the (file, arguments) ProcRunner needs. A `.cmd`/`.bat` target is
    /// run through cmd.exe explicitly — Process.Start with UseShellExecute=false
    /// won't launch a batch file by itself. Arguments are quoted only when they
    /// need it; every value we pass is a flag, a model alias, a path, or a JSON
    /// schema (quoted, with inner quotes escaped for cmd's `"` rules).
    internal static (string File, string Arguments) Command(string claudePath, IEnumerable<string> claudeArgs)
    {
        var args = new StringBuilder();
        foreach (var a in claudeArgs)
        {
            if (args.Length > 0) args.Append(' ');
            args.Append(Quote(a));
        }
        var ext = Path.GetExtension(claudePath);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // /d: no AutoRun; /s: strip the outer quotes exactly once; /c: run and exit.
            return (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    $"/d /s /c \"\"{claudePath}\" {args}\"");
        }
        return (claudePath, args.ToString());
    }

    /// Run `claude -p` with `prompt` on stdin in `cwd`. `extraArgs` are added
    /// verbatim after the fixed flags (tool allow-lists, budget caps, schema).
    /// `timeoutMs` bounds the whole run; a timeout or cancellation kills the
    /// process tree and returns Ok=false.
    public static async Task<HeadlessResult> RunAsync(
        string prompt, string cwd, string model, string site,
        IEnumerable<string>? extraArgs = null, int timeoutMs = 300_000, CancellationToken ct = default)
    {
        var claude = ResolveClaude();
        if (claude == null)
            return new HeadlessResult(false, "", "Claude Code isn't installed (no `claude` on PATH).", 0, 0, "");

        var args = new List<string> { "-p", "--output-format", "json", "--no-session-persistence" };
        if (!string.IsNullOrWhiteSpace(model)) { args.Add("--model"); args.Add(model.Trim()); }
        if (extraArgs != null) args.AddRange(extraArgs);

        var (file, arguments) = Command(claude, args);
        var env = new Dictionary<string, string?>
        {
            ["PERCH_PIPE"] = null,
            ["PERCH_PANE_ID"] = null,
        };
        var started = DateTimeOffset.UtcNow;
        var (code, stdout, stderr) = await ProcRunner.RunAsync(
            file, arguments, site,
            workingDir: Directory.Exists(cwd) ? cwd : null,
            timeoutMs: timeoutMs,
            stdoutEncoding: new UTF8Encoding(false),
            ct: ct,
            stdinText: prompt,
            env: env);
        var elapsed = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        var result = Parse(stdout, stderr, code);
        return result.DurationMs > 0 ? result : result with { DurationMs = elapsed };
    }

    /// Turn `claude -p --output-format json` output into a result. Accepts a
    /// single `{type:"result",…}` object or an array of events (the last
    /// `result` wins); anything else is a failure carrying stderr's tail.
    internal static HeadlessResult Parse(string stdout, string stderr, int code)
    {
        var text = (stdout ?? "").Trim();
        var tail = Tail(stderr, 600);
        if (text.Length == 0)
            return new HeadlessResult(false, "", code == 0 ? "claude returned nothing" : Describe(code, tail), 0, 0, "");

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            JsonElement? resultObj = null;
            if (root.ValueKind == JsonValueKind.Object) resultObj = root;
            else if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in root.EnumerateArray())
                    if (el.ValueKind == JsonValueKind.Object && Str(el, "type") == "result") resultObj = el;
            }
            if (resultObj is not { } r)
                return new HeadlessResult(false, "", "claude's answer had no result object", 0, 0, text);

            var isError = r.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            var subtype = Str(r, "subtype") ?? "";
            var answer = Str(r, "result") ?? "";
            var cost = Num(r, "total_cost_usd");
            var duration = (long)Num(r, "duration_ms");
            string? structured = null;
            if (r.TryGetProperty("structured_output", out var so) && so.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                structured = so.GetRawText();
            if (isError || (subtype.Length > 0 && subtype != "success"))
            {
                var why = answer.Length > 0 ? answer : (subtype.Length > 0 ? subtype : "claude reported an error");
                if (subtype.StartsWith("error_max_budget", StringComparison.Ordinal)) why = "stopped at the cost cap";
                return new HeadlessResult(false, answer, why, cost, duration, text, structured);
            }
            if (code != 0 && answer.Length == 0 && structured == null)
                return new HeadlessResult(false, "", Describe(code, tail), cost, duration, text);
            return new HeadlessResult(true, answer, null, cost, duration, text, structured);
        }
        catch (JsonException)
        {
            return new HeadlessResult(false, "", code == 0 ? "claude's answer wasn't JSON" : Describe(code, tail), 0, 0, text);
        }
    }

    private static string Describe(int code, string stderrTail)
    {
        if (stderrTail.Contains("timed out", StringComparison.OrdinalIgnoreCase)) return "timed out";
        return stderrTail.Length > 0 ? stderrTail : $"claude exited with code {code}";
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : 0;

    private static string Tail(string? s, int max)
    {
        var t = (s ?? "").Trim();
        return t.Length <= max ? t : t[^max..];
    }

    /// cmd-safe quoting: wrap when the value has whitespace or quotes, and
    /// escape inner quotes as \" (what both cmd's outer parse and Claude's
    /// Node argv parser agree on for a JSON schema value).
    internal static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }
}
