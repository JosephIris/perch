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
    public void PaneModel()
    {
        var m = Round<PaneModelMsg>($"{{\"type\":\"pane.model\",\"paneId\":\"{G1}\",\"model\":\"opus\"}}");
        Assert.Equal(Guid.Parse(G1), m.PaneId);
        Assert.Equal("opus", m.Model);
        // Empty string is the "account default" selection — must round-trip, not
        // fail the required-field guard (it's present, just empty).
        Assert.Equal("", Round<PaneModelMsg>($"{{\"type\":\"pane.model\",\"paneId\":\"{G1}\",\"model\":\"\"}}").Model);
    }

    [Fact]
    public void PaneProbe_EachObservationTravelsAlone()
    {
        // The page sends exactly one observation per message; the absent field
        // must read null (not a defaulted false, which would LOOK like a
        // "dialog gone" report and demote a blocked pane).
        var perm = Round<PaneProbeMsg>(
            $"{{\"type\":\"pane.probe\",\"paneId\":\"{G1}\",\"permissionVisible\":false}}");
        Assert.Equal(Guid.Parse(G1), perm.PaneId);
        Assert.False(perm.PermissionVisible);
        Assert.Null(perm.BlockedVisible);

        var blocked = Round<PaneProbeMsg>(
            $"{{\"type\":\"pane.probe\",\"paneId\":\"{G1}\",\"blockedVisible\":true}}");
        Assert.Null(blocked.PermissionVisible);
        Assert.True(blocked.BlockedVisible);
    }

    [Fact]
    public void UrlPaneLayout()
    {
        var m = Round<UrlPaneLayoutMsg>(
            $"{{\"type\":\"urlpane.layout\",\"paneId\":\"{G1}\",\"url\":\"https://x.dev\",\"x\":10.5,\"y\":0,\"w\":800,\"h\":600}}");
        Assert.Equal((10.5, 0d, 800d, 600d), (m.X, m.Y, m.W, m.H));
    }

    [Fact]
    public void UrlPaneVisible()
    {
        var m = Round<UrlPaneVisibleMsg>($"{{\"type\":\"urlpane.visible\",\"paneId\":\"{G1}\",\"visible\":false}}");
        Assert.Equal(Guid.Parse(G1), m.PaneId);
        Assert.False(m.Visible);
    }

    [Fact]
    public void WebPanesSuppress()
    {
        Assert.True(Round<WebPanesSuppressMsg>("{\"type\":\"ui.webpanes.suppress\",\"suppress\":true}").Suppress);
        Assert.False(Round<WebPanesSuppressMsg>("{\"type\":\"ui.webpanes.suppress\",\"suppress\":false}").Suppress);
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
    public void ProjectMessages()
    {
        // session.new carries an optional projectId — absent on the plain "New
        // session" button, present when a tab is created from a project header.
        Assert.Null(Round<SessionNewMsg>("{\"type\":\"session.new\"}").ProjectId);
        Assert.Equal(
            Guid.Parse(G1),
            Round<SessionNewMsg>($"{{\"type\":\"session.new\",\"projectId\":\"{G1}\"}}").ProjectId);

        Assert.Equal(@"C:\src\repo", Round<ProjectAddMsg>(
            "{\"type\":\"project.add\",\"path\":\"C:\\\\src\\\\repo\"}").Path);
        Assert.Equal("repo", Round<ProjectAddMsg>(
            "{\"type\":\"project.add\",\"path\":\"C:\\\\src\\\\r\",\"name\":\"repo\"}").Name);
        Assert.Equal(Guid.Parse(G1), Round<ProjectRef>($"{{\"type\":\"project.remove\",\"id\":\"{G1}\"}}").Id);
        Assert.Equal("projects", Round<UiModeMsg>("{\"type\":\"ui.mode\",\"mode\":\"projects\"}").Mode);

        var tab = Round<ProjectTabNewMsg>(
            $"{{\"type\":\"project.tab.new\",\"projectId\":\"{G1}\",\"name\":\"loc diff fix\",\"agent\":\"claude\",\"worktree\":true}}");
        Assert.Equal("loc diff fix", tab.Name);
        Assert.Equal("claude", tab.Agent);
        Assert.True(tab.Worktree);
        // model is optional: absent → null (account default), present → the alias.
        Assert.Null(tab.Model);
        Assert.Equal("opus", Round<ProjectTabNewMsg>(
            $"{{\"type\":\"project.tab.new\",\"projectId\":\"{G1}\",\"name\":\"t\",\"agent\":\"claude\",\"worktree\":false,\"model\":\"opus\"}}").Model);

        var upd = Round<ProjectUpdateMsg>(
            $"{{\"type\":\"project.update\",\"id\":\"{G1}\",\"seedPaths\":[\"src/web/node_modules\"]}}");
        Assert.Null(upd.Name);                       // absent → leave the name alone
        Assert.Equal(new[] { "src/web/node_modules" }, upd.SeedPaths);
    }

    [Fact]
    public void CloudMessages_RoundTrip()
    {
        Assert.True(Round<CloudPanelMsg>("{\"type\":\"cloud.panel\",\"open\":true}").Open);
        Assert.False(Round<CloudPanelMsg>("{\"type\":\"cloud.panel\",\"open\":false}").Open);

        // The id is the host's stable key, NOT a bare resource name. A VM and a
        // Dataproc cluster take different gcloud delete commands, and the "kind/"
        // prefix is what keeps them apart — deleting a cluster as if it were a VM
        // kills the master and strands the workers, still billing.
        Assert.Equal("cluster/dp-audience-8f2c",
            Round<CloudDeleteMsg>("{\"type\":\"cloud.delete\",\"id\":\"cluster/dp-audience-8f2c\"}").Id);
        Assert.Equal("us-central1-a/gpu-train-h1",
            Round<CloudDeleteMsg>("{\"type\":\"cloud.delete\",\"id\":\"us-central1-a/gpu-train-h1\"}").Id);
    }

    [Fact]
    public void LocalMessages_RoundTrip()
    {
        Assert.True(Round<LocalPanelMsg>("{\"type\":\"local.panel\",\"open\":true}").Open);
        Assert.False(Round<LocalPanelMsg>("{\"type\":\"local.panel\",\"open\":false}").Open);

        // Kill targets an exact pid, never a process name — a stale dev server
        // and a real service routinely share the name "node.exe".
        Assert.Equal(8821, Round<LocalKillMsg>("{\"type\":\"local.kill\",\"pid\":8821}").Pid);
        // Open targets a port; the URL is built host-side.
        Assert.Equal(5173, Round<LocalOpenMsg>("{\"type\":\"local.open\",\"port\":5173}").Port);
    }

    [Fact]
    public void SessionClose_RemoveWorktreeIsOptIn()
    {
        // Absent → null → the worktree is KEPT and the session is restorable.
        // Deleting someone's worktree folder must never be the default reading of
        // a plain close.
        Assert.Null(Round<SessionCloseMsg>($"{{\"type\":\"session.close\",\"id\":\"{G1}\"}}").RemoveWorktree);
        Assert.True(Round<SessionCloseMsg>(
            $"{{\"type\":\"session.close\",\"id\":\"{G1}\",\"removeWorktree\":true}}").RemoveWorktree);
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
        // Absent → null, which the handler reads as "leave scan roots alone".
        // An empty array is the distinct, deliberate "clear them".
        Assert.Null(m.ProjectScanRoots);
    }

    [Fact]
    public void SettingsSave_CarriesProjectScanRoots()
    {
        var m = Round<SettingsSaveMsg>(
            "{\"type\":\"settings.save\",\"projectScanRoots\":[\"C:\\\\src\",\"D:\\\\work\"]}");
        Assert.Equal(new[] { @"C:\src", @"D:\work" }, m.ProjectScanRoots);
        Assert.Empty(Round<SettingsSaveMsg>(
            "{\"type\":\"settings.save\",\"projectScanRoots\":[]}").ProjectScanRoots!);
    }

    // ---- Inspector rail ---------------------------------------------------

    [Fact]
    public void InspectorRequest_RoundTrips()
    {
        // inspector.request reuses PaneRef — same shape as commits.request.
        Assert.Equal(Guid.Parse(G1), Round<PaneRef>($"{{\"type\":\"inspector.request\",\"paneId\":\"{G1}\"}}").PaneId);
    }

    [Fact]
    public void PrefsSet_InspectorOpen_IsIndependentOfFontSize()
    {
        // Both fields are nullable so the page can update one without asserting
        // the other — a font-size bump must not silently reopen a collapsed rail.
        var railOnly = Round<PrefsSetMsg>("{\"type\":\"prefs.set\",\"inspectorOpen\":false}");
        Assert.False(railOnly.InspectorOpen);
        Assert.Null(railOnly.FontSize);

        var fontOnly = Round<PrefsSetMsg>("{\"type\":\"prefs.set\",\"fontSize\":15}");
        Assert.Equal(15, fontOnly.FontSize);
        Assert.Null(fontOnly.InspectorOpen);
    }

    [Fact]
    public void PrefsSet_WideLayout_IsIndependentOfTheOtherPrefs()
    {
        // The width-mode toggle sends only wideLayout — it must not disturb the
        // font size or the rail's open state riding in the same pref bag.
        var wideOnly = Round<PrefsSetMsg>("{\"type\":\"prefs.set\",\"wideLayout\":true}");
        Assert.True(wideOnly.WideLayout);
        Assert.Null(wideOnly.FontSize);
        Assert.Null(wideOnly.InspectorOpen);

        // And it round-trips as a string over the control pipe, like the others.
        Assert.True(Round<PrefsSetMsg>("{\"wideLayout\":\"true\"}").WideLayout);
    }

    [Fact]
    public void PrefsSet_LocalPerchOnly_IsIndependentOfTheOtherPrefs()
    {
        // The local panel's "Perch only" filter sends only localPerchOnly — it must
        // not disturb the other prefs riding in the same bag.
        var perchOnly = Round<PrefsSetMsg>("{\"type\":\"prefs.set\",\"localPerchOnly\":true}");
        Assert.True(perchOnly.LocalPerchOnly);
        Assert.Null(perchOnly.FontSize);
        Assert.Null(perchOnly.InspectorOpen);
        Assert.Null(perchOnly.WideLayout);

        // And it round-trips as a string over the control pipe, like the others.
        Assert.True(Round<PrefsSetMsg>("{\"localPerchOnly\":\"true\"}").LocalPerchOnly);
    }

    [Fact]
    public void SidebarReorder_RoundTrips()
    {
        var m = Round<SidebarReorderMsg>(
            "{\"type\":\"sidebar.reorder\",\"kind\":\"tab\",\"movedId\":\"a\",\"targetId\":\"b\",\"edge\":\"after\"}");
        Assert.Equal("tab", m.Kind);
        Assert.Equal("a", m.MovedId);
        Assert.Equal("b", m.TargetId);
        Assert.Equal("after", m.Edge);
    }

    // ---- Control-pipe leniencies (perch test ships flags as strings) ------

    [Fact]
    public void StringNumbersAndBools_AcceptedForControlPipe()
    {
        Assert.Equal(14, Round<PrefsSetMsg>("{\"fontSize\":\"14\"}").FontSize);
        Assert.True(Round<PrefsSetMsg>("{\"inspectorOpen\":\"true\"}").InspectorOpen);
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
