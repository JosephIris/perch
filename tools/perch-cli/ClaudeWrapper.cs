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

        // PreToolUse is registered ONLY when gcloud is actually installed, and
        // only to stamp agent labels onto `gcloud ... create`.
        //
        // Why the gate: this hook has to be SYNCHRONOUS (an async hook's stdout
        // isn't read, so it couldn't rewrite the command), which puts a
        // short-lived process on the critical path of EVERY Bash call. For
        // someone who drives GCP from an agent that's a fair trade. For everyone
        // else it would be pure latency for a hook that can never fire — so they
        // don't get it at all.
        //
        // Note this is NOT the old `pre-tool-use` status reporter: that one needed
        // the "" matcher, fired on every Read/Grep/Edit, and its cycling detail
        // string was noise rather than signal. It stays unregistered.
        if (BinResolver.FindOnPathSkippingSelf("gcloud") != null)
            hooks["PreToolUse"] = new[] { Hook("pre-bash", timeoutSec: 5, matcher: "Bash") };

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
