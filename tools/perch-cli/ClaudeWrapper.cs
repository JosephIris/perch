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
        object Hook(string eventName, int timeoutSec = 10, bool async = false)
        {
            var hook = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["type"] = "command",
                ["command"] = $"\"{self}\" hooks claude {eventName}",
                ["timeout"] = timeoutSec,
            };
            if (async) hook["async"] = true;
            return new { matcher = "", hooks = new[] { hook } };
        }

        // PreToolUse intentionally omitted: it fires for every tool call
        // (Read/Grep/Edit/Bash/…) which during agentic work means many per
        // second. The cycling detail string is noise rather than signal.
        // HookHandler.cs still has a pre-tool-use case, so anyone wanting
        // the firehose can re-add it here behind a setting later.
        var settings = new
        {
            preferredNotifChannel = "notifications_disabled",
            hooks = new System.Collections.Generic.Dictionary<string, object>
            {
                ["SessionStart"]      = new[] { Hook("session-start") },
                ["Stop"]              = new[] { Hook("stop") },
                ["SubagentStop"]      = new[] { Hook("subagent-stop", async: true) },
                ["SessionEnd"]        = new[] { Hook("session-end", timeoutSec: 1) },
                ["Notification"]      = new[] { Hook("notification") },
                ["UserPromptSubmit"]  = new[] { Hook("prompt-submit") },
            },
        };

        return JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });
    }
}
