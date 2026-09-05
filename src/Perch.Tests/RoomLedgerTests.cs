using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Perch.Tests;

/// The room's append-only log. The property everything else leans on: `Seq`
/// only ever goes up, including across a reopen — so a page that remembers
/// "I've seen up to 41" can ask for "since 41" and get exactly what it missed.
public class RoomLedgerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "perch-roomtest-" + Guid.NewGuid().ToString("N")[..8], "room.jsonl");

    [Fact]
    public void Append_AssignsMonotonicSeq_AndSurvivesReopen()
    {
        var path = TempPath();
        var ledger = new RoomLedger(path);
        Assert.Equal(0, ledger.LastSeq);
        var a = ledger.Append(new RoomEntry { Kind = "user", From = "you", Text = "hello", To = new List<string> { "ada" } });
        var b = ledger.Append(new RoomEntry { Kind = "beat", From = "ada", Text = "hi" });
        Assert.Equal(1, a.Seq);
        Assert.Equal(2, b.Seq);
        Assert.True(a.TsMs > 0);

        var reopened = new RoomLedger(path);
        Assert.Equal(2, reopened.LastSeq);
        var c = reopened.Append(new RoomEntry { Kind = "system", From = "perch", Text = "Bo joined", Event = "joined" });
        Assert.Equal(3, c.Seq);

        var all = reopened.ReadAll();
        Assert.Equal(new long[] { 1, 2, 3 }, all.ConvertAll(e => e.Seq).ToArray());
        Assert.Equal("ada", Assert.Single(all[0].To!));
        Assert.Equal("joined", all[2].Event);
        Assert.Null(all[1].To);
    }

    [Fact]
    public void ReadSince_ReturnsOnlyNewerEntries_AndFlagsTruncation()
    {
        var ledger = new RoomLedger(TempPath());
        for (var i = 0; i < 10; i++) ledger.Append(new RoomEntry { Kind = "beat", From = "ada", Text = $"m{i}" });

        var (since7, truncated) = ledger.ReadSince(7);
        Assert.False(truncated);
        Assert.Equal(new long[] { 8, 9, 10 }, since7.ConvertAll(e => e.Seq).ToArray());

        var (capped, wasCut) = ledger.ReadSince(0, max: 4);
        Assert.True(wasCut);
        Assert.Equal(new long[] { 7, 8, 9, 10 }, capped.ConvertAll(e => e.Seq).ToArray());

        var (none, _) = ledger.ReadSince(10);
        Assert.Empty(none);
        Assert.Equal(new long[] { 9, 10 }, ledger.Tail(2).ConvertAll(e => e.Seq).ToArray());
    }

    [Fact]
    public void CorruptLines_AreSkipped_NotFatal()
    {
        var path = TempPath();
        var ledger = new RoomLedger(path);
        ledger.Append(new RoomEntry { Kind = "beat", From = "ada", Text = "ok" });
        File.AppendAllText(path, "{ half a line\n");
        ledger.Append(new RoomEntry { Kind = "beat", From = "ada", Text = "still ok" });

        var reopened = new RoomLedger(path);
        var all = reopened.ReadAll();
        Assert.Equal(2, all.Count);
        Assert.Equal("still ok", all[1].Text);
        Assert.Equal(2, reopened.LastSeq);
    }

    [Fact]
    public void Rotation_KeepsTheNewestLines_AndSeqKeepsClimbing()
    {
        var path = TempPath();
        var ledger = new RoomLedger(path);
        // Big entries so the byte cap trips well before the line cap would.
        var filler = new string('x', 4096);
        var count = (int)(RoomLedger.RotateAtBytes / 4096) + RoomLedger.KeepLines / 2 + 5;
        for (var i = 0; i < count; i++) ledger.Append(new RoomEntry { Kind = "beat", From = "ada", Text = filler });

        var all = ledger.ReadAll();
        Assert.True(all.Count <= RoomLedger.KeepLines, $"kept {all.Count} lines");
        Assert.Equal(count, all[^1].Seq);            // newest survives with its original seq
        Assert.Equal(count, ledger.LastSeq);
        var next = ledger.Append(new RoomEntry { Kind = "beat", From = "ada", Text = "after" });
        Assert.Equal(count + 1, next.Seq);
    }

    [Fact]
    public void Missing_File_IsAnEmptyRoom()
    {
        var ledger = new RoomLedger(TempPath());
        Assert.Empty(ledger.ReadAll());
        Assert.Empty(ledger.Tail(5));
        Assert.Equal(0, ledger.LastSeq);
    }
}
