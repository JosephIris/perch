using System;
using System.IO;
using System.Linq;
using Perch;
using Xunit;

namespace Perch.Tests;

/// The codex journal, read into the same rail Claude's fills.
///
/// Every line below is the real shape, taken from a rollout file codex-cli
/// 0.153.2 actually wrote (trimmed, and with the prose shortened). That matters
/// more than usual here: nothing documents this format, so the tests ARE the
/// specification, and a codex release that changes it should fail here rather
/// than quietly emptying the rail.
public class CodexTranscriptReaderTests
{
    private static string Line(string payload, string ts = "2026-09-04T07:10:06.887Z", string type = "event_msg")
        => $"{{\"timestamp\":\"{ts}\",\"type\":\"{type}\",\"payload\":{payload}}}";

    private const string UserMsg =
        "{\"type\":\"item_completed\",\"item\":{\"type\":\"UserMessage\",\"id\":\"u1\"," +
        "\"content\":[{\"type\":\"text\",\"text\":\"add a line to notes.md\"}]}}";

    // Note the capital "Text": codex spells the block type differently on the
    // agent side than the user side, which is why the reader never matches on it.
    private const string AgentMsg =
        "{\"type\":\"item_completed\",\"item\":{\"type\":\"AgentMessage\",\"id\":\"m1\"," +
        "\"content\":[{\"type\":\"Text\",\"text\":\"Appended it.\"}],\"phase\":\"commentary\"}}";

    private const string Command =
        "{\"type\":\"item_completed\",\"item\":{\"type\":\"CommandExecution\",\"id\":\"e1\"," +
        "\"command\":[\"C:\\\\Windows\\\\System32\\\\WindowsPowerShell\\\\v1.0\\\\powershell.exe\",\"-Command\",\"git status --short\"]," +
        "\"cwd\":\"file:///C:/tmp/lab\",\"parsed_cmd\":[{\"type\":\"unknown\",\"cmd\":\"git status --short\"}]," +
        "\"status\":\"completed\",\"stdout\":\"\"}}";

    private const string FileChange =
        "{\"type\":\"item_completed\",\"item\":{\"type\":\"FileChange\",\"id\":\"f1\",\"changes\":{" +
        "\"C:\\\\tmp\\\\lab\\\\notes.md\":{\"type\":\"update\",\"unified_diff\":\"@@\\n+line two\\n\"}," +
        "\"C:\\\\tmp\\\\lab\\\\NEW.md\":{\"type\":\"add\",\"content\":\"hi\"}}," +
        "\"status\":\"completed\"}}";

    private const string WebSearch =
        "{\"type\":\"item_completed\",\"item\":{\"type\":\"Extension\",\"kind\":\"web.search\",\"id\":\"x1\"," +
        "\"query\":\"codex hooks config\"}}";

    private const string Reasoning =
        "{\"type\":\"item_completed\",\"item\":{\"type\":\"Reasoning\",\"id\":\"r1\",\"summary_text\":[],\"raw_content\":[]}}";

    private const string Aborted =
        "{\"type\":\"turn_aborted\",\"turn_id\":\"t1\",\"reason\":\"interrupted\",\"duration_ms\":47702}";

    private const string TaskStarted =
        "{\"type\":\"task_started\",\"turn_id\":\"t1\",\"model_context_window\":258400}";

    private static string Usage(long input, long cached, long output) =>
        $"{{\"thread_id\":\"th\",\"usage\":{{\"input_tokens\":{input}}}," +
        $"\"thread_token_usage\":{{\"input_tokens\":{input},\"cached_input_tokens\":{cached}," +
        $"\"cache_write_input_tokens\":0,\"output_tokens\":{output},\"total_tokens\":{input + output}}}}}";

    private const string WorldState =
        "{\"full\":true,\"state\":{\"collaboration_mode\":{\"mode\":\"default\",\"model\":\"gpt-5.6-sol\"}}}";

