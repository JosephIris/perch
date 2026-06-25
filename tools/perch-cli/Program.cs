using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PerchCli;

// Tiny shell-side helper that talks to the perch host over a named pipe.
// The host puts the pipe path in PERCH_PIPE before spawning each pane's
// shell; we connect, send one JSON line, and exit. Outside of a perch pane
// PERCH_PIPE is unset and every subcommand is a silent no-op so scripts
// can call `perch notify ...` unconditionally.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 0; }

        // wrap-claude / wrap-codex must run even outside a perch pane — they
        // pass through to the real binary. Carve them out before the
        // PERCH_PIPE no-op.
        if (args[0] == "wrap-claude") return ClaudeWrapper.Run(args);
        if (args[0] == "wrap-codex")  return CodexWrapper.Run(args);
        if (args[0] is "-h" or "--help" or "help") return PrintUsageAndExit(0);

        // `perch test <verb>` targets the app-level control pipe directly and
        // doesn't depend on PERCH_PIPE being set. Used by the test harness so
        // it can drive splits/closes without synthesizing keystrokes (which
        // leaked to whatever window had focus and once tore down Chrome).
        if (args[0] == "test") return CmdTest(args);

        var pipePath = Environment.GetEnvironmentVariable("PERCH_PIPE");
        if (string.IsNullOrEmpty(pipePath))
            return 0; // not in a perch pane — silent no-op

        var pipeName = ExtractPipeName(pipePath);
        if (pipeName == null)
        {
            Console.Error.WriteLine($"perch: PERCH_PIPE doesn't look like a pipe path: {pipePath}");
            return 2;
        }

        try
        {
            return args[0] switch
            {
                "notify" => CmdNotify(pipeName, args),
                "status" => CmdStatus(pipeName, args),
                "meta"   => CmdMeta(pipeName, args),
                "focus"  => CmdFocus(pipeName, args),
                "send"   => CmdSend(pipeName, args),
                "open"   => CmdOpen(pipeName, args),
                "agent"  => CmdAgent(pipeName, args),
                "hooks"  => HookHandler.Run(pipeName, args),
                _ => PrintUnknown(args[0]),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("perch: host not listening (timed out)");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"perch: {ex.Message}");
            return 1;
        }
    }

    private static int CmdNotify(string pipeName, string[] args)
    {
        // Usage: perch notify [--level info|success|warn|error] <text...>
        string? level = null;
        var i = 1;
        while (i < args.Length && args[i].StartsWith("--"))
        {
            if (args[i] == "--level" && i + 1 < args.Length) { level = args[++i]; i++; continue; }
            Console.Error.WriteLine($"perch notify: unknown flag {args[i]}");
            return 2;
        }
        if (i >= args.Length) { Console.Error.WriteLine("perch notify: missing text"); return 2; }
        var text = string.Join(' ', args, i, args.Length - i);

        var payload = new { type = "notify", text, level };
        return Send(pipeName, payload);
    }

    private static int CmdStatus(string pipeName, string[] args)
    {
        // Usage: perch status <idle|working|waiting|permission> [detail...]
        if (args.Length < 2) { Console.Error.WriteLine("perch status: missing state"); return 2; }
        var state = args[1];
        var detail = args.Length > 2 ? string.Join(' ', args, 2, args.Length - 2) : null;
        return Send(pipeName, new { type = "status", state, detail });
    }

    private static int CmdMeta(string pipeName, string[] args)
    {
        // Usage: perch meta [--branch X] [--port N]... [--cwd path]
        string? branch = null, cwd = null;
        var ports = new System.Collections.Generic.List<int>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--branch" when i + 1 < args.Length: branch = args[++i]; break;
                case "--cwd" when i + 1 < args.Length:    cwd = args[++i]; break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var p)) ports.Add(p);
                    else { Console.Error.WriteLine($"perch meta: --port expects an integer"); return 2; }
                    break;
                default:
                    Console.Error.WriteLine($"perch meta: unknown flag {args[i]}");
                    return 2;
            }
        }
        return Send(pipeName, new { type = "meta", branch, ports = ports.Count == 0 ? null : ports.ToArray(), cwd });
    }

    private static int CmdFocus(string pipeName, string[] args)
    {
        // Usage: perch focus <pane-name|session:pane>
        if (args.Length < 2) { Console.Error.WriteLine("perch focus: missing target"); return 2; }
        return Send(pipeName, new { type = "focus", target = args[1] });
    }

    private static int CmdSend(string pipeName, string[] args)
    {
        // Usage: perch send <target> <input...>
        //   Input is sent literally — caller embeds any newline (\n) needed.
        //   To send a real newline from a shell, use a "$'...\n...'" string.
        if (args.Length < 3) { Console.Error.WriteLine("perch send: missing target and/or input"); return 2; }
        var input = string.Join(' ', args, 2, args.Length - 2);
        return Send(pipeName, new { type = "send", target = args[1], input });
    }

    private static int CmdOpen(string pipeName, string[] args)
    {
        // Usage: perch open [--name X] [--cwd path] [--cmd "shell command"]
        string? name = null, cwd = null, cmd = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--cwd"  when i + 1 < args.Length: cwd  = args[++i]; break;
                case "--cmd"  when i + 1 < args.Length: cmd  = args[++i]; break;
                default:
                    Console.Error.WriteLine($"perch open: unknown flag {args[i]}");
                    return 2;
            }
        }
        return Send(pipeName, new { type = "open", name, cwd, cmd });
    }

    private static int CmdAgent(string pipeName, string[] args)
    {
        // Usage: perch agent <name>   (no name / empty clears the badge)
        // Tells the host which agent runs in this pane so the header shows a
        // badge (e.g. "claude" → CC, "codex" → CX). Used by the codex.cmd
        // wrapper to bracket a codex run; callable directly too.
        var name = args.Length > 1 ? args[1] : "";
        return Send(pipeName, new { type = "agent", name });
    }

    private static int CmdTest(string[] args)
    {
        // Usage: perch test <verb> [--text S] [--id GUID] [--title S] [--shell S]
        //
        // Known verbs:
        //   pty.send       --text "echo hi\r"   -> writes UTF-8 bytes to the PTY
        //   pty.snapshot                         -> logs current byte count
        //   session.new    [--shell ...]
        //   session.select --id GUID
        //   session.close  --id GUID
        //   session.rename --id GUID --title S
        //
        // The host opens this pipe only when PERCH_ENABLE_TEST_IPC is set at
        // launch time; if it isn't, Connect fails fast and we tell the caller.
        if (args.Length < 2) { Console.Error.WriteLine("perch test: missing verb"); return 2; }
        var verb = args[1];
        var fields = new System.Collections.Generic.Dictionary<string, string>();
        for (var i = 2; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--") || i + 1 >= args.Length)
            {
                Console.Error.WriteLine($"perch test: bad flag {key}");
                return 2;
            }
            fields[key.Substring(2)] = args[++i];
        }

        // Build the payload manually so we keep declared key order and avoid
        // an anonymous-type-per-shape explosion.
        var payload = new System.Collections.Generic.Dictionary<string, object> { ["verb"] = verb };
        foreach (var kv in fields) payload[kv.Key] = kv.Value;

        try
        {
            using var client = new NamedPipeClientStream(".", @"perch\control", PipeDirection.Out);
            client.Connect(2000);
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("perch test: control pipe not listening (set PERCH_ENABLE_TEST_IPC before launching perch)");
            return 3;
        }
    }

    private static int Send(string pipeName, object payload)
    {
        using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
        client.Connect(2000); // 2s — host should be up before the shell starts
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        client.Write(bytes, 0, bytes.Length);
        client.Flush();
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// PERCH_PIPE is shipped as a full path `\\.\pipe\<name>`. NamedPipeClientStream
    /// wants just the `<name>` portion (the server side too — we let .NET prepend
    /// `\\.\pipe\` itself). Strip the prefix if present, otherwise return as-is
    /// to forgive small variations in how the env var is set.
    private static string? ExtractPipeName(string pipePath)
    {
        const string prefix = @"\\.\pipe\";
        if (pipePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return pipePath.Substring(prefix.Length);
        // Already a bare name?
        return pipePath.Length > 0 ? pipePath : null;
    }

    private static int PrintUnknown(string cmd)
    {
        Console.Error.WriteLine($"perch: unknown command '{cmd}'");
        PrintUsage();
        return 2;
    }

    private static int PrintUsageAndExit(int code) { PrintUsage(); return code; }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  perch notify [--level info|success|warn|error] <text...>");
        Console.WriteLine("  perch status <idle|working|waiting|permission> [detail...]");
        Console.WriteLine("  perch meta [--branch X] [--port N]... [--cwd path]");
        Console.WriteLine("  perch focus <pane-name|session:pane>");
        Console.WriteLine("  perch send <target> <input...>");
        Console.WriteLine("  perch open [--name X] [--cwd path] [--cmd command]");
        Console.WriteLine("  perch agent <name>           (header badge; empty clears)");
        Console.WriteLine();
        Console.WriteLine("Outside a perch pane (no PERCH_PIPE set) every command is a silent no-op.");
    }
}
