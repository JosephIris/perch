using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PerchCli;

// Intercepts `claude` invocations inside a perch pane and injects Claude Code's
// inline --settings flag with a HOOKS_JSON that routes every hook event back
// to us. Modeled on perch-mac's Resources/bin/claude wrapper.
//
// Outside a perch pane (PERCH_PIPE unset), we transparently pass through to the
// real claude binary so the user's PATH still works as expected.
internal static class ClaudeWrapper
{
    public static int Run(string[] args)
    {
        var passthroughArgs = new string[args.Length - 1];
        Array.Copy(args, 1, passthroughArgs, 0, passthroughArgs.Length);

        var realClaude = FindRealClaude();
        if (realClaude == null)
        {
            Console.Error.WriteLine("perch wrap-claude: real `claude` binary not found on PATH (skipping perch's tools dir)");
            return 127;
        }

        // Outside perch: passthrough. Inside perch: inject --settings.
        // The user's own ~/.claude/settings.json is preserved — Claude Code
        // merges --settings additively.
        var pipePath = Environment.GetEnvironmentVariable("PERCH_PIPE");
        var inPerch = !string.IsNullOrEmpty(pipePath);

        var psi = new ProcessStartInfo(realClaude)
        {
            UseShellExecute = false,
            // No stdio redirection: child inherits our handles so it sees a
            // real TTY/PTY. Critical for claude's interactive UI.
        };

        if (inPerch)
        {
            // Write hooks JSON to a file and pass the path. Passing the JSON
            // literally fails when real claude is a .cmd shim (the npm-installed
            // case): Process.Start spawns it via cmd.exe, whose quote handling
            // mangles the JSON's inner `"` chars. Claude Code accepts
            // `--settings <file-or-json>` per its --help, so a path works
            // identically on both .exe and .cmd targets.
            var path = WriteHooksFile();
            psi.ArgumentList.Add("--settings");
            psi.ArgumentList.Add(path);

            // Per-pane model selection: the host drops the chosen CLI alias in a
            // temp file keyed by PERCH_PANE_ID whenever the user picks one, and
            // re-reads it here at every launch (an env var frozen at shell spawn
            // couldn't follow a mid-session change). Added BEFORE passthrough so
            // an explicit user `--model X` on the command line still wins.
            var model = ReadModelAlias();
            if (!string.IsNullOrEmpty(model))
            {
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model!);
            }

            // Per-pane session name, same temp-file channel as the model alias.
            // The host keeps it equal to the tab title (deduped across panes),
            // so `/list-agents` inside ANY session shows the names the user
            // already sees in the sidebar — that's what makes "tell weekly-digest
            // about the rename" addressable without a lookup. Skipped when the
            // user passed their own -n/--name.
            var peerName = ReadPeerName();
            var callerNamed = Array.IndexOf(passthroughArgs, "--name") >= 0
                || Array.IndexOf(passthroughArgs, "-n") >= 0;
            if (!string.IsNullOrEmpty(peerName) && !callerNamed)
            {
                psi.ArgumentList.Add("--name");
                psi.ArgumentList.Add(peerName!);
            }

            // Record the name this launch ACTUALLY goes out under. The host
            // writes the name it INTENDS; a project tab passes its own --name
            // (a slug, because a spaced name shreds in the shell's startup
            // line) and that one wins. The session-start hook used to re-read
            // the host's file, so the host recorded "loc diff fix" for a
            // session that answers to "loc-diff-fix", and every peer note
            // addressed to the real name was dropped as "not one of our
            // tabs". Now the hook reports THIS file: the address, not the
            // intent.
            RecordLaunchedName(callerNamed ? CallerName(passthroughArgs) : peerName);

            // Per-bot standing brief, same temp-file channel. The host points
            // this pane at the bot's rendered system.md and it is appended to
            // Claude Code's own system prompt (append, never replace: the bot
            // is a full Claude Code session with a role, not a bare model).
            // Re-read at every launch so an edited brief applies on relaunch
            // without a flag. A caller supplying its own system-prompt flag
            // wins, same as --model and --name.
            var brief = ReadBriefPath();
            if (brief != null && !HasSystemPromptArg(passthroughArgs))
            {
                psi.ArgumentList.Add("--append-system-prompt-file");
                psi.ArgumentList.Add(brief);
            }
        }
        foreach (var a in passthroughArgs) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch wrap-claude: failed to exec {realClaude}: {ex.Message}");
            return 1;
        }
    }

    /// The first claude binary on PATH that is NOT inside the wrapper's own
    /// directory (so the claude.cmd shim doesn't resolve to itself).
    private static string? FindRealClaude() => BinResolver.FindOnPathSkippingSelf("claude");

    /// Read the host-written per-pane model alias from
    /// %TEMP%\perch-claude-model-&lt;PERCH_PANE_ID&gt;.txt. Returns null when unset.
    /// Validated to a single safe token so a corrupt/stale file can never inject
    /// extra arguments into the real claude invocation.
    private static string? ReadModelAlias()
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrEmpty(paneId)) return null;
            var path = Path.Combine(Path.GetTempPath(), $"perch-claude-model-{paneId}.txt");
            if (!File.Exists(path)) return null;
            var alias = File.ReadAllText(path).Trim();
            if (alias.Length == 0 || alias.Length > 40) return null;
            foreach (var c in alias)
                if (!(char.IsLetterOrDigit(c) || c is '-' or '_' or '.')) return null;
            return alias;
        }
        catch { return null; }
    }

    /// Read the host-written per-pane session name from
    /// %TEMP%\perch-claude-name-&lt;PERCH_PANE_ID&gt;.txt. Returns null when unset.
    /// The host sanitizes on write; the validation here is only the last line
    /// of defense against a corrupt/stale file becoming a weird CLI arg.
    private static string? ReadPeerName()
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrEmpty(paneId)) return null;
            var path = Path.Combine(Path.GetTempPath(), $"perch-claude-name-{paneId}.txt");
            if (!File.Exists(path)) return null;
            var name = File.ReadAllText(path).Trim();
            if (name.Length == 0 || name.Length > 60) return null;
            foreach (var c in name)
                if (char.IsControl(c) || c is '"' or '\'') return null;
            return name;
        }
        catch { return null; }
    }

    /// The name the caller passed with --name/-n (either "--name X" or
    /// "--name=X"), or null when the flag has no value.
    private static string? CallerName(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a is "--name" or "-n")
                return i + 1 < args.Length ? args[i + 1] : null;
            if (a.StartsWith("--name=", StringComparison.Ordinal))
                return a.Substring("--name=".Length);
        }
        return null;
    }

    /// Write %TEMP%\perch-claude-launched-name-&lt;PERCH_PANE_ID&gt;.txt with the
    /// effective session name, or delete it when this launch has none (cc
    /// then auto-names from the cwd and the hook falls back to the host's
    /// file, then to nothing). Never throws: a missing record costs the host
    /// an address, not the user a launch.
    private static void RecordLaunchedName(string? name)
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrEmpty(paneId)) return;
            var path = Path.Combine(Path.GetTempPath(), $"perch-claude-launched-name-{paneId}.txt");
            var n = (name ?? "").Trim();
            if (n.Length == 0 || n.Length > 60)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            File.WriteAllText(path, n);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch wrap-claude: couldn't record the session name: {ex.Message}");
        }
    }

    /// True when the caller already chose a system prompt, in any of the
    /// four spellings Claude Code accepts. Ours must not stack on top.
    private static bool HasSystemPromptArg(string[] args)
    {
        foreach (var a in args)
        {
            if (a is "--append-system-prompt" or "--append-system-prompt-file"
                  or "--system-prompt" or "--system-prompt-file") return true;
            if (a.StartsWith("--append-system-prompt=", StringComparison.Ordinal)
                || a.StartsWith("--append-system-prompt-file=", StringComparison.Ordinal)
                || a.StartsWith("--system-prompt=", StringComparison.Ordinal)
                || a.StartsWith("--system-prompt-file=", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// Read the host-written per-pane brief pointer from
    /// %TEMP%\perch-claude-brief-&lt;PERCH_PANE_ID&gt;.txt. Returns the absolute path
    /// of the bot's system.md, or null when this pane runs no bot.
    ///
    /// Validated harder than the other side-channels because the payload is
    /// a FILE READ INTO THE SYSTEM PROMPT: the path must exist, be a .md of
    /// sane size, carry no quote/control characters, and live under a
    /// `.perch\team\` folder. A stale or tampered marker therefore cannot
    /// make `claude` swallow an arbitrary file as its instructions.
    private static string? ReadBriefPath()
    {
        try
        {
            var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
            if (string.IsNullOrEmpty(paneId)) return null;
            var marker = Path.Combine(Path.GetTempPath(), $"perch-claude-brief-{paneId}.txt");
            if (!File.Exists(marker)) return null;
            var path = File.ReadAllText(marker).Trim();
            if (path.Length == 0 || path.Length > 260) return null;
            foreach (var c in path)
                if (char.IsControl(c) || c is '"' or '\'') return null;
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;
            if (path.IndexOf(@"\.perch\team\", StringComparison.OrdinalIgnoreCase) < 0
                && path.IndexOf("/.perch/team/", StringComparison.OrdinalIgnoreCase) < 0) return null;
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 256 * 1024) return null;
            return info.FullName;
        }
        catch { return null; }
    }

    /// Writes the hooks JSON to a per-pane temp file and returns the path.
    /// Idempotent: overwriting the same file each time the wrapper runs in
    /// a given pane is fine — claude reads it once at startup. We don't try
    /// to clean these up (they're small, %TEMP% is the OS's responsibility).
    private static string WriteHooksFile()
    {
        var paneId = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
        var safeName = string.IsNullOrEmpty(paneId)
            ? $"perch-claude-hooks-{Process.GetCurrentProcess().Id}.json"
            : $"perch-claude-hooks-{paneId}.json";
        var path = Path.Combine(Path.GetTempPath(), safeName);
        File.WriteAllText(path, BuildHooksJson());
        return path;
    }

    /// Builds Claude Code's --settings payload, identical in shape to perch-mac's
    /// wrapper. Every hook calls back into our CLI's `hooks claude <event>`
    /// subcommand, which reads the hook payload on stdin and routes it to IPC.
    private static string BuildHooksJson()
    {
        // Use our own absolute path so the spawned hook process resolves us
        // even if PATH has been mutated mid-session.
        var self = Environment.ProcessPath ?? "perch.exe";

        // Helper to keep the JSON structure readable. Each event maps to a
        // single hook entry calling our subcommand. timeout matches perch-mac's
        // values where they were specific; otherwise a 10s default.
        object Hook(string eventName, int timeoutSec = 10, bool async = false, string matcher = "")
        {
            var hook = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["type"] = "command",
                ["command"] = $"\"{self}\" hooks claude {eventName}",
                ["timeout"] = timeoutSec,
            };
            if (async) hook["async"] = true;
            return new { matcher, hooks = new[] { hook } };
        }

        var hooks = new System.Collections.Generic.Dictionary<string, object>
        {
            ["SessionStart"]      = new[] { Hook("session-start") },
            ["Stop"]              = new[] { Hook("stop") },
            ["SubagentStop"]      = new[] { Hook("subagent-stop", async: true) },
            ["SessionEnd"]        = new[] { Hook("session-end", timeoutSec: 1) },
            ["Notification"]      = new[] { Hook("notification") },
            ["UserPromptSubmit"]  = new[] { Hook("prompt-submit") },
            // PostToolUse fires right after a tool executes — the only signal
            // that arrives AFTER a permission prompt is answered (approving isn't
            // a UserPromptSubmit). Without it a pane sticks on red "permission"
            // until the turn's Stop. async so it never sits on the agent's
            // critical path; the host coalesces the resulting working→working
            // firehose (see OnAgentStatus).
            ["PostToolUse"]       = new[] { Hook("post-tool-use", async: true) },
        };

        // PreToolUse carries at most two NARROW matcher groups — the "" matcher
        // that would fire on every Read/Grep/Edit (the old status-detail
        // firehose) stays unregistered.
        //
        // "SendMessage" is cross-session messaging: it fires only when the agent
        // messages another session, which is rare, and async keeps it entirely
        // off the agent's critical path (we print nothing, so the unread-stdout
        // limitation of async hooks costs nothing). It's what lets the sidebar
        // show the send while it's happening, not after.
        //
        // "Bash" is registered ONLY when gcloud is actually installed, purely to
        // stamp agent-attribution labels onto `gcloud ... create`. It has to be
        // SYNCHRONOUS (rewriting the command means its stdout must be read),
        // which puts a short-lived process on the critical path of EVERY Bash
        // call — a fair trade for someone driving GCP from an agent, pure
        // latency for everyone else, so the gate stays.
        var pre = new System.Collections.Generic.List<object>
        {
            Hook("pre-send", timeoutSec: 5, async: true, matcher: "SendMessage"),
        };
        if (BinResolver.FindOnPathSkippingSelf("gcloud") != null)
            pre.Add(Hook("pre-bash", timeoutSec: 5, matcher: "Bash"));
        hooks["PreToolUse"] = pre.ToArray();

        var settings = new
        {
            preferredNotifChannel = "notifications_disabled",
            hooks,
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }
}
