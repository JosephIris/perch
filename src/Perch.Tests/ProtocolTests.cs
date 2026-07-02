using System.Text.Json;
using Xunit;

namespace Perch.Tests;

// The page → host wire contract. Each payload below is written exactly as
// bridge.ts / the control pipe emit it; if a DTO in PageMessages.cs drifts
// from the TS union, the corresponding test fails here instead of a button
// silently dying at runtime. Also covers the two deliberate leniencies
// (string numbers / string bools from `perch test`) and the loud-failure
// guarantee for missing required fields.
public class ProtocolTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static T Round<T>(string json) => PageJson.Deserialize<T>(Parse(json));

    private const string G1 = "11111111-2222-3333-4444-555555555555";
    private const string G2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    // ---- One test per wire message, payload as the page sends it ----------

    [Fact]
    public void PaneIn()
    {
        var m = Round<PaneInMsg>($"{{\"type\":\"pane.in\",\"paneId\":\"{G1}\",\"b64\":\"aGk=\"}}");
        Assert.Equal(Guid.Parse(G1), m.PaneId);
        Assert.Equal("aGk=", m.B64);
    }

    [Fact]
    public void PaneAck()
    {
        var m = Round<PaneAckMsg>($"{{\"type\":\"pane.ack\",\"paneId\":\"{G1}\",\"bytes\":65536}}");
        Assert.Equal(65536, m.Bytes);
    }

    [Fact]
    public void PaneResize()
    {
        var m = Round<PaneResizeMsg>($"{{\"type\":\"pane.resize\",\"paneId\":\"{G1}\",\"cols\":120,\"rows\":30}}");
        Assert.Equal((120, 30), (m.Cols, m.Rows));
    }

    [Fact]
    public void PaneSplit_UrlOptional()
    {
        var plain = Round<PaneSplitMsg>($"{{\"type\":\"pane.split\",\"paneId\":\"{G1}\",\"dir\":\"down\"}}");
        Assert.Equal("down", plain.Dir);
        Assert.Null(plain.Url);

        var web = Round<PaneSplitMsg>($"{{\"type\":\"pane.split\",\"paneId\":\"{G1}\",\"dir\":\"right\",\"url\":\"https://x.dev\"}}");
        Assert.Equal("https://x.dev", web.Url);
    }

    [Fact]
    public void PaneChooserChoose()
    {
        var m = Round<PaneChooserChooseMsg>($"{{\"type\":\"pane.chooser.choose\",\"paneId\":\"{G1}\",\"choice\":\"agent\"}}");
        Assert.Equal("agent", m.Choice);
    }

    [Fact]
    public void ResizeSplit_MidDragAndFinal()
    {
        var mid = Round<ResizeSplitMsg>($"{{\"type\":\"pane.resizeSplit\",\"splitId\":\"{G1}\",\"weights\":[1.5,0.5],\"final\":false}}");
        Assert.Equal(new[] { 1.5, 0.5 }, mid.Weights);
        Assert.False(mid.Final);

        var end = Round<ResizeSplitMsg>($"{{\"type\":\"pane.resizeSplit\",\"splitId\":\"{G1}\",\"weights\":[1,1]}}");
        Assert.Null(end.Final);   // omitted == final (handler treats != false as final)
    }

    [Fact]
    public void PaneMove()
    {
        var m = Round<PaneMoveMsg>($"{{\"type\":\"pane.move\",\"src\":\"{G1}\",\"target\":\"{G2}\",\"edge\":\"center\"}}");
        Assert.Equal(Guid.Parse(G2), m.Target);
        Assert.Equal("center", m.Edge);
    }

    [Fact]
    public void PaneMoveDir()
    {
        var m = Round<PaneMoveDirMsg>($"{{\"type\":\"pane.moveDir\",\"paneId\":\"{G1}\",\"dir\":\"up\"}}");
        Assert.Equal("up", m.Dir);
    }

    [Fact]
    public void PaneRenameRecolorCwd()
    {
        Assert.Equal("api fix", Round<PaneRenameMsg>($"{{\"paneId\":\"{G1}\",\"name\":\"api fix\"}}").Name);
        Assert.Equal(4, Round<PaneRecolorMsg>($"{{\"paneId\":\"{G1}\",\"colorIndex\":4}}").ColorIndex);
        Assert.Equal(@"C:\repo", Round<PaneCwdMsg>($"{{\"paneId\":\"{G1}\",\"cwd\":\"C:\\\\repo\"}}").Cwd);
    }

    [Fact]
    public void UrlPaneLayout()
    {
        var m = Round<UrlPaneLayoutMsg>(
            $"{{\"type\":\"urlpane.layout\",\"paneId\":\"{G1}\",\"url\":\"https://x.dev\",\"x\":10.5,\"y\":0,\"w\":800,\"h\":600}}");
        Assert.Equal((10.5, 0d, 800d, 600d), (m.X, m.Y, m.W, m.H));
    }

    [Fact]
    public void SessionMessages()
    {
        Assert.Null(Round<SessionNewMsg>("{\"type\":\"session.new\"}").Shell);
        Assert.Equal("pwsh.exe", Round<SessionNewMsg>("{\"type\":\"session.new\",\"shell\":\"pwsh.exe\"}").Shell);
        Assert.Equal(Guid.Parse(G1), Round<SessionRef>($"{{\"type\":\"session.select\",\"id\":\"{G1}\"}}").Id);
        Assert.Equal("release prep", Round<SessionRenameMsg>($"{{\"id\":\"{G1}\",\"title\":\"release prep\"}}").Title);
    }

    [Fact]
    public void ResumeDecision_MissingAcceptDegradesToNull()
    {
        Assert.True(Round<ResumeDecisionMsg>("{\"type\":\"resume.decision\",\"accept\":true}").Accept);
        // Absent accept must NOT throw — the handler treats null as declined,
        // releasing parked spawns as plain shells instead of parking forever.
        Assert.Null(Round<ResumeDecisionMsg>("{\"type\":\"resume.decision\"}").Accept);
    }

    [Fact]
    public void SettingsSave_AllFieldsOptional()
    {
        var m = Round<SettingsSaveMsg>("{\"type\":\"settings.save\",\"fontSize\":16,\"resumeAgentsOnLaunch\":false}");
        Assert.Equal(16, m.FontSize);
        Assert.False(m.ResumeAgentsOnLaunch);
        Assert.Null(m.DefaultShell);
    }

    // ---- Control-pipe leniencies (perch test ships flags as strings) ------

    [Fact]
    public void StringNumbersAndBools_AcceptedForControlPipe()
    {
        Assert.Equal(14, Round<PrefsSetMsg>("{\"fontSize\":\"14\"}").FontSize);
        Assert.True(Round<SettingsSaveMsg>("{\"resumeAgentsOnLaunch\":\"true\"}").ResumeAgentsOnLaunch);
        Assert.True(Round<ResumeDecisionMsg>("{\"accept\":\"true\"}").Accept);
    }

    // ---- Loud failure: protocol drift must throw, not default -------------

    [Fact]
    public void MissingRequiredField_Throws()
    {
        // pane.in without b64 — before typing, this silently no-op'd.
        Assert.Throws<JsonException>(() => Round<PaneInMsg>($"{{\"paneId\":\"{G1}\"}}"));
        // pane.move without edge.
        Assert.Throws<JsonException>(() => Round<PaneMoveMsg>($"{{\"src\":\"{G1}\",\"target\":\"{G2}\"}}"));
    }

    [Fact]
    public void MalformedGuid_Throws()
    {
        Assert.ThrowsAny<Exception>(() => Round<PaneRef>("{\"paneId\":\"not-a-guid\"}"));
    }

    // ---- Router mechanics ---------------------------------------------------

    [Fact]
    public void Router_DispatchesTypedAndPayloadless()
    {
        PaneRef? seen = null;
        var readyCount = 0;
        var router = new MessageRouter()
            .Add("ready", () => readyCount++)
            .Add<PaneRef>("pane.focus", m => seen = m);

        Assert.True(router.Dispatch("ready", Parse("{\"type\":\"ready\"}")));
        Assert.True(router.Dispatch("pane.focus", Parse($"{{\"paneId\":\"{G1}\"}}")));
        Assert.Equal(1, readyCount);
        Assert.Equal(Guid.Parse(G1), seen!.PaneId);
    }

    [Fact]
    public void Router_UnknownTypeReturnsFalse()
    {
        var router = new MessageRouter().Add("ready", () => { });
        Assert.False(router.Dispatch("nope", Parse("{}")));
    }

    [Fact]
    public void Router_BadPayloadThrowsOutOfDispatch()
    {
        var router = new MessageRouter().Add<PaneInMsg>("pane.in", _ => { });
        Assert.Throws<JsonException>(() => router.Dispatch("pane.in", Parse($"{{\"paneId\":\"{G1}\"}}")));
    }
}
