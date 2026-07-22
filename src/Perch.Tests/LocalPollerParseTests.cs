using System;
using System.Text.Json;
using Perch;
using Xunit;

namespace Perch.Tests;

/// The scan's JSON travels through a real PowerShell subprocess, and process
/// command lines are arbitrary user text: one process launched with a raw BEL
/// (0x07) in its arguments reached ConvertTo-Json's output verbatim, the strict
/// parser refused the whole document, and every scan for the rest of the app
/// session returned nothing — no port chips, no panel rows, silently. These
/// pin the failure and both layers of the fix (script-side scrub + host-side
/// StripControlChars).
public class LocalPollerParseTests
{
    /// One node listener attributable (by ancestry) to pane1's shell at pid
    /// 100, with a control char smuggled into an UNRELATED process's command
    /// line — the poisoned bystander must not cost us the attributable server.
    private static string Payload(char bad) =>
        "{\"listeners\":[{\"port\":5173,\"pid\":200,\"addr\":\"127.0.0.1\"}]," +
        "\"procs\":[" +
        "{\"pid\":200,\"ppid\":100,\"name\":\"node.exe\",\"cmd\":\"node vite\",\"startMs\":1}," +
        $"{{\"pid\":300,\"ppid\":100,\"name\":\"weird.exe\",\"cmd\":\"weird {bad}arg\",\"startMs\":1}}" +
        "]}";

    [Fact]
    public void RawControlCharDefeatsTheStrictParser()
    {
        var poller = new LocalPoller();
        Assert.ThrowsAny<JsonException>(
            () => poller.Parse(Payload('\a'), Array.Empty<PaneProc>()));
    }

    [Fact]
    public void StripControlCharsHealsThePayloadAndKeepsAttribution()
    {
        var poller = new LocalPoller();
        var panes = new[] { new PaneProc(100, "pane1", "web", "working", null) };
        var got = poller.Parse(LocalPoller.StripControlChars(Payload('\a')), panes);
        var l = Assert.Single(got);
        Assert.Equal(5173, l.Port);
        Assert.Equal("pane1", l.OwnerPaneId);   // ancestry: node(200) → shell(100)
    }

    [Fact]
    public void StripControlCharsLeavesCleanJsonUntouched()
    {
        var s = Payload('x');
        Assert.Same(s, LocalPoller.StripControlChars(s));
    }
}
