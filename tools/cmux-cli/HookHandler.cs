using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CmuxCli;

// Handles `cmux hooks claude <event>` callbacks fired by Claude Code via the
// --settings hooks JSON our wrapper injects. Claude passes the hook context
// as JSON on stdin; we extract what's useful and send it back to the cmux
// host through the IPC pipe.
//
// We intentionally accept-and-forget unknown events so adding a new hook to
// Claude Code doesn't break us.
internal static class HookHandler
{
    public static int Run(string pipeName, string[] args)
    {
        // Usage: cmux hooks claude <event>
        if (args.Length < 3 || args[1] != "claude")
        {
            Console.Error.WriteLine("cmux hooks: usage: cmux hooks claude <event>");
            return 2;
        }
        var evt = args[2];

        // Claude Code passes the hook payload on stdin as JSON. Read it
        // (with a small budget) and try to parse — if it isn't JSON, fall
        // back to event-only behavior so we still report state transitions.
        string stdinPayload = "";
        try
        {
            if (Console.IsInputRedirected)
            {
                using var sr = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
                // 64KB cap so a runaway payload can't hang us. Claude's
                // hook payloads are tiny in practice.
                var buf = new char[64 * 1024];
                var n = sr.ReadBlock(buf, 0, buf.Length);
                stdinPayload = new string(buf, 0, n);
            }
        }
        catch { /* tolerate */ }

        JsonElement? root = null;
        if (!string.IsNullOrWhiteSpace(stdinPayload))
        {
            try
            {
                var doc = JsonDocument.Parse(stdinPayload);
                root = doc.RootElement.Clone();
            }
            catch { /* not JSON; ignore */ }
        }

        // Map Claude's event → our IPC message(s).
        switch (evt)
        {
            case "session-start":
                Send(pipeName, new { type = "status", state = "working", detail = "claude started" });
                break;

            case "prompt-submit":
                // User just submitted a prompt → agent is thinking. Include
                // the prompt's leading chars as detail when present.
                Send(pipeName, new { type = "status", state = "working", detail = StringFrom(root, "prompt", maxLen: 60) ?? "thinking" });
                break;

            case "notification":
                // Permission requests, "waiting for input" prompts, etc.
                var msg = StringFrom(root, "message") ?? "claude needs attention";
                Send(pipeName, new { type = "notify", level = "warn", text = msg });
                Send(pipeName, new { type = "status", state = "waiting", detail = (string?)null });
                break;

            case "stop":
                Send(pipeName, new { type = "status", state = "done", detail = (string?)null });
                break;

            case "subagent-stop":
                // Subagent finished; not a state transition for the top-level
                // pane. Skip — agents tend to fire many of these.
                break;

            case "session-end":
                Send(pipeName, new { type = "status", state = "idle", detail = (string?)null });
                break;

            case "pre-tool-use":
                // Optional: report the tool name as activity detail. Cheap
                // signal; useful for "what's the agent doing right now?".
                var tool = StringFrom(root, "tool_name") ?? StringFrom(root, "tool");
                if (!string.IsNullOrEmpty(tool))
                    Send(pipeName, new { type = "status", state = "working", detail = $"using {tool}" });
                break;

            default:
                // Unknown event — keep the hook fast and silent.
                break;
        }
        return 0;
    }

    /// JSON helper: pulls a string property if present, optionally truncating.
    /// Claude payloads may carry the field at the root OR nested under a
    /// wrapper, so we check both shapes.
    private static string? StringFrom(JsonElement? root, string name, int maxLen = 0)
    {
        if (root is not JsonElement el) return null;
        string? Pick(JsonElement e)
        {
            if (e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString();
            return null;
        }
        var s = Pick(el);
        if (s == null)
        {
            // Some Claude versions nest under "hook_input" / "data".
            foreach (var wrap in new[] { "hook_input", "data" })
            {
                if (el.TryGetProperty(wrap, out var w) && w.ValueKind == JsonValueKind.Object)
                {
                    s = Pick(w);
                    if (s != null) break;
                }
            }
        }
        if (s != null && maxLen > 0 && s.Length > maxLen) s = s.Substring(0, maxLen) + "…";
        return s;
    }

    private static void Send(string pipeName, object payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(2000);
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch (Exception ex)
        {
            // Hooks must never break the agent. Log to stderr (which Claude
            // shows when verbose) and move on.
            Console.Error.WriteLine($"cmux hooks: send failed: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
