using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Perch.Tests;

/// The pane pipe under the load a real hook puts on it: several messages,
/// each on its own connection, fired back to back with no pause.
///
/// This is the macOS shape of the bug "a Claude started from a project tab
/// never showed its session": the session-start hook sends agent, status,
/// session, name.reset and git.baseline in a burst; .NET's Unix pipe unlinks
/// the socket between one client and the next unless an extra server
/// instance holds it open (PerchIpcServer._anchor), and two or three of the
/// five were lost to that gap on every run. On Windows the same test passes
/// trivially — the kernel queues clients on the pipe name — which is the
/// point: one assertion, both hosts.
public class PerchIpcBurstTests
{
    /// Runs posted work inline — the tests have no UI thread.
    private sealed class InlineUi : IUiThread
    {
        public void Post(Action action) => action();
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => throw new NotSupportedException();
    }

    [Fact]
    public async Task EveryMessageOfABurstArrives()
    {
        var paneId = Guid.NewGuid();
        using var server = new PerchIpcServer(paneId, new InlineUi());
        var seen = new List<string>();
        var all = new SemaphoreSlim(0);
        server.OnAgent += m => { lock (seen) seen.Add("agent:" + m.Name); all.Release(); };
        server.OnStatus += m => { lock (seen) seen.Add("status:" + m.State); all.Release(); };
        server.OnSession += m => { lock (seen) seen.Add("session:" + m.Id); all.Release(); };
        server.OnNameReset += _ => { lock (seen) seen.Add("name.reset"); all.Release(); };
        server.OnGitBaseline += m => { lock (seen) seen.Add("git.baseline:" + m.Sha); all.Release(); };
        server.Start();
        await Task.Delay(150); // let the accept loop park on WaitForConnection

        // Exactly what `perch hooks claude session-start` sends, as fast as
        // it sends it: connect, write one line, close, next.
        var pipeName = $@"perch\{paneId:N}";
        var burst = new[]
        {
            "{\"type\":\"agent\",\"name\":\"claude\"}",
            "{\"type\":\"status\",\"state\":\"working\",\"detail\":\"claude started\"}",
            "{\"type\":\"session\",\"id\":\"abc-123\",\"agent\":\"claude\"}",
            "{\"type\":\"name.reset\",\"source\":\"startup\"}",
            "{\"type\":\"git.baseline\",\"sha\":\"deadbeef\"}",
        };
        for (var round = 0; round < 4; round++)
            foreach (var line in burst)
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                client.Connect(2000);
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
            }

        var expected = burst.Length * 4;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (seen.Count < expected && DateTime.UtcNow < deadline)
            await all.WaitAsync(TimeSpan.FromMilliseconds(250));

        lock (seen)
        {
            Assert.Equal(expected, seen.Count);
            Assert.Equal(4, seen.FindAll(s => s == "session:abc-123").Count);
            Assert.Equal(4, seen.FindAll(s => s == "status:working").Count);
        }
    }
}
