using Xunit;

namespace Perch.Tests;

// What waits for a project chat — the coordinator's queue, its turn in
// progress, lines for a thread — survives Perch closing. A turn cut off comes
// back first and flagged, since it may be half done; a line already typed
// into a thread is never sent twice.
public class ChatQueueRestoreTests
{
    [Fact]
    public void Restored_CutTurnFirstAndFlaggedThenTheQueue()
    {
        var list = ChatController.Restored(new ChatController.QueueFile(new() { "[Perch] Thread 2 finished.", " " }, "Merge #1 and #2"));
        Assert.Equal(2, list.Count);
        Assert.StartsWith("[Perch] Perch closed while you were working on the turn below", list[0]);
        Assert.EndsWith("Merge #1 and #2", list[0]);
        Assert.Equal("[Perch] Thread 2 finished.", list[1]);
        Assert.Empty(ChatController.Restored(new ChatController.QueueFile(new(), null)));
    }

    [Fact]
    public void Unsent_LeavesOutALineAlreadyTyped()
    {
        var sess = new Session();
        var busy = false;
        var d = new LineDelivery(new LineDelivery.Host
        {
            SessionById = id => id == sess.Id ? sess : null,
            ClaudeUp = _ => true,
            Busy = _ => busy,
            Type = (_, _) => true,
            PressEnter = _ => true,
            Delay = (_, _) => { },
            EnsureRunning = _ => { },
        });
        d.Enqueue(sess.Id, "first\nwith two lines");   // typed at once
        busy = true;
        d.Enqueue(sess.Id, "second");                   // waits
        Assert.Equal(new[] { "second" }, d.Unsent(sess.Id));
        Assert.Empty(d.Unsent(Guid.NewGuid()));
    }
}
