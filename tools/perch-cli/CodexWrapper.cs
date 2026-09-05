using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PerchCli;

// Intercepts `codex` invocations inside a perch pane so the pane reports the
// same states a Claude Code pane does.
//
// Codex 0.153+ ships a lifecycle-hook system with the same event names and the
// same stdin payload field names as Claude Code (SessionStart, UserPromptSubmit,
// PreToolUse, PostToolUse, PermissionRequest, Stop, ...), so the mapping is the
// one we already had — see HookHandler. What differs is HOW you hand codex the
// config: there is no `--settings <file>` flag. Codex layers
// `$CODEX_HOME/<name>.config.toml` over the user's own config when you pass
// `--profile <name>`, and a profile that doesn't exist is ignored rather than
// an error. So we write `perch.config.toml` next to the user's config and pass
// `--profile perch`. Nothing of theirs is edited or replaced; the base
// config.toml still applies underneath, and a caller passing its own --profile
// wins (we then simply don't inject ours).
//
// One thing the user has to do once: codex will not run hooks it has not been
// asked about, so the first codex launch inside perch shows a "Hooks need
// review" card. Accepting it ("Trust all and continue") persists the trust and
// it never asks again. We deliberately do NOT pass
// --dangerously-bypass-hook-trust: that flag would also silently un-gate any
// hooks the user configured themselves, which is not ours to decide.
//
// Until (or unless) that trust is given, the pane still gets the "CX" badge and
// perch's output-silence watchdog still moves it between working and done — the
// behaviour we had before hooks. So the bracket below stays either way.
internal static class CodexWrapper
{
    public static int Run(string[] args)
    {
        var passthrough = new string[args.Length - 1];
        Array.Copy(args, 1, passthrough, 0, passthrough.Length);

        var realCodex = BinResolver.FindOnPathSkippingSelf("codex");
        if (realCodex == null)
        {
            Console.Error.WriteLine("perch wrap-codex: real `codex` binary not found on PATH (skipping perch's tools dir)");
            return 127;
        }

        var pipeName = ExtractPipeName(Environment.GetEnvironmentVariable("PERCH_PIPE"));
        if (pipeName != null) SendAgent(pipeName, "codex");
        try
        {
            var psi = new ProcessStartInfo(realCodex) { UseShellExecute = false };
            // Inside a perch pane: route codex's lifecycle hooks back to us.
            // Added BEFORE passthrough so an explicit `--profile x` still wins
            // (codex takes the last one; ours is only the default).
            if (pipeName != null && !HasProfileArg(passthrough) && WritePerchProfile())
            {
                psi.ArgumentList.Add("--profile");
                psi.ArgumentList.Add(ProfileName);
            }
            // Per-pane model, chosen from the pane header's picker. Same
            // per-pane temp file the claude wrapper reads (one file, one
            // meaning: "the model this pane is set to"), re-read at every
            // launch so a change made mid-session applies to the next start.
            // Added BEFORE passthrough so an explicit `-m` on the command line
            // still wins.
            if (pipeName != null && ReadModelSlug() is { Length: > 0 } model
                && !HasModelArg(passthrough))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
            }
            foreach (var a in passthrough) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch wrap-codex: failed to exec {realCodex}: {ex.Message}");
            return 1;
        }
        finally
        {
            // Best-effort clear so the badge drops when codex quits, even on a
            // crash/Ctrl-C path. The SessionEnd hook says the same thing when
            // hooks are trusted; this covers the case where they aren't.
            if (pipeName != null) SendAgent(pipeName, "");
        }
    }

    private const string ProfileName = "perch";

    private static bool HasProfileArg(string[] args)
    {
        foreach (var a in args)
            if (a == "-p" || a == "--profile" || a.StartsWith("--profile=", StringComparison.Ordinal))
                return true;
        return false;
    }

    private static bool HasModelArg(string[] args)
    {
        foreach (var a in args)
            if (a == "-m" || a == "--model" || a.StartsWith("--model=", StringComparison.Ordinal))
                return true;
        return false;
    }

    /// The model slug the host wrote for this pane, from
    /// %TEMP%\perch-claude-model-&lt;PERCH_PANE_ID&gt;.txt — the same file the claude
    /// wrapper reads, because it answers the same question for whichever agent
    /// runs in the pane. Null when unset. Validated to one safe token so a
    /// stale or corrupt file can never inject extra arguments into codex.
    private static string? ReadModelSlug()
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrEmpty(paneId)) return null;
            var path = Path.Combine(Path.GetTempPath(), $"perch-claude-model-{paneId}.txt");
            if (!File.Exists(path)) return null;
            var slug = File.ReadAllText(path).Trim();
            if (slug.Length == 0 || slug.Length > 60) return null;
            foreach (var c in slug)
                if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return null;
            return slug;
        }
        catch { return null; }
    }

    /// Codex's config home: $CODEX_HOME, else ~/.codex. Returns null when we
    /// can't work it out (then we simply don't inject the profile).
    private static string? CodexHome()
    {
        var explicitHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(explicitHome)) return explicitHome;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, ".codex");
    }

    /// Write `<codex home>/perch.config.toml`. Rewritten on every launch so an
    /// upgraded perch re-points the hooks at its new path; the content is
    /// otherwise byte-identical run to run, which is what keeps codex's
    /// one-time hook-trust prompt one-time. Returns false if we couldn't write
    /// it — the caller then leaves codex entirely alone.
    private static bool WritePerchProfile()
    {
        try
        {
            var home = CodexHome();
            if (home == null) return false;
            Directory.CreateDirectory(home);
            var path = Path.Combine(home, ProfileName + ".config.toml");
            var body = BuildProfileToml();
            // Leave the file alone when our part of it is already right.
            //
            // This is load-bearing, not an optimisation: when the user trusts
            // these hooks, codex APPENDS its trust record to this same file
            // ([hooks.state.'…:stop:0:0'] with a sha of each hook). Rewriting
            // the file would delete that record and re-ask on every single
            // launch, which is exactly the nag a one-time prompt must not
            // become. So we compare only the prefix we wrote, and rewrite
            // (deliberately dropping the trust, because the hooks really did
            // change) when it differs.
            if (File.Exists(path) && File.ReadAllText(path).StartsWith(body, StringComparison.Ordinal))
                return true;
            File.WriteAllText(path, body);
            return true;
        }
        catch { return false; }
    }

    /// The profile codex layers over the user's config: nothing but our hooks.
    private static string BuildProfileToml()
    {
        var sb = new StringBuilder();
        sb.Append("# Written by Perch. Layered over your own ~/.codex/config.toml only when\n");
        sb.Append("# codex is launched from inside a Perch pane (`--profile perch`), and only\n");
        sb.Append("# ever contains the hooks that report this pane's state back to Perch.\n");
        sb.Append("# Safe to delete: Perch rewrites it on the next launch.\n");

        // Event → the `perch hooks codex <event>` subcommand it calls. Kept in
        // the same shape as the Claude wrapper's map so the two stay legible
        // side by side; see HookHandler for what each one does.
        //   PreToolUse/PostToolUse are async — they're the per-tool firehose and
        //   must never sit on the agent's critical path.
        //   PermissionRequest is synchronous but prints nothing, so codex just
        //   goes on to ask the user as it normally would.
        Hook(sb, "SessionStart", "session-start");
        Hook(sb, "SessionEnd", "session-end", timeoutSec: 1);
        Hook(sb, "UserPromptSubmit", "prompt-submit");
        Hook(sb, "PreToolUse", "pre-tool-use", async: true);
        Hook(sb, "PostToolUse", "post-tool-use", async: true);
        // Long on purpose, and synchronous: for a team bot this hook HOLDS the
        // approval open while the owner answers the card in the room, then
        // prints the decision codex applies. For every other pane it returns at
        // once and codex shows its own card as it always would.
        Hook(sb, "PermissionRequest", "permission-request", timeoutSec: 590);
        Hook(sb, "Notification", "notification");
        Hook(sb, "Stop", "stop");
        // A turn the user aborted (Esc) ends without a Stop, exactly like
        // Claude Code — but codex tells us about it, so the pane settles
        // immediately instead of waiting for the silence watchdog. 3 s is
        // codex's own ceiling for this event; asking for more just prints
        // "clamping Interrupt hook timeout to 3s" over the pane on every
        // launch.
        Hook(sb, "Interrupt", "stop", timeoutSec: 3);
        return sb.ToString();
    }

    private static void Hook(StringBuilder sb, string evt, string verb, int timeoutSec = 10, bool async = false)
    {
        var self = Environment.ProcessPath ?? "perch.exe";
        sb.Append($"\n[[hooks.{evt}]]\nmatcher = \"\"\n");
        sb.Append($"  [[hooks.{evt}.hooks]]\n");
        sb.Append("  type = \"command\"\n");
        sb.Append($"  command = {TomlString($"{HookExePath()} hooks codex {verb}")}\n");
        sb.Append($"  timeout = {timeoutSec}\n");
        if (async) sb.Append("  async = true\n");
    }

    /// Our own path, in the form codex's hook runner can actually launch:
    /// UNQUOTED. Codex splits a hook command on whitespace and execs argv[0]
    /// itself — it does not hand the string to a shell — so a conventionally
    /// quoted `"C:\…\perch.exe" hooks codex stop` makes argv[0] literally
    /// include the quote characters, no such file exists, and every hook dies
    /// with "hook exited with code 1" before a byte of ours runs. (Measured
    /// 2026-09-04 against codex-cli 0.153.2: identical hooks fail quoted and
    /// succeed unquoted.)
    ///
    /// Unquoted means a space in the path would split into two arguments, so
    /// when there is one we hand over the 8.3 short path instead, which never
    /// contains spaces. If short names are disabled on that volume there is
    /// nothing left to try and we return the quoted form — codex's hooks then
    /// don't run, and the pane falls back to the badge + output watchdog.
    private static string HookExePath()
    {
        var self = Environment.ProcessPath ?? "perch.exe";
        if (!self.Contains(' ')) return self;
        if (!OperatingSystem.IsWindows()) return UnixSpacelessAlias(self);
        var buf = new StringBuilder(600);
        var n = GetShortPathName(self, buf, buf.Capacity);
        var shortPath = n > 0 && n < buf.Capacity ? buf.ToString() : "";
        return shortPath.Length > 0 && !shortPath.Contains(' ') ? shortPath : "\"" + self + "\"";
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathName(string lpszLongPath, StringBuilder lpszShortPath, int cchBuffer);

    /// macOS/Linux have no 8.3 names, but they do have symlinks: a space-free
    /// alias in the temp dir (keyed by a hash of the real path, so two installs
    /// never share one) points at the real binary. Codex execs the alias,
    /// Environment.ProcessPath in the hook still resolves to the real file.
    /// Falls back to the quoted form (hooks then don't run — see above) if
    /// the link can't be made.
    private static string UnixSpacelessAlias(string self)
    {
        try
        {
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                Encoding.UTF8.GetBytes(self)))[..12].ToLowerInvariant();
            var alias = Path.Combine(Path.GetTempPath(), $"perch-hook-{key}");
            if (alias.Contains(' ')) return "\"" + self + "\"";
            var existing = new FileInfo(alias);
            if (existing.Exists && existing.LinkTarget == self) return alias;
            try { File.Delete(alias); } catch { }
            File.CreateSymbolicLink(alias, self);
            return alias;
        }
        catch { return "\"" + self + "\""; }
    }

    /// TOML string literal for a Windows command line. Single-quoted (literal)
    /// so backslashes stay backslashes; falls back to a basic string with
    /// escapes if the value itself contains a single quote.
    private static string TomlString(string value)
    {
        if (!value.Contains('\'')) return "'" + value + "'";
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static void SendAgent(string pipeName, string name)
    {
        var json = JsonSerializer.Serialize(new { type = "agent", name });
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(1500);
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                return;
            }
            catch { /* the badge is cosmetic — never let it break codex */ }
            System.Threading.Thread.Sleep(100);
        }
    }

    private static string? ExtractPipeName(string? pipePath)
    {
        if (string.IsNullOrEmpty(pipePath)) return null;
        const string prefix = @"\\.\pipe\";
        return pipePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? pipePath.Substring(prefix.Length)
            : pipePath;
    }
}
