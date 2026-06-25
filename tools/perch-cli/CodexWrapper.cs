using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace PerchCli;

// Intercepts `codex` invocations inside a perch pane so the pane header shows a
// "CX" badge while codex runs. Unlike Claude Code, codex exposes no hook system
// we can inject, so we simply BRACKET the real codex process: tell the host the
// pane's agent is "codex" before launching it, and "" again once it exits.
// Outside a perch pane (PERCH_PIPE unset) it's a transparent passthrough.
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
            // crash/Ctrl-C path.
            if (pipeName != null) SendAgent(pipeName, "");
        }
    }

    private static void SendAgent(string pipeName, string name)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(1500);
            var json = JsonSerializer.Serialize(new { type = "agent", name });
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch { /* the badge is cosmetic — never let it break codex */ }
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
