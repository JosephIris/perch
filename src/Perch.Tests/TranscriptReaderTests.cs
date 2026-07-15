using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Perch;
using Xunit;

namespace Perch.Tests;

/// The Inspector's whole value rests on the projection being RIGHT: a journal
/// that quietly includes tool_result rows, or slash-command scaffolding, or a
/// subagent's chatter, is worse than no journal — it looks authoritative and
/// isn't. These pin the filters that make ~300 assistant rows readable as ~25
/// prose beats.
///
/// The reader resolves its own path via ClaudeTranscripts, which honors
/// CLAUDE_CONFIG_DIR — so each test builds a real .claude tree in a temp dir
/// and points the reader at it. That exercises the path rule too, rather than
/// stubbing past it.
public sealed class TranscriptReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "perch-tr-" + Guid.NewGuid().ToString("N"));
    private readonly string? _prevConfigDir =
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");

    private const string Cwd = @"C:\repo";
    private const string Sid = "11111111-2222-3333-4444-555555555555";

    public TranscriptReaderTests()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _prevConfigDir);
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// Write a transcript at exactly the path Claude Code would.
    private void WriteTranscript(params string[] rows)
    {
        var dir = Path.Combine(_root, "projects", ClaudeTranscripts.SanitizeCwd(Cwd));
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, Sid + ".jsonl"),
            string.Join("\n", rows) + "\n",
            new UTF8Encoding(false));
    }

    private static InspectorData Read(TranscriptReader r) =>
        r.Read(Guid.NewGuid(), Sid, Cwd)
        ?? throw new Xunit.Sdk.XunitException("reader found no transcript");

    // ---- Row shapes (as Claude Code actually writes them) -------------------

    // Built by concatenation rather than an interpolated raw string: the JSON is
    // brace-dense enough that the `{{`/`}}` interpolation delimiters collide with
    // it, and escaping our way out reads far worse than this does.
    //
    // Flat() matters: JSONL is ONE ROW PER LINE, but the block literals below are
    // written across several lines for legibility. Without this, each row would
    // arrive at the reader torn into fragments of invalid JSON.
    private static string Flat(string s) => s.Replace("\r", "").Replace("\n", " ");

    private static string Assistant(string blocks, string ts = "2026-07-03T19:32:00Z",
                                    string model = "claude-fable-5", string usage = "") =>
        "{\"type\":\"assistant\",\"timestamp\":\"" + ts + "\",\"message\":{\"model\":\"" + model +
        "\",\"content\":[" + Flat(blocks) + "]" + Flat(usage) + "}}";

    private static string UserText(string text, string ts = "2026-07-03T17:47:00Z") =>
        "{\"type\":\"user\",\"timestamp\":\"" + ts + "\",\"message\":{\"content\":\"" + text + "\"}}";

    private const string TextBlock = """{"type":"text","text":"Found it."}""";

    // ---- The filters that make the journal readable -------------------------

    [Fact]
    public void Beats_AreTheProse_ThinkingAndToolResultsAreNot()
    {
        WriteTranscript(
            UserText("fix the loc bug"),
            // A real assistant turn: thinking + prose + a tool call. Only the
            // prose is a beat; the thinking block is the single biggest slice of
            // a transcript and is exactly what the user asked NOT to see.
            Assistant("""
                {"type":"thinking","thinking":"Let me consider the untracked fold-in..."},
                {"type":"text","text":"Found it. The LOC isn't miscounted."},
                {"type":"tool_use","name":"Edit","input":{"file_path":"C:\\repo\\src\\GitProc.cs"}}
                """),
            // A tool_result comes back as a `user` row. It is NOT a user prompt;
            // treating it as one would put raw tool output in the journal as if
            // you had typed it.
            """
            {"type":"user","timestamp":"2026-07-03T19:33:00Z","message":{"content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}
            """);

        var d = Read(new TranscriptReader());

        Assert.Equal(
            new[] { "prompt", "beat", "work" },
            d.Events.Select(e => e.Kind));
        Assert.Equal("fix the loc bug", d.Events[0].Text);
        Assert.Equal("Found it. The LOC isn't miscounted.", d.Events[1].Text);
        // Target is the FILENAME — at 336px a full path ellipsizes into nothing.
        Assert.Equal("GitProc.cs", d.Events[2].Target);
        Assert.Equal("Edit", d.Events[2].Verb);
    }

    [Fact]
    public void SlashCommandScaffolding_IsNotAPrompt()
    {
        // Claude Code injects these as `user` rows. In a real 633-row transcript
        // they outnumber the actual typed prompts 11 to 1 — unfiltered, the
        // journal's turn headers are almost entirely noise.
        WriteTranscript(
            UserText("<command-name>/clear</command-name>"),
            UserText("<local-command-stdout>Set model to Fable 5</local-command-stdout>"),
            UserText("<local-command-caveat>Caveat: the messages below...</local-command-caveat>"),
            UserText("bogus LOC reported again"),
            Assistant(TextBlock));

        var d = Read(new TranscriptReader());

        var prompts = d.Events.Where(e => e.Kind == "prompt").ToList();
        Assert.Single(prompts);
        Assert.Equal("bogus LOC reported again", prompts[0].Text);
    }

    [Fact]
    public void InjectedUserRows_AreNotPrompts_OnlyTypedTurnsAre()
    {
        // Claude Code writes a lot of `user` rows the user never typed: image-paste
        // markers and skill/command bodies (isMeta), and task-completion
        // notifications / system turns (origin.kind != "human"). Rendered in full
        // they flood the journal — a single task-notification is ~8 KB of XML.
        // Only a genuinely typed turn becomes a prompt.
        WriteTranscript(
            // an image-paste marker
            """{"type":"user","isMeta":true,"timestamp":"2026-07-03T17:40:00Z","message":{"content":"[Image: source: C:\\img\\1.png]"}}""",
            // a task-completion notification injected as a user turn
            """{"type":"user","origin":{"kind":"task-notification"},"promptSource":"system","timestamp":"2026-07-03T17:41:00Z","message":{"content":"<task-notification>done</task-notification>"}}""",
            // a skill body injected under a tool use
            """{"type":"user","isMeta":true,"timestamp":"2026-07-03T17:42:00Z","message":{"content":"Approach this as the design lead at a small studio."}}""",
            // the one thing the user actually typed
            """{"type":"user","origin":{"kind":"human"},"promptSource":"typed","timestamp":"2026-07-03T17:47:00Z","message":{"content":"scope the loc to the agent edits"}}""",
            Assistant(TextBlock));

        var d = Read(new TranscriptReader());

        var prompts = d.Events.Where(e => e.Kind == "prompt").ToList();
        Assert.Single(prompts);
        Assert.Equal("scope the loc to the agent edits", prompts[0].Text);
    }

    [Fact]
    public void MissingOriginMetadata_ReadsAsHuman_SoOlderTranscriptsKeepTheirPrompts()
    {
        // Older transcripts carry no origin/promptSource/isMeta. A missing signal
        // must read as "human", or every prompt in an old transcript would vanish.
        WriteTranscript(UserText("older transcript prompt"), Assistant(TextBlock));

        var prompts = Read(new TranscriptReader()).Events.Where(e => e.Kind == "prompt").ToList();
        Assert.Single(prompts);
        Assert.Equal("older transcript prompt", prompts[0].Text);
    }

    [Fact]
    public void InterruptTurns_GetTheirOwnKind_NotPrompt()
    {
        // Esc / Ctrl-C records a "[Request interrupted …]" user turn. It's an
        // alarm, not a prompt — the rail paints it red — so it's classified
        // apart. Two variants: bare, and "…for tool use".
        WriteTranscript(
            UserText("do the thing"),
            UserText("[Request interrupted by user]"),
            UserText("[Request interrupted by user for tool use]"),
            Assistant(TextBlock));

        var d = Read(new TranscriptReader());

        Assert.Equal(
            new[] { "prompt", "interrupt", "interrupt", "beat" },
            d.Events.Select(e => e.Kind));
        // Text is preserved verbatim; the page drops the brackets at render time.
        Assert.Equal("[Request interrupted by user]", d.Events[1].Text);
    }

    [Fact]
    public void SubagentRows_AreSkipped()
    {
        // A fan-out session's sidechain rows are the MAJORITY of the file (129 of
        // 633 in the sample I measured). They'd bury the main thread's narrative,
        // and the coordinator's own `Task` tool_use already stands in for them.
        WriteTranscript(
            Assistant(TextBlock),
            """
            {"type":"assistant","isSidechain":true,"timestamp":"2026-07-03T19:32:00Z","message":{"model":"claude-fable-5","content":[{"type":"text","text":"subagent chatter"}]}}
            """);

        var d = Read(new TranscriptReader());

        Assert.Single(d.Events);
        Assert.Equal("Found it.", d.Events[0].Text);
    }

    // ---- The thrash signal --------------------------------------------------

    [Fact]
    public void ConsecutiveIdenticalCalls_CollapseIntoOneRowWithACount()
    {
        WriteTranscript(Assistant("""
            {"type":"tool_use","name":"Read","input":{"file_path":"/a/perch.log"}},
            {"type":"tool_use","name":"Read","input":{"file_path":"/a/perch.log"}},
            {"type":"tool_use","name":"Read","input":{"file_path":"/a/perch.log"}},
            {"type":"tool_use","name":"Bash","input":{"command":"dotnet test"}}
            """));

        var d = Read(new TranscriptReader());

        Assert.Equal(2, d.Events.Count);
        Assert.Equal("perch.log", d.Events[0].Target);
        Assert.Equal(3, d.Events[0].Repeat);            // ×3 — the thrash chip
        Assert.Equal("dotnet test", d.Events[1].Target);
        Assert.Equal(1, d.Events[1].Repeat);
    }

    [Fact]
    public void NonAdjacentRepeats_DoNotCollapse()
    {
        // Read → Edit → Read of the same file is two DIFFERENT looks at a file
        // that changed in between, not thrash. Merging them would invent a
        // repeat that never happened and cry wolf on a perfectly normal loop.
        WriteTranscript(Assistant("""
            {"type":"tool_use","name":"Read","input":{"file_path":"/a/x.cs"}},
            {"type":"tool_use","name":"Edit","input":{"file_path":"/a/x.cs"}},
            {"type":"tool_use","name":"Read","input":{"file_path":"/a/x.cs"}}
            """));

        var d = Read(new TranscriptReader());

        Assert.Equal(3, d.Events.Count);
        Assert.All(d.Events, e => Assert.Equal(1, e.Repeat));
    }

    // ---- Vitals -------------------------------------------------------------

    [Fact]
    public void Vitals_PriceTheTurnExactly_IncludingTheCacheTtlSplit()
    {
        // Fable 5: $10/1M in, $50/1M out. Cache reads bill at 0.1x input; cache
        // writes at 1.25x (5-minute TTL) or 2x (1-hour). The transcript reports
        // the TTL split, so we price it rather than assuming.
        WriteTranscript(Assistant(TextBlock, usage: """
            ,"usage":{"input_tokens":1000,"output_tokens":2000,"cache_read_input_tokens":100000,
                      "cache_creation_input_tokens":50000,
                      "cache_creation":{"ephemeral_5m_input_tokens":40000,"ephemeral_1h_input_tokens":10000}}
            """));

        var v = Read(new TranscriptReader()).Vitals;
        Assert.NotNull(v);

        // The vendor prefix is noise in a 336px rail; every model here is Claude.
        Assert.Equal("fable-5", v!.Model);

        //   input   1_000 × $10                 = $0.010
        //   5m     40_000 × $10 × 1.25 / 1e6    = $0.500
        //   1h     10_000 × $10 × 2.00 / 1e6    = $0.200
        //   read  100_000 × $10 × 0.10 / 1e6    = $0.100
        //   out     2_000 × $50        / 1e6    = $0.100
        Assert.Equal(0.91, v.CostUsd, precision: 4);

        // Context is what the model SAW this turn: fresh input + cache read +
        // cache write. It's the number that matters on a subscription, where
        // there's no bill — only headroom.
        Assert.Equal(151_000, v.ContextTokens);
        Assert.Equal(1_000_000, v.ContextMax);
    }

    [Fact]
    public void UnknownModel_ShowsNoCostRatherThanAMadeUpOne()
    {
        WriteTranscript(Assistant(TextBlock, model: "some-future-model", usage: """
            ,"usage":{"input_tokens":1000,"output_tokens":2000}
            """));

        var v = Read(new TranscriptReader()).Vitals;

        Assert.NotNull(v);
        Assert.Equal(0.0, v!.CostUsd);      // the page omits the chip entirely at 0
    }

    [Fact]
    public void HaikuGetsItsOwnContextCeiling()
    {
        // 200K, not 1M. A context bar that says "3% full" when you're actually
        // at 15% is worse than no bar.
        WriteTranscript(Assistant(TextBlock, model: "claude-haiku-4-5-20251001", usage: """
            ,"usage":{"input_tokens":30000,"output_tokens":100}
            """));

        var v = Read(new TranscriptReader()).Vitals;

        Assert.Equal(200_000, v!.ContextMax);
        Assert.Equal("haiku-4-5-20251001", v.Model);
    }

    // ---- Tailing ------------------------------------------------------------

    [Fact]
    public void SecondRead_ParsesOnlyTheAppendedRows_AndKeepsTheHistory()
    {
        var reader = new TranscriptReader();
        var pane = Guid.NewGuid();
        WriteTranscript(UserText("first"), Assistant(TextBlock));

        var before = reader.Read(pane, Sid, Cwd)!;
        Assert.Equal(2, before.Events.Count);

        // The agent appends. We must accumulate, not re-project from scratch and
        // not lose what came before.
        var dir = Path.Combine(_root, "projects", ClaudeTranscripts.SanitizeCwd(Cwd));
        File.AppendAllText(
            Path.Combine(dir, Sid + ".jsonl"),
            Assistant("""{"type":"text","text":"All 61 tests pass."}""") + "\n",
            new UTF8Encoding(false));

        var after = reader.Read(pane, Sid, Cwd)!;
        Assert.Equal(3, after.Events.Count);
        Assert.Equal("All 61 tests pass.", after.Events[2].Text);
    }

    [Fact]
    public void AHalfWrittenLastLine_IsLeftForTheNextRead_NotParsedAsGarbage()
    {
        // The agent appends to this file while we read it. A row that isn't
        // newline-terminated yet is INCOMPLETE JSON — parsing it would throw, and
        // consuming it would lose the row forever.
        var dir = Path.Combine(_root, "projects", ClaudeTranscripts.SanitizeCwd(Cwd));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Sid + ".jsonl");
        var reader = new TranscriptReader();
        var pane = Guid.NewGuid();

        File.WriteAllText(path, Assistant(TextBlock) + "\n" + """{"type":"assistant","mess""");

        var partial = reader.Read(pane, Sid, Cwd)!;
        Assert.Single(partial.Events);                    // the torn row is not counted

        // …and once it lands whole, it shows up.
        File.WriteAllText(path,
            Assistant(TextBlock) + "\n" +
            Assistant("""{"type":"text","text":"Now complete."}""") + "\n");

        var whole = reader.Read(pane, Sid, Cwd)!;
        Assert.Equal(2, whole.Events.Count);
        Assert.Equal("Now complete.", whole.Events[1].Text);
    }

    [Fact]
    public void NoTranscriptOnDisk_ReadsAsNull_NotAnEmptyJournal()
    {
        // A pane with no agent must show the empty state, not a zeroed-out
        // journal that looks like the agent did nothing.
        Assert.Null(new TranscriptReader().Read(Guid.NewGuid(), Sid, Cwd));
        Assert.Null(new TranscriptReader().Read(Guid.NewGuid(), null, Cwd));
        Assert.Null(new TranscriptReader().Read(Guid.NewGuid(), Sid, null));
    }
}
