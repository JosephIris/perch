using System.IO;
using System.Text.Json;
using Xunit;

namespace Perch.Tests;

// Project chat presets: `.perch/presets/<slug>.md` in the project's repo,
// listed for the page and put in full into the coordinator's and threads'
// prompts while switched on. Read fresh each time, so an edit is in force
// from the next prompt written.
[Collection("Data dir")]
public class PresetsTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), "perch-presets-" + Guid.NewGuid().ToString("N"));

    public PresetsTests() => Directory.CreateDirectory(Presets.Dir(_repo));

    public void Dispose()
    {
        try { Directory.Delete(_repo, true); } catch { }
    }

    private void Write(string slug, string text) => File.WriteAllText(Path.Combine(Presets.Dir(_repo), slug + ".md"), text);

    [Fact]
    public void List_IsEmpty_WhenTheFolderIsMissing()
    {
        Assert.Empty(Presets.List(Path.Combine(_repo, "nope")));
        Assert.Empty(Presets.Load(Path.Combine(_repo, "nope"), new[] { "x" }));
    }

    [Fact]
    public void List_TakesNameFromTheHeading_AndSummaryFromTheFirstProse_TopLevelMarkdownOnly()
    {
        Write("release-review", "# Release review\n\n_written by joseph_\n\nCheck every change against the release rules before it ships.\n\n## Steps\n- one\n");
        Write("bare", "No heading here, just prose.");
        Write("long", "# Long\n" + new string('x', 300));
        File.WriteAllText(Path.Combine(Presets.Dir(_repo), "notes.txt"), "not a preset");
        var helper = Path.Combine(Presets.Dir(_repo), "release-review");
        Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(helper, "inner.md"), "# Inner");

        var list = Presets.List(_repo);
        Assert.Equal(new[] { "bare", "long", "release-review" }, list.Select(p => p.Slug));
        var rr = list.Single(p => p.Slug == "release-review");
        Assert.Equal("Release review", rr.Name);
        Assert.Equal("Check every change against the release rules before it ships.", rr.Summary);
        Assert.Equal("bare", list[0].Name);
        Assert.Equal("No heading here, just prose.", list[0].Summary);
        Assert.True(list[1].Summary.Length <= 161);
    }

    [Fact]
    public void List_SeesAnEditedFile()
    {
        Write("a", "# First\n\nOne.");
        Assert.Equal("First", Presets.List(_repo)[0].Name);
        Write("a", "# Second name\n\nOne, now longer.");
        Assert.Equal("Second name", Presets.List(_repo)[0].Name);
    }

    [Fact]
    public void Load_ReadsFresh_SkipsMissing_AndRefusesPathsOutsideTheFolder()
    {
        Write("a", "# Alpha\n\nBody one.");
        var loaded = Presets.Load(_repo, new[] { "a", "gone", "../a", "A" });
        var only = Assert.Single(loaded);
        Assert.Equal("Alpha", only.Name);
        Assert.Equal("Body one.", only.Body);
        Assert.Equal(".perch/presets/a.md", only.RelPath);

        Write("a", "# Alpha\n\nBody two.");
        Assert.Equal("Body two.", Presets.Load(_repo, new[] { "a" })[0].Body);
    }

    [Fact]
    public void Body_IsCutAt24KB_WithANote()
    {
        var body = Presets.Body("# Big\n" + new string('y', Presets.MaxBodyChars + 500));
        Assert.StartsWith("yyy", body);
        Assert.Contains("was cut here", body);
        Assert.True(body.Length < Presets.MaxBodyChars + 200);
    }

    [Fact]
    public void Prompts_CarryEachPresetInFull_BeforeTheInstructions()
    {
        Write("release-review", "# Release review\n\nCheck the release rules.\n\n## Checklist\n- changelog entry\n- screenshots");
        var presets = Presets.Load(_repo, new[] { "release-review" });
        var proj = new Project { Name = "demo", Path = _repo };

        var coord = ThreadController.CoordinatorPrompt(proj, "Ship", "Use develop", null, presets).Replace("\r\n", "\n");
        Assert.Contains("## Mode: Release review\nCheck the release rules.", coord);
        Assert.Contains("- changelog entry\n- screenshots", coord);
        Assert.Contains("(Preset file: `.perch/presets/release-review.md` - it is re-read every turn.)", coord);
        Assert.True(coord.IndexOf("## Mode: Release review") < coord.IndexOf("## The user's instructions"));
        Assert.True(coord.IndexOf("## The goal") < coord.IndexOf("## Mode: Release review"));

        var thread = ThreadController.ThreadPrompt(1, "T", "Do it", "Use develop", null, presets).Replace("\r\n", "\n");
        Assert.Contains("## Mode: Release review\nCheck the release rules.", thread);
        Assert.Contains("- screenshots", thread);
        Assert.True(thread.IndexOf("## Mode: Release review") < thread.IndexOf("## The user's instructions"));
        Assert.Equal("Do it", ThreadController.BriefFromPrompt(thread));

        Assert.DoesNotContain("## Mode:", ThreadController.CoordinatorPrompt(proj));
    }

    [Fact]
    public void WriteCoordinatorPrompt_PicksUpAToggleAndAnEdit()
    {
        var dataDir = Path.Combine(_repo, "data");
        var prev = Environment.GetEnvironmentVariable("PERCH_DATA_DIR");
        Environment.SetEnvironmentVariable("PERCH_DATA_DIR", dataDir);
        try
        {
            Write("rr", "# Release review\n\nVersion one.");
            var proj = new Project { Name = "demo", Path = _repo };
            var lead = new Session { IsLead = true };
            var path = ThreadController.WriteCoordinatorPrompt(lead, proj);
            Assert.DoesNotContain("## Mode:", File.ReadAllText(path));

            lead.ChatPresets = new[] { "rr" };
            ThreadController.WriteCoordinatorPrompt(lead, proj);
            Assert.Contains("Version one.", File.ReadAllText(path));

            Write("rr", "# Release review\n\nVersion two.");
            ThreadController.WriteCoordinatorPrompt(lead, proj);
            Assert.Contains("Version two.", File.ReadAllText(path));

            File.Delete(Path.Combine(Presets.Dir(_repo), "rr.md"));
            ThreadController.WriteCoordinatorPrompt(lead, proj);
            Assert.DoesNotContain("## Mode:", File.ReadAllText(path));
        }
        finally { Environment.SetEnvironmentVariable("PERCH_DATA_DIR", prev); }
    }

    [Fact]
    public void Clean_KeepsValidSlugsOnce()
    {
        Assert.Equal(new[] { "a", "b-c" }, Presets.Clean(new[] { " a ", "b-c", "A", "..", "x/y", "" }));
        Assert.Empty(Presets.Clean(null));
    }

    [Fact]
    public void ChatPresets_SurviveTheStoreRoundTrip_AndDefaultEmpty()
    {
        var dto = new SessionStoreDto { Version = 3, Sessions = new List<Session> { new() { IsLead = true, ChatPresets = new[] { "release-review" } } } };
        var json = JsonSerializer.Serialize(dto, SessionStoreJsonContext.Default.SessionStoreDto);
        var back = JsonSerializer.Deserialize(json, SessionStoreJsonContext.Default.SessionStoreDto)!;
        Assert.Equal(new[] { "release-review" }, back.Sessions![0].ChatPresets);

        const string legacy = """{ "Version": 3, "Sessions": [ { "Title": "old chat", "IsLead": true } ] }""";
        var old = JsonSerializer.Deserialize(legacy, SessionStoreJsonContext.Default.SessionStoreDto)!;
        Assert.Empty(old.Sessions![0].ChatPresets);
    }
}
