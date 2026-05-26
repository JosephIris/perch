using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CmuxCli;

// Tiny shell-side helper that talks to the cmux-win host over a named pipe.
// The host puts the pipe path in CMUX_PIPE before spawning each pane's
// shell; we connect, send one JSON line, and exit. Outside of a cmux pane
// CMUX_PIPE is unset and every subcommand is a silent no-op so scripts
// can call `cmux notify ...` unconditionally.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 0; }

        // wrap-claude must run even outside a cmux pane — it pass-throughs to
        // the real claude binary. Carve it out before the CMUX_PIPE no-op.
        if (args[0] == "wrap-claude") return ClaudeWrapper.Run(args);
        if (args[0] is "-h" or "--help" or "help") return PrintUsageAndExit(0);

        var pipePath = Environment.GetEnvironmentVariable("CMUX_PIPE");
        if (string.IsNullOrEmpty(pipePath))
            return 0; // not in a cmux pane — silent no-op

        var pipeName = ExtractPipeName(pipePath);
        if (pipeName == null)
        {
            Console.Error.WriteLine($"cmux: CMUX_PIPE doesn't look like a pipe path: {pipePath}");
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
                "hooks"  => HookHandler.Run(pipeName, args),
                _ => PrintUnknown(args[0]),
            };
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("cmux: host not listening (timed out)");
            return 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"cmux: {ex.Message}");
            return 1;
        }
    }

    private static int CmdNotify(string pipeName, string[] args)
    {
        // Usage: cmux notify [--level info|success|warn|error] <text...>
        string? level = null;
        var i = 1;
        while (i < args.Length && args[i].StartsWith("--"))
        {
            if (args[i] == "--level" && i + 1 < args.Length) { level = args[++i]; i++; continue; }
            Console.Error.WriteLine($"cmux notify: unknown flag {args[i]}");
            return 2;
        }
        if (i >= args.Length) { Console.Error.WriteLine("cmux notify: missing text"); return 2; }
        var text = string.Join(' ', args, i, args.Length - i);

        var payload = new { type = "notify", text, level };
        return Send(pipeName, payload);
    }

    private static int CmdStatus(string pipeName, string[] args)
    {
        // Usage: cmux status <idle|working|waiting|done> [detail...]
        if (args.Length < 2) { Console.Error.WriteLine("cmux status: missing state"); return 2; }
        var state = args[1];
        var detail = args.Length > 2 ? string.Join(' ', args, 2, args.Length - 2) : null;
        return Send(pipeName, new { type = "status", state, detail });
    }

    private static int CmdMeta(string pipeName, string[] args)
    {
        // Usage: cmux meta [--branch X] [--port N]... [--cwd path]
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
                    else { Console.Error.WriteLine($"cmux meta: --port expects an integer"); return 2; }
                    break;
                default:
                    Console.Error.WriteLine($"cmux meta: unknown flag {args[i]}");
                    return 2;
            }
        }
        return Send(pipeName, new { type = "meta", branch, ports = ports.Count == 0 ? null : ports.ToArray(), cwd });
    }

    private static int CmdFocus(string pipeName, string[] args)
    {
        // Usage: cmux focus <pane-name|session:pane>
        if (args.Length < 2) { Console.Error.WriteLine("cmux focus: missing target"); return 2; }
        return Send(pipeName, new { type = "focus", target = args[1] });
    }

    private static int CmdSend(string pipeName, string[] args)
    {
        // Usage: cmux send <target> <input...>
        //   Input is sent literally — caller embeds any newline (\n) needed.
        //   To send a real newline from a shell, use a "$'...\n...'" string.
        if (args.Length < 3) { Console.Error.WriteLine("cmux send: missing target and/or input"); return 2; }
        var input = string.Join(' ', args, 2, args.Length - 2);
        return Send(pipeName, new { type = "send", target = args[1], input });
    }

    private static int CmdOpen(string pipeName, string[] args)
    {
        // Usage: cmux open [--name X] [--cwd path] [--cmd "shell command"]
        string? name = null, cwd = null, cmd = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--name" when i + 1 < args.Length: name = args[++i]; break;
                case "--cwd"  when i + 1 < args.Length: cwd  = args[++i]; break;
                case "--cmd"  when i + 1 < args.Length: cmd  = args[++i]; break;
                default:
                    Console.Error.WriteLine($"cmux open: unknown flag {args[i]}");
                    return 2;
            }
        }
        return Send(pipeName, new { type = "open", name, cwd, cmd });
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

    /// CMUX_PIPE is shipped as a full path `\\.\pipe\<name>`. NamedPipeClientStream
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
        Console.Error.WriteLine($"cmux: unknown command '{cmd}'");
        PrintUsage();
        return 2;
    }

    private static int PrintUsageAndExit(int code) { PrintUsage(); return code; }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  cmux notify [--level info|success|warn|error] <text...>");
        Console.WriteLine("  cmux status <idle|working|waiting|done> [detail...]");
        Console.WriteLine("  cmux meta [--branch X] [--port N]... [--cwd path]");
        Console.WriteLine("  cmux focus <pane-name|session:pane>");
        Console.WriteLine("  cmux send <target> <input...>");
        Console.WriteLine("  cmux open [--name X] [--cwd path] [--cmd command]");
        Console.WriteLine();
        Console.WriteLine("Outside a cmux pane (no CMUX_PIPE set) every command is a silent no-op.");
    }
}
