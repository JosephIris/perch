using Xunit;

namespace Perch.Tests;

// The reaper that answers the 2026-09-06 session leak: seven agent panes
// restored at launch, untouched for 56 hours, holding 5.7 GB on an app that
// never restarted. Every rule below is a constraint from that incident's
// write-up, so a change that relaxes one should have to delete a test that
// says why it was there.
public class IdleReaperTests
{
    private static ReapView View(
        bool live = true, bool agent = true, bool active = false, bool dormant = false,
        bool busy = false, bool ports = false, bool parked = false, double idle = 99) =>
        new(Guid.NewGuid(), "tab", active, dormant, live, agent, busy, ports, parked, idle);

    // ---- Judge: what may and may not be slept -----------------------------

    [Fact]
    public void Sleeps_AnIdleAgentTabWithNothingGoingOn()
        => Assert.Equal(ReapVerdict.Sleep, IdleReaper.Judge(View(idle: 5), 4));

    [Fact]
    public void Never_TheTabYouAreLookingAt()
        => Assert.Equal(ReapVerdict.Active, IdleReaper.Judge(View(active: true), 4));

    // The axis that keeps PaneJob's deliberate design intact: a plain shell
    // you left a build running in is not an agent session and is never swept,
    // however long it has been quiet.
    [Fact]
    public void Never_APlainShellHoweverIdle()
        => Assert.Equal(ReapVerdict.NotAnAgent, IdleReaper.Judge(View(agent: false, idle: 500), 4));

    // "A server outliving its pane is exactly what the Local panel exists to
    // show you" — so a tab that is serving is off limits, agent or not.
    [Fact]
    public void Never_ATabServingAPort()
        => Assert.Equal(ReapVerdict.ServingPorts, IdleReaper.Judge(View(ports: true), 4));

    [Fact]
    public void Never_AnAgentMidTurn()
        => Assert.Equal(ReapVerdict.Busy, IdleReaper.Judge(View(busy: true), 4));

    // A bot with a room post queued is about to be busy, however quiet the
    // pane looks this second.
    [Fact]
    public void Never_ABotWithAPostWaiting()
        => Assert.Equal(ReapVerdict.Parked, IdleReaper.Judge(View(parked: true), 4));

    [Fact]
    public void Never_BeforeTheThresholdIsReached()
        => Assert.Equal(ReapVerdict.NotIdleYet, IdleReaper.Judge(View(idle: 3.9), 4));

    [Fact]
    public void Never_WhenTheSettingIsOff()
        => Assert.Equal(ReapVerdict.NotIdleYet, IdleReaper.Judge(View(idle: 500), 0));

    [Fact]
    public void Never_ATabWithNoLivePty()
        => Assert.Equal(ReapVerdict.NoPty, IdleReaper.Judge(View(live: false), 4));

    [Fact]
    public void Never_OneAlreadyAsleep()
        => Assert.Equal(ReapVerdict.AlreadyAsleep, IdleReaper.Judge(View(dormant: true), 4));

    // ---- Look: how idle is measured ---------------------------------------

    private sealed class Fake
    {
        public readonly List<Session> Sessions = new();
        public readonly HashSet<Guid> Live = new();
        public readonly Dictionary<Guid, long> Activity = new();
        public readonly List<Guid> Slept = new();
        public readonly List<Guid> Destroyed = new();
        public Guid? Active;

        public IdleReaper Reaper(double hours = 4) => new()
        {
            Sessions = () => Sessions,
            Leaves = s => Leaves(s.Root),
            HasPty = id => Live.Contains(id),
            LastActivityTicks = id => Activity.TryGetValue(id, out var t) ? t : null,
            ActiveSessionId = () => Active,
            Parked = _ => false,
            Sleep = id => Slept.Add(id),
            LivePaneIds = () => Live.ToList(),
            DestroyPane = id => { Destroyed.Add(id); Live.Remove(id); },
            IdleHours = hours,
        };

        private static IEnumerable<PaneNode> Leaves(PaneNode n)
        {
            if (n.IsLeaf) { yield return n; yield break; }
            foreach (var c in n.Children) foreach (var l in Leaves(c)) yield return l;
        }

