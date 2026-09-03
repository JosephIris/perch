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
    public void BoardRequest()
    {
        // Reuses PaneRef: a board.request carries nothing but the pane asking,
        // because WHICH board it means is a fact about the pane's session, not
        // something the page is allowed to assert.
        var m = Round<PaneRef>($"{{\"type\":\"board.request\",\"paneId\":\"{G1}\"}}");
        Assert.Equal(Guid.Parse(G1), m.PaneId);
    }

    [Fact]
    public void BoardNew()
    {
        var m = Round<PaneRef>($"{{\"type\":\"board.new\",\"paneId\":\"{G2}\"}}");
        Assert.Equal(Guid.Parse(G2), m.PaneId);
    }

    [Fact]
    public void BoardAdd()
    {
        var m = Round<BoardAddMsg>(
            $"{{\"type\":\"board.add\",\"paneId\":\"{G1}\",\"kind\":\"auto\",\"text\":\"src/a.ts\",\"x\":16,\"y\":32}}");
        Assert.Equal(("auto", "src/a.ts", 16d, 32d), (m.Kind, m.Text, m.X, m.Y));
        Assert.Null(m.Note);
    }

    [Fact]
    public void BoardEditAndPickFile()
    {
        var ed = Round<BoardEditMsg>(
            $"{{\"type\":\"board.edit\",\"paneId\":\"{G1}\",\"nodeId\":\"n4\",\"text\":\"after login\"}}");
        Assert.Equal((Guid.Parse(G1), "n4", "after login"), (ed.PaneId, ed.NodeId, ed.Text));

        var pick = Round<BoardPickFileMsg>(
            $"{{\"type\":\"board.pickFile\",\"paneId\":\"{G2}\",\"x\":16,\"y\":48}}");
        Assert.Equal((Guid.Parse(G2), 16d, 48d), (pick.PaneId, pick.X, pick.Y));
    }

    [Fact]
    public void BoardPaste_CarriesNoPayload()
    {
        // The absence of image bytes here is the design: the host reads the
        // clipboard itself rather than taking megabytes over the bridge.
        var m = Round<BoardPasteMsg>($"{{\"type\":\"board.paste\",\"paneId\":\"{G1}\",\"x\":8,\"y\":8}}");
        Assert.Equal((Guid.Parse(G1), 8d, 8d), (m.PaneId, m.X, m.Y));
    }

    [Fact]
    public void BoardMoveAndResize_FinalDefaultsTrue()
    {
        var mv = Round<BoardMoveMsg>(
            $"{{\"type\":\"board.move\",\"paneId\":\"{G1}\",\"nodeId\":\"n3\",\"x\":40,\"y\":80,\"final\":false}}");
        Assert.Equal(("n3", 40d, 80d, false), (mv.NodeId, mv.X, mv.Y, mv.Final));

        var rz = Round<BoardResizeMsg>(
            $"{{\"type\":\"board.resize\",\"paneId\":\"{G1}\",\"nodeId\":\"n3\",\"w\":300,\"h\":220,\"final\":true}}");
        Assert.Equal(("n3", 300d, 220d, true), (rz.NodeId, rz.W, rz.H, rz.Final));

        // Omitted `final` must default to TRUE, so a caller that forgets it
        // still persists rather than silently dropping the change.
        var noFinal = Round<BoardMoveMsg>(
            $"{{\"type\":\"board.move\",\"paneId\":\"{G1}\",\"nodeId\":\"n3\",\"x\":1,\"y\":2}}");
        Assert.True(noFinal.Final);
    }

    [Fact]
    public void BoardRemoveAndImage_ShareTheNodeRefShape()
    {
        var rm = Round<BoardNodeRefMsg>($"{{\"type\":\"board.remove\",\"paneId\":\"{G1}\",\"nodeId\":\"n2\"}}");
        Assert.Equal("n2", rm.NodeId);
        var im = Round<BoardNodeRefMsg>($"{{\"type\":\"board.image\",\"paneId\":\"{G2}\",\"nodeId\":\"n9\"}}");
        Assert.Equal((Guid.Parse(G2), "n9"), (im.PaneId, im.NodeId));
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
    public void SessionPair_CarriesBothIds()
    {
        var m = Round<SessionPairMsg>(
            $"{{\"type\":\"session.pair\",\"id\":\"{G1}\",\"partnerId\":\"{G2}\"}}");
        Assert.Equal(Guid.Parse(G1), m.Id);
        Assert.Equal(Guid.Parse(G2), m.PartnerId);
        // Unpair rides the plain SessionRef shape.
        Assert.Equal(Guid.Parse(G1), Round<SessionRef>($"{{\"type\":\"session.unpair\",\"id\":\"{G1}\"}}").Id);
    }

    // ---- The hook → host pipe side of cross-session messaging -------------

    [Fact]
    public void PeerMsg_PipeShapes()
    {
        // phase "sending" (PreToolUse): no verdict yet — Ok stays null.
        var sending = JsonSerializer.Deserialize<PeerMsgMessage>(
            "{\"type\":\"peer.msg\",\"phase\":\"sending\",\"target\":\"weekly-digest\",\"text\":\"users.name is now users.display_name\"}",
            IpcJson.Options)!;
        Assert.Equal("sending", sending.Phase);
        Assert.Equal("weekly-digest", sending.Target);
        Assert.Null(sending.Ok);

        // phase "sent" (PostToolUse): carries the delivered/failed verdict.
        var sent = JsonSerializer.Deserialize<PeerMsgMessage>(
            "{\"type\":\"peer.msg\",\"phase\":\"sent\",\"target\":\"weekly-digest\",\"text\":\"t\",\"ok\":false}",
            IpcJson.Options)!;
        Assert.False(sent.Ok);
        // The pre-team shape carries no body: Message/Summary default null
        // rather than failing the parse of an older hook binary.
        Assert.Null(sent.Message);
        Assert.Null(sent.Summary);

        // The team-room shape: the full body (newlines intact) and the
        // sender's own summary ride alongside the unchanged one-line cut.
        var full = JsonSerializer.Deserialize<PeerMsgMessage>(
            "{\"type\":\"peer.msg\",\"phase\":\"sent\",\"target\":\"bo\",\"text\":\"Schema done\",\"ok\":true,"
            + "\"message\":\"Schema done.\\nThe column is tenant_id.\",\"summary\":\"Schema done\"}",
            IpcJson.Options)!;
        Assert.True(full.Ok);
        Assert.Equal("Schema done.\nThe column is tenant_id.", full.Message);
        Assert.Equal("Schema done", full.Summary);
        Assert.Equal("Schema done", full.Text);
    }

    [Fact]
    public void TeamPost_PipeShape()
    {
        // `perch team post <text>` from inside a bot's pane: text only.
        var post = JsonSerializer.Deserialize<TeamPostMessage>(
            "{\"type\":\"team.post\",\"text\":\"Sidebar mockup is in design-loop/team.html\"}",
            IpcJson.Options)!;
        Assert.Equal("Sidebar mockup is in design-loop/team.html", post.Text);
        // A bare post (no text) still parses; the host drops it.
        Assert.Null(JsonSerializer.Deserialize<TeamPostMessage>("{\"type\":\"team.post\"}", IpcJson.Options)!.Text);
    }

    [Fact]
    public void SessionMessage_NameOptionalForOlderHooks()
    {
        // A hook binary predating peer names sends only the id — Name must
        // default null, not fail the parse.
        var old = JsonSerializer.Deserialize<SessionMessage>(
            "{\"type\":\"session\",\"id\":\"abc\"}", IpcJson.Options)!;
        Assert.Equal("abc", old.Id);
        Assert.Null(old.Name);

        var named = JsonSerializer.Deserialize<SessionMessage>(
            "{\"type\":\"session\",\"id\":\"abc\",\"name\":\"user-profiles\"}", IpcJson.Options)!;
        Assert.Equal("user-profiles", named.Name);
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
        Assert.Null(upd.Hidden);                     // absent → leave visibility alone
        Assert.True(Round<ProjectUpdateMsg>(
            $"{{\"type\":\"project.update\",\"id\":\"{G1}\",\"hidden\":true}}").Hidden);
        Assert.False(Round<ProjectUpdateMsg>(
            $"{{\"type\":\"project.update\",\"id\":\"{G1}\",\"hidden\":false}}").Hidden);
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
        Assert.Equal("cluster/batch-web-8f2c",
            Round<CloudDeleteMsg>("{\"type\":\"cloud.delete\",\"id\":\"cluster/batch-web-8f2c\"}").Id);
        Assert.Equal("us-central1-a/build-runner-h1",
            Round<CloudDeleteMsg>("{\"type\":\"cloud.delete\",\"id\":\"us-central1-a/build-runner-h1\"}").Id);
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

    // ---- team ------------------------------------------------------------
    // The team room and new-bot dialog, written exactly as team-room.ts /
    // new-bot-dialog.ts send them.

    [Fact]
    public void TeamRequest_SinceSeqOptional()
    {
        var m = Round<TeamRequestMsg>($"{{\"type\":\"team.request\",\"projectId\":\"{G1}\"}}");
        Assert.Equal(Guid.Parse(G1), m.ProjectId);
        Assert.Null(m.SinceSeq);
        Assert.Equal(41, Round<TeamRequestMsg>($"{{\"type\":\"team.request\",\"projectId\":\"{G1}\",\"sinceSeq\":41}}").SinceSeq);
    }

    [Fact]
    public void TeamPost_ToIsPolymorphic()
    {
        // An array of nicknames, the string "everyone", or null (unaddressed —
        // the host routes it). All three must land intact.
        var named = Round<TeamPostMsg>($"{{\"type\":\"team.post\",\"projectId\":\"{G1}\",\"text\":\"@Ada hi\",\"to\":[\"Ada\"],\"clientId\":\"c1\"}}");
        Assert.Equal("@Ada hi", named.Text);
        Assert.Equal("c1", named.ClientId);
        Assert.Equal(JsonValueKind.Array, named.To!.Value.ValueKind);
        Assert.Equal("Ada", named.To.Value[0].GetString());

        var all = Round<TeamPostMsg>($"{{\"type\":\"team.post\",\"projectId\":\"{G1}\",\"text\":\"x\",\"to\":\"everyone\",\"clientId\":\"c2\"}}");
        Assert.Equal("everyone", all.To!.Value.GetString());

        var routed = Round<TeamPostMsg>($"{{\"type\":\"team.post\",\"projectId\":\"{G1}\",\"text\":\"x\",\"to\":null,\"clientId\":\"c3\"}}");
        Assert.True(routed.To == null || routed.To.Value.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void TeamBotCreate_NewOrExistingPosition()
    {
        var fresh = Round<TeamBotCreateMsg>(
            $"{{\"type\":\"team.bot.create\",\"projectId\":\"{G1}\",\"nickname\":\"Ada\",\"worktree\":true," +
            "\"position\":{\"name\":\"Frontend dev\",\"purpose\":\"Owns src/web\",\"referencePath\":\"C:\\\\repo\",\"model\":\"sonnet\",\"brief\":\"## Role\\nYou own src/web.\"}}");
        Assert.Equal("Ada", fresh.Nickname);
        Assert.True(fresh.Worktree);
        Assert.Null(fresh.PositionSlug);
        Assert.Equal("Frontend dev", fresh.Position!.Name);
        Assert.Equal(@"C:\repo", fresh.Position.ReferencePath);
        Assert.Equal("## Role\nYou own src/web.", fresh.Position.Brief);

        var reuse = Round<TeamBotCreateMsg>(
            $"{{\"type\":\"team.bot.create\",\"projectId\":\"{G1}\",\"nickname\":\"Bo\",\"worktree\":false,\"positionSlug\":\"frontend-dev\"}}");
        Assert.Equal("frontend-dev", reuse.PositionSlug);
        Assert.False(reuse.Worktree);
        Assert.Null(reuse.Position);
        // perch test sends flags as strings.
        Assert.True(Round<TeamBotCreateMsg>(
            $"{{\"type\":\"team.bot.create\",\"projectId\":\"{G1}\",\"nickname\":\"Cy\",\"worktree\":\"true\",\"positionSlug\":\"x\"}}").Worktree);
    }

    [Fact]
    public void TeamBriefMessages()
    {
        var g = Round<TeamBriefGenerateMsg>(
            $"{{\"type\":\"team.brief.generate\",\"jobId\":\"j1\",\"projectId\":\"{G1}\",\"positionName\":\"Analyst\",\"purpose\":\"Reads the data\",\"referencePath\":\"C:\\\\repo\",\"model\":\"opus\"}}");
        Assert.Equal("j1", g.JobId);
        Assert.Equal("Analyst", g.PositionName);
        Assert.Equal("Reads the data", g.Purpose);
        Assert.Equal("opus", g.Model);
        Assert.Equal("j1", Round<TeamBriefCancelMsg>("{\"type\":\"team.brief.cancel\",\"jobId\":\"j1\"}").JobId);
    }

    [Fact]
    public void TeamPositionUpdate_PartialKeys()
    {
        var m = Round<TeamPositionUpdateMsg>(
            $"{{\"type\":\"team.position.update\",\"projectId\":\"{G1}\",\"slug\":\"analyst\",\"brief\":\"## Role\\nnew\"}}");
        Assert.Equal("analyst", m.Slug);
        Assert.Equal("## Role\nnew", m.Brief);
        Assert.Null(m.Purpose);
        Assert.Null(m.Name);
    }

    [Fact]
    public void TeamBotRemove_AndRoom_AndBrowse()
    {
        var r = Round<TeamBotRemoveMsg>(
            $"{{\"type\":\"team.bot.remove\",\"projectId\":\"{G1}\",\"botId\":\"bo\",\"closeTab\":true}}");
        Assert.Equal("bo", r.BotId);
        Assert.True(r.CloseTab);
        Assert.Null(r.RemoveWorktree);

        var room = Round<TeamRoomMsg>($"{{\"type\":\"team.room\",\"projectId\":\"{G1}\",\"open\":true}}");
        Assert.True(room.Open);

        var b = Round<TeamReferenceBrowseMsg>($"{{\"type\":\"team.reference.browse\",\"requestId\":\"r1\",\"projectId\":\"{G1}\"}}");
        Assert.Equal("r1", b.RequestId);
        Assert.Equal(Guid.Parse(G1), b.ProjectId);
    }

    [Fact]
    public void TeamMilestoneB_Verbs_RoundTrip()
    {
        var rename = Round<TeamTaskRenameMsg>($"{{\"type\":\"team.task.rename\",\"projectId\":\"{G1}\",\"taskId\":\"abcd1234\",\"title\":\"Dark footer\"}}");
        Assert.Equal("abcd1234", rename.TaskId);
        Assert.Equal("Dark footer", rename.Title);
        var confirm = Round<TeamTaskConfirmMsg>($"{{\"type\":\"team.task.confirm\",\"projectId\":\"{G1}\",\"taskId\":\"abcd1234\"}}");
        Assert.Equal("abcd1234", confirm.TaskId);
        var reject = Round<TeamTaskRejectMsg>($"{{\"type\":\"team.task.reject\",\"projectId\":\"{G1}\",\"taskId\":\"abcd1234\",\"note\":\"footer shifts\"}}");
        Assert.Equal("footer shifts", reject.Note);
        var perm = Round<TeamPermAnswerMsg>($"{{\"type\":\"team.perm.answer\",\"projectId\":\"{G1}\",\"id\":\"p1\",\"decision\":\"allow\"}}");
        Assert.Equal("allow", perm.Decision);
        var ask = Round<TeamAskAnswerMsg>($"{{\"type\":\"team.ask.answer\",\"projectId\":\"{G1}\",\"id\":\"q1\",\"answer\":\"Ship it\"}}");
        Assert.Equal("Ship it", ask.Answer);
        var image = Round<TeamImageMsg>($"{{\"type\":\"team.image\",\"projectId\":\"{G1}\",\"path\":\"C:\\\\shots\\\\a.png\"}}");
        Assert.Equal(@"C:\shots\a.png", image.Path);
        var react = Round<TeamReactMsg>($"{{\"type\":\"team.react\",\"projectId\":\"{G1}\",\"seq\":42,\"emoji\":\"✅\"}}");
        Assert.Equal(42, react.Seq);
        Assert.Equal("✅", react.Emoji);
    }

    [Fact]
    public void TeamMilestoneB_PipeShapes()
    {
        var ask = JsonSerializer.Deserialize<PermAskMessage>(
            "{\"type\":\"perm.ask\",\"id\":\"p1\",\"tool\":\"Bash\",\"summary\":\"rm -rf build\",\"input\":\"{\\\"command\\\":\\\"rm -rf build\\\"}\",\"suggestions\":[\"Bash(rm *)\"]}",
            IpcJson.Options)!;
        Assert.Equal("p1", ask.Id);
        Assert.Equal("Bash", ask.Tool);
        Assert.Equal("rm -rf build", ask.Summary);
        Assert.Equal("Bash(rm *)", Assert.Single(ask.Suggestions!));
        var denied = JsonSerializer.Deserialize<PermDeniedMessage>("{\"type\":\"perm.denied\",\"tool\":\"Bash\",\"summary\":\"curl x\"}", IpcJson.Options)!;
        Assert.Null(denied.Reason);
        var post = JsonSerializer.Deserialize<TeamPostMessage>("{\"type\":\"team.post\",\"text\":\"look\",\"image\":\"C:\\\\a.png\"}", IpcJson.Options)!;
        Assert.Equal(@"C:\a.png", post.Image);
        var old = JsonSerializer.Deserialize<TeamPostMessage>("{\"type\":\"team.post\",\"text\":\"look\"}", IpcJson.Options)!;
        Assert.Null(old.Image);
        var q = JsonSerializer.Deserialize<TeamAskMessage>("{\"type\":\"team.ask\",\"id\":\"q1\",\"text\":\"Ship?\",\"choices\":[\"Yes\",\"No\"]}", IpcJson.Options)!;
        Assert.Equal(2, q.Choices!.Length);
        var r = JsonSerializer.Deserialize<TeamReactMessage>("{\"type\":\"team.react\",\"target\":\"#12\",\"emoji\":\"👀\"}", IpcJson.Options)!;
        Assert.Equal("#12", r.Target);
        var task = JsonSerializer.Deserialize<TeamTaskMessage>("{\"type\":\"team.task\",\"op\":\"assign\",\"taskId\":\"abcd1234\",\"bot\":\"ada\",\"title\":\"x\"}", IpcJson.Options)!;
        Assert.Equal("abcd1234", task.TaskId);
        var legacyTask = JsonSerializer.Deserialize<TeamTaskMessage>("{\"type\":\"team.task\",\"op\":\"mine\",\"status\":\"done\"}", IpcJson.Options)!;
        Assert.Null(legacyTask.TaskId);
        var session = JsonSerializer.Deserialize<SessionMessage>("{\"type\":\"session\",\"id\":\"abc\",\"name\":\"ada\",\"socket\":\"uds:\\\\\\\\.\\\\pipe\\\\LOCAL\\\\cc-msg-1\"}", IpcJson.Options)!;
        Assert.Equal(@"uds:\\.\pipe\LOCAL\cc-msg-1", session.Socket);
    }

    [Fact]
    public void TeamPost_MayCarryAPastedPicture_AndPasteAsksTheHost()
    {
        var m = Round<TeamPostMsg>(
            $"{{\"type\":\"team.post\",\"projectId\":\"{G1}\",\"text\":\"\",\"to\":null,\"clientId\":\"c9\",\"image\":\"C:\\\\repo\\\\.perch\\\\team\\\\local\\\\images\\\\paste-1.png\"}}");
        Assert.Equal(@"C:\repo\.perch\team\local\images\paste-1.png", m.Image);
        Assert.Equal("", m.Text);
        Assert.Null(Round<TeamPostMsg>($"{{\"type\":\"team.post\",\"projectId\":\"{G1}\",\"text\":\"x\",\"to\":null,\"clientId\":\"c1\"}}").Image);
        Assert.Equal(Guid.Parse(G1), Round<TeamPasteMsg>($"{{\"type\":\"team.paste\",\"projectId\":\"{G1}\"}}").ProjectId);
    }

    [Fact]
    public void TeamBotAnswer_CarriesTheChoice()
    {
        var m = Round<TeamBotAnswerMsg>(
            $"{{\"type\":\"team.bot.answer\",\"projectId\":\"{G1}\",\"botId\":\"big-dawg\",\"answer\":\"trust\"}}");
        Assert.Equal(Guid.Parse(G1), m.ProjectId);
        Assert.Equal("big-dawg", m.BotId);
        Assert.Equal("trust", m.Answer);
    }

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