    private static string Write(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"perch-codex-test-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void AConversationReadsAsPromptsBeatsAndWork()
    {
        var path = Write(
            Line(TaskStarted),
            Line(WorldState, type: "world_state"),
            Line(UserMsg),
            Line(Reasoning),
            Line(Command),
            Line(FileChange),
            Line(AgentMsg));
        try
        {
            var data = new CodexTranscriptReader().Read(Guid.NewGuid(), null, path);
            Assert.NotNull(data);
            var kinds = data!.Events.Select(e => e.Kind).ToArray();
            // Reasoning is dropped on purpose — it is most of the file and none
            // of the story, exactly as Claude's thinking blocks are.
            Assert.DoesNotContain("reasoning", kinds);
            Assert.Equal(new[] { "prompt", "work", "work", "work", "beat" }, kinds);

            Assert.Equal("add a line to notes.md", data.Events[0].Text);
            // The command reads as a person would say it, not as an argv array.
            Assert.Equal(("Run", "git status --short"), (data.Events[1].Verb, data.Events[1].Target));
            // One row per file, named by filename — a full path ellipsizes into
            // uselessness at the rail's width.
            Assert.Equal(("Edit", "notes.md"), (data.Events[2].Verb, data.Events[2].Target));
            Assert.Equal(("Write", "NEW.md"), (data.Events[3].Verb, data.Events[3].Target));
            Assert.Equal("Appended it.", data.Events[4].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TokenTotalsAreAssignedNotAccumulated()
    {
        // codex reports CUMULATIVE thread totals on every turn. Adding them
        // would multiply the count by the number of turns — the exact bug this
        // pins.
        var path = Write(
            Line(WorldState, type: "world_state"),
            Line(Usage(100, 40, 10), type: "token_usage_record"),
            Line(Usage(250, 90, 25), type: "token_usage_record"));
        try
        {
            var v = new CodexTranscriptReader().Read(Guid.NewGuid(), null, path)!.Vitals;
            Assert.NotNull(v);
            Assert.Equal((250, 25, 90), (v!.InputTokens, v.OutputTokens, v.CacheReadTokens));
            Assert.Equal("gpt-5.6-sol", v.Model);
            // No prices for these models, so no invented dollar figure.
            Assert.Equal(0.0, v.CostUsd);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AnInterruptedTurnIsAnAlarmNotSilence()
    {
        var path = Write(Line(UserMsg), Line(Aborted));
        try
        {
            var events = new CodexTranscriptReader().Read(Guid.NewGuid(), null, path)!.Events;
            Assert.Equal("interrupt", events[^1].Kind);
            Assert.Contains("interrupted", events[^1].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RepeatedIdenticalWorkFoldsIntoOneRowWithACount()
    {
        var path = Write(Line(Command), Line(Command), Line(Command), Line(AgentMsg));
        try
        {
            var events = new CodexTranscriptReader().Read(Guid.NewGuid(), null, path)!.Events;
            Assert.Equal(2, events.Count);
            Assert.Equal(3, events[0].Repeat);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ReadingTwiceOnlyParsesWhatWasAppended()
    {
        var path = Write(Line(UserMsg));
        try
        {
            var reader = new CodexTranscriptReader();
            var paneId = Guid.NewGuid();
            Assert.Single(reader.Read(paneId, null, path)!.Events);

            File.AppendAllText(path, Line(AgentMsg) + "\n");
            var again = reader.Read(paneId, null, path)!;
            Assert.Equal(2, again.Events.Count);          // not 3 — the first row isn't re-added
            Assert.Equal("beat", again.Events[1].Kind);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AHalfWrittenLineWaitsForTheRestOfItself()
    {
        // codex is appending while we read. A truncated final row must be left
        // for the next pass, not parsed as garbage.
        var path = Write(Line(UserMsg));
        try
        {
            var reader = new CodexTranscriptReader();
            var paneId = Guid.NewGuid();
            reader.Read(paneId, null, path);

            File.AppendAllText(path, "{\"timestamp\":\"x\",\"type\":\"event_ms");
            Assert.Single(reader.Read(paneId, null, path)!.Events);

            File.AppendAllText(path, "g\"}\n");            // still not a row we care about
            Assert.Single(reader.Read(paneId, null, path)!.Events);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OneUnparseableRowDoesNotTakeTheJournalDown()
    {
        var path = Write(Line(UserMsg), "{not json at all", Line(AgentMsg));
        try
        {
            var events = new CodexTranscriptReader().Read(Guid.NewGuid(), null, path)!.Events;
            Assert.Equal(new[] { "prompt", "beat" }, events.Select(e => e.Kind));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AWebSearchIsNamedInWords()
    {
        var path = Write(Line(WebSearch));
        try
        {
            var e = new CodexTranscriptReader().Read(Guid.NewGuid(), null, path)!.Events.Single();
            Assert.Equal(("Search", "codex hooks config"), (e.Verb, e.Target));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoFileMeansNoJournalRatherThanAnEmptyOne()
    {
        // Null, not an empty journal: the rail's "no agent" state is the honest
        // answer for a pane codex has not written for yet.
        Assert.Null(new CodexTranscriptReader().Read(Guid.NewGuid(), null, null));
        Assert.Null(new CodexTranscriptReader().Read(Guid.NewGuid(), "not-a-real-session-id", null));
    }
}
