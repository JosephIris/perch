using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Perch.Tests;

/// The headless `claude -p` runner. Parse is pinned against the shapes the CLI
/// actually emits (one result object, or an event array ending in one), and
/// the launch is exercised against a FAKE claude.cmd so the two things that
/// would silently misbehave in production — the prompt not reaching stdin,
/// and the pane env leaking into the child — fail here first.
public class ClaudeHeadlessTests
{
    [Fact]
    public void Parse_SingleResultObject_IsTheAnswer()
    {
        var json = "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"result\":\"## Role\\nYou own src/web.\"," +
                   "\"total_cost_usd\":0.0421,\"duration_ms\":8123,\"session_id\":\"x\"}";
        var r = ClaudeHeadless.Parse(json, "", 0);
        Assert.True(r.Ok);
        Assert.Equal("## Role\nYou own src/web.", r.Text);
        Assert.Equal(0.0421, r.CostUsd, 4);
        Assert.Equal(8123, r.DurationMs);
        Assert.Null(r.Error);
    }

    [Fact]
    public void Parse_EventArray_TakesTheLastResult()
    {
        var json = "[{\"type\":\"system\",\"subtype\":\"init\"},{\"type\":\"assistant\"}," +
                   "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"first\"}," +
                   "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"final\",\"total_cost_usd\":0.01}]";
        var r = ClaudeHeadless.Parse(json, "", 0);
        Assert.True(r.Ok);
        Assert.Equal("final", r.Text);
    }

    [Fact]
    public void Parse_ErrorResult_IsAFailureWithTheReason()
    {
        var json = "{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"result\":\"boom\"}";
        var r = ClaudeHeadless.Parse(json, "", 1);
        Assert.False(r.Ok);
        Assert.Equal("boom", r.Error);

        var budget = ClaudeHeadless.Parse("{\"type\":\"result\",\"subtype\":\"error_max_budget_usd\",\"result\":\"\"}", "", 1);
        Assert.False(budget.Ok);
        Assert.Equal("stopped at the cost cap", budget.Error);
    }

    [Fact]
    public void Parse_Garbage_AndSilence_AreFailures_NotThrows()
    {
        var garbage = ClaudeHeadless.Parse("not json at all", "", 0);
        Assert.False(garbage.Ok);
        Assert.Contains("wasn't JSON", garbage.Error);

        var empty = ClaudeHeadless.Parse("", "", 0);
        Assert.False(empty.Ok);

        var crashed = ClaudeHeadless.Parse("", "Error: something exploded", 2);
        Assert.False(crashed.Ok);
        Assert.Contains("exploded", crashed.Error);

        var timedOut = ClaudeHeadless.Parse("", "timed out", -1);
        Assert.Equal("timed out", timedOut.Error);
    }

    [Fact]
    public void Command_RunsABatchShimThroughCmd_AndAnExeDirectly()
    {
        var (file, args) = ClaudeHeadless.Command(@"C:\Users\me\AppData\Roaming\npm\claude.cmd",
            new[] { "-p", "--output-format", "json", "--model", "haiku", "--json-schema", "{\"type\":\"object\"}" });
        Assert.EndsWith("cmd.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/d /s /c \"\"C:\\Users\\me\\AppData\\Roaming\\npm\\claude.cmd\" -p --output-format json --model haiku --json-schema ", args);
        Assert.Contains("\"{\\\"type\\\":\\\"object\\\"}\"", args);   // schema quoted, inner quotes escaped
        Assert.EndsWith("\"", args);

        var (exe, exeArgs) = ClaudeHeadless.Command(@"C:\tools\claude.exe", new[] { "-p", "--model", "haiku" });
        Assert.Equal(@"C:\tools\claude.exe", exe);
        Assert.Equal("-p --model haiku", exeArgs);
    }

    [Theory]
    [InlineData("haiku", "haiku")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Quote_OnlyWhenNeeded(string value, string expected)
        => Assert.Equal(expected, ClaudeHeadless.Quote(value));

    /// End to end against a fake `claude.cmd` first on PATH: the prompt must
    /// arrive on stdin, the pane variables must NOT reach the child, and the
    /// JSON it prints must come back parsed.
    [Fact]
    public async Task RunAsync_FeedsStdin_StripsPaneEnv_AndParsesTheAnswer()
    {
        var dir = Path.Combine(Path.GetTempPath(), "perch-headless-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var capture = Path.Combine(dir, "capture.txt");
        // The fake: record argv and env, echo stdin into the capture, answer JSON.
        File.WriteAllText(Path.Combine(dir, "claude.cmd"),
            "@echo off\r\n" +
            $"echo ARGS=%* > \"{capture}\"\r\n" +
            $"echo PIPE=[%PERCH_PIPE%] >> \"{capture}\"\r\n" +
            $"echo PANE=[%PERCH_PANE_ID%] >> \"{capture}\"\r\n" +
            $"more >> \"{capture}\"\r\n" +
            "echo {\"type\":\"result\",\"subtype\":\"success\",\"result\":\"PONG\",\"total_cost_usd\":0.001,\"duration_ms\":5}\r\n");

        var oldPipe = Environment.GetEnvironmentVariable("PERCH_PIPE");
        var oldPane = Environment.GetEnvironmentVariable("PERCH_PANE_ID");
        try
        {
            ClaudeHeadless.ResolveOverride = () => Path.Combine(dir, "claude.cmd");
            Environment.SetEnvironmentVariable("PERCH_PIPE", @"\\.\pipe\perch\leak");
            Environment.SetEnvironmentVariable("PERCH_PANE_ID", "leak");

            var r = await ClaudeHeadless.RunAsync("PROMPT-ON-STDIN", dir, "haiku", "test.headless",
                new[] { "--tools", "" }, timeoutMs: 30_000);

            Assert.True(r.Ok, r.Error ?? r.RawJson);
            Assert.Equal("PONG", r.Text);
            var got = File.ReadAllText(capture);
            Assert.Contains("-p --output-format json --no-session-persistence --model haiku --tools \"\"", got);
            Assert.Contains("PIPE=[]", got);
            Assert.Contains("PANE=[]", got);
            Assert.Contains("PROMPT-ON-STDIN", got);
        }
        finally
        {
            ClaudeHeadless.ResolveOverride = null;
            Environment.SetEnvironmentVariable("PERCH_PIPE", oldPipe);
            Environment.SetEnvironmentVariable("PERCH_PANE_ID", oldPane);
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RunAsync_WithoutClaudeInstalled_FailsPlainly()
    {
        try
        {
            ClaudeHeadless.ResolveOverride = () => null;
            var r = await ClaudeHeadless.RunAsync("x", Path.GetTempPath(), "", "test.headless", timeoutMs: 5000);
            Assert.False(r.Ok);
            Assert.Contains("isn't installed", r.Error);
        }
        finally { ClaudeHeadless.ResolveOverride = null; }
    }
}