        /// A session with one agent pane, idle for `idleHours`.
        public Session AddAgentTab(double idleHours, bool live = true)
        {
            var sess = new Session { Shell = "pwsh.exe" };
            sess.Root.AgentType = "claude";
            Sessions.Add(sess);
            if (live)
            {
                Live.Add(sess.Root.Id);
                Activity[sess.Root.Id] = System.Diagnostics.Stopwatch.GetTimestamp()
                    - (long)(idleHours * 3600 * System.Diagnostics.Stopwatch.Frequency);
            }
            return sess;
        }
    }

    [Fact]
    public void SweepIdle_SleepsThePastItAndLeavesTheRecent()
    {
        var f = new Fake();
        var old = f.AddAgentTab(idleHours: 9);
        var recent = f.AddAgentTab(idleHours: 0.5);
        Assert.Equal(1, f.Reaper().SweepIdle());
        Assert.Equal(new[] { old.Id }, f.Slept);
        Assert.DoesNotContain(recent.Id, f.Slept);
    }

    // One busy pane keeps its quiet siblings alive: sleep is a tab-level
    // action, so the tab is judged by its FRESHEST pane.
    [Fact]
    public void SweepIdle_ASplitIsAsIdleAsItsBusiestPane()
    {
        var f = new Fake();
        var sess = f.AddAgentTab(idleHours: 40);
        var busy = new PaneNode { AgentType = "claude" };
        var quiet = sess.Root;
        sess.Root = new PaneNode { Split = SplitOrientation.Vertical, Children = { quiet, busy } };
        f.Live.Add(busy.Id);
        f.Activity[busy.Id] = System.Diagnostics.Stopwatch.GetTimestamp();
        Assert.Equal(0, f.Reaper().SweepIdle());
    }

    // Leaving for the weekend with a tab open and coming back to it still
    // there is the point: the tab you are IN is never taken from you.
    [Fact]
    public void SweepIdle_NeverTheTabYouLeftOpenAndAreStillIn()
    {
        var f = new Fake();
        var sess = f.AddAgentTab(idleHours: 72);
        f.Active = sess.Id;
        Assert.Equal(0, f.Reaper().SweepIdle());
        Assert.Empty(f.Slept);
    }

    // A pane we have never clocked is "too new to judge", not "ancient".
    [Fact]
    public void SweepIdle_APaneWithNoReadingIsLeftAlone()
    {
        var f = new Fake();
        var sess = f.AddAgentTab(idleHours: 99);
        f.Activity.Remove(sess.Root.Id);
        Assert.Equal(0, f.Reaper().SweepIdle());
    }

    // ---- The forgotten-session case ---------------------------------------
    //
    // Five of the seven leaked sessions had NO record in sessions.json at all,
    // so a reaper that walked the store would have missed the majority of the
    // incident. This sweep walks the other way — from live PTYs back to tabs.

    [Fact]
    public void SweepOrphans_DestroysAPtyNoTabOwns()
    {
        var f = new Fake();
        f.AddAgentTab(idleHours: 1);
        var forgotten = Guid.NewGuid();
        f.Live.Add(forgotten);
        Assert.Equal(1, f.Reaper().SweepOrphans());
        Assert.Equal(new[] { forgotten }, f.Destroyed);
    }

    // A slept tab must own no PTY. One that does is the same leak wearing a
    // different hat, so the orphan sweep claims it too.
    [Fact]
    public void SweepOrphans_DestroysAPtyHangingOffASleptTab()
    {
        var f = new Fake();
        var sess = f.AddAgentTab(idleHours: 1);
        sess.Dormant = true;
        Assert.Equal(1, f.Reaper().SweepOrphans());
        Assert.Equal(new[] { sess.Root.Id }, f.Destroyed);
    }

    [Fact]
    public void SweepOrphans_LeavesAnOrdinaryLiveTabAlone()
    {
        var f = new Fake();
        f.AddAgentTab(idleHours: 1);
        Assert.Equal(0, f.Reaper().SweepOrphans());
        Assert.Empty(f.Destroyed);
    }
}
