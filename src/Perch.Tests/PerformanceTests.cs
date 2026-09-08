using System.Text;
using System.IO;
using Xunit;

namespace Perch.Tests;

public sealed class PerformanceTests
{
    private sealed class BlockingPty : IPty
    {
        public event EventHandler<ReadOnlyMemory<byte>>? OutputReceived { add { } remove { } }
        public event EventHandler<int>? Exited { add { } remove { } }
        public int ProcessId => 1;
        public long MaxOutstanding => 0;
        public IProcScope? Scope => null;
        public readonly ManualResetEventSlim Entered = new(), Release = new();
        public readonly List<byte> Written = new();
        public void Write(ReadOnlySpan<byte> bytes)
        {
            Entered.Set();
            Assert.True(Release.Wait(TimeSpan.FromSeconds(5)));
            lock (Written) Written.AddRange(bytes.ToArray());
        }
        public void Ack(long bytes) { }
        public void Resize(int cols, int rows) { }
        public void Dispose() { Release.Set(); }
    }

    [Fact]
    public async Task BlockedInputReturnsImmediatelyAndAllCallersShareOrdering()
    {
        var native = new BlockingPty();
        using var pty = new QueuedPty(native);
        var first = pty.WriteAsync(new byte[] { 1, 2 });
        Assert.True(native.Entered.Wait(2000));
        Assert.False(first.IsCompleted);
        pty.Write(new byte[] { 3 }); // team/system input must not overtake keyboard
        var last = pty.WriteAsync(new byte[] { 4, 5 });
        Assert.False(last.IsCompleted);
        native.Release.Set();
        await last.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, native.Written);
    }

    [Fact]
    public async Task CloseCancelsQueuedInputAndDoesNotReplayItIntoAnotherPty()
    {
        var native = new BlockingPty();
        var pty = new QueuedPty(native);
        var first = pty.WriteAsync(new byte[] { 1 });
        Assert.True(native.Entered.Wait(2000));
        var queued = pty.WriteAsync(new byte[] { 2 });
        pty.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(new byte[] { 1 }, native.Written);
    }

    private sealed class Ui : IUiThread
    {
        public readonly Queue<Action> Pending = new();
        public void Post(Action action) => Pending.Enqueue(action);
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public IUiTimer CreateTimer(TimeSpan interval, Action tick) => throw new NotSupportedException();
    }

    [Fact]
    public void OutputBurstsCoalesceAndYieldWithExactByteOrdering()
    {
        var ui = new Ui();
        var id = Guid.NewGuid();
        var other = Guid.NewGuid();
        var output = new List<(Guid Id, byte[] Bytes)>();
        using var batcher = new PaneOutputBatcher(ui, (pane, bytes) => output.Add((pane, bytes.ToArray())));
        for (int i = 0; i < 24; i++) batcher.Add(id, Enumerable.Repeat((byte)i, 8192).ToArray());
        batcher.Add(other, new byte[] { 42 });
        Assert.Single(ui.Pending);
        ui.Pending.Dequeue()();
        Assert.Contains(output, x => x.Id == other);
        Assert.Equal(65536, output.First().Bytes.Length);
        Assert.Single(ui.Pending);
        while (ui.Pending.TryDequeue(out var callback)) callback();
        Assert.Equal(Enumerable.Range(0, 24).SelectMany(i => Enumerable.Repeat((byte)i, 8192)), output.Where(x => x.Id == id).SelectMany(x => x.Bytes));
        Assert.Equal(4, output.Count);
    }

    [Fact]
    public void ExitCannotOvertakeQueuedOutputOrTheNextProcess()
    {
        var ui = new Ui();
        var id = Guid.NewGuid();
        var seen = new List<int>();
        using var batcher = new PaneOutputBatcher(ui, (_, b) => seen.Add(b.Span[0]));
        batcher.Add(id, new byte[65536]);
        batcher.Add(id, new byte[] { 1 });
        batcher.Complete(id, () => seen.Add(2));
        batcher.Add(id, new byte[] { 3 });
        while (ui.Pending.TryDequeue(out var callback)) callback();
        Assert.Equal(new[] { 0, 1, 2, 3 }, seen);
    }

    [Fact]
    public void JsonlStreamingPreservesByteOffsetsAndPartialUnicodeRows()
    {
        var path = Path.GetTempFileName();
        try
        {
            var first = new string('x', 70000) + "שלום";
            var prefix = Encoding.UTF8.GetBytes(first + "\n");
            File.WriteAllBytes(path, prefix.Concat(Encoding.UTF8.GetBytes("partial")).ToArray());
            var rows = new List<(string, long, int)>();
            long offset;
            using (var file = File.OpenRead(path)) offset = TranscriptLines.Read(file, 0, (s, o, l) => rows.Add((s, o, l)));
            Assert.Equal(prefix.Length, offset);
            Assert.Equal((first, 0L, prefix.Length - 1), Assert.Single(rows));
            File.AppendAllText(path, "✓\n");
            using var again = File.OpenRead(path);
            TranscriptLines.Read(again, offset, (s, o, l) => rows.Add((s, o, l)));
            Assert.Equal("partial✓", rows[1].Item1);
            Assert.Equal(prefix.Length, rows[1].Item2);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void JournalDeltaReplacesCollapsedTailAndHandlesTruncation()
    {
        var row = new InspectorEvent("work", "now", "", "Read", "file", "", 1);
        var old = new InspectorData(new[] { row, row }, null);
        Assert.Equal(2, InspectorDelta.CommonPrefix(old, old));
        Assert.Equal(1, InspectorDelta.CommonPrefix(old, new InspectorData(new[] { row, row with { Repeat = 2 } }, null)));
        Assert.Equal(1, InspectorDelta.CommonPrefix(old, new InspectorData(new[] { row }, null)));
        Assert.Equal(0, InspectorDelta.CommonPrefix(old, null));
    }

    [Fact]
    public async Task TranscriptWorkerCachesUnchangedHistoryAndReloadsAfterEviction()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{\"type\":\"event_msg\",\"timestamp\":\"now\",\"payload\":{\"type\":\"turn_aborted\",\"reason\":\"test\"}}\n");
            var key = new TranscriptKey(Guid.NewGuid(), "test", null, true, path);
            var service = new TranscriptService();
            var first = await service.ReadAsync(key);
            Assert.Single(first!.Events);
            Assert.Same(first, await service.ReadAsync(key));
            service.Retain(new HashSet<Guid>());
            var restored = await service.ReadAsync(key);
            Assert.NotSame(first, restored);
            Assert.Equal(first.Events, restored!.Events);
            // The room must be able to consume a sleeping bot's final reply
            // on its next poll instead of perpetually reading a cold null.
            Assert.Same(restored, service.ReadCached(key));
            service.Retain(new HashSet<Guid>());
            Assert.Same(restored, await service.ReadAsync(key));
        }
        finally { File.Delete(path); }
    }
}
