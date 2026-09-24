using Xunit;

namespace Perch.Tests;

// The inbox's pure half: reading what the Gmail export writes, and the state
// file every PC shares. Merge is the part that decides whether two machines
// agree about an email, so it gets the most cases.
public class InboxModelTests
{
    // Exactly the shape Code.gs writes, including a body that contains its
    // own `---` line and a quoted reply.
    private const string Thread =
        "# Binance FTD numbers\n" +
        "\n" +
        "---\n" +
        "## Message 1\n" +
        "From: Dana Levi <dana@persona.ly>\n" +
        "To: joseph@persona.ly\n" +
        "Cc: ofir@persona.ly\n" +
        "Date: 2026-09-23T07:12:00.000Z\n" +
        "Subject: Binance FTD numbers\n" +
        "\n" +
        "Hi Joseph,\n" +
        "---\n" +
        "Is the 10 real?\n" +
        "\n" +
        "Attachment: 1_1_image.png\n" +
        "Attachment: 1_2_report.pdf\n" +
        "---\n" +
        "## Message 2\n" +
        "From: joseph@persona.ly\n" +
        "To: Dana Levi <dana@persona.ly>\n" +
        "Date: 2026-09-23T08:00:00.000Z\n" +
        "Subject: Re: Binance FTD numbers\n" +
        "\n" +
        "> old text\n" +
        "Looking now.\n" +
        "On Tue, Dana wrote:\n" +
        "> Is the 10 real?\n" +
        "\n";

    [Fact]
    public void ParseThread_ReadsMessagesHeadersAndAttachments()
    {
        var t = InboxModel.ParseThread(Thread);
        Assert.Equal("Binance FTD numbers", t.Subject);
        Assert.Equal(2, t.Messages.Count);
        var m1 = t.Messages[0];
        Assert.Equal("Dana Levi <dana@persona.ly>", m1.From);
        Assert.Equal("ofir@persona.ly", m1.Cc);
        Assert.Equal("Hi Joseph,\n---\nIs the 10 real?", m1.Body);
        Assert.Equal(new[] { "1_1_image.png", "1_2_report.pdf" }, m1.Attachments);
        Assert.Empty(t.Messages[1].Attachments);
        Assert.Equal("", t.Messages[1].Cc);
    }

    [Fact]
    public void ParseThread_CrlfAndGarbageDoNotThrow()
    {
        Assert.Single(InboxModel.ParseThread(Thread.Replace("\n", "\r\n")).Messages.Skip(1));
        Assert.Empty(InboxModel.ParseThread("").Messages);
        Assert.Empty(InboxModel.ParseThread("just text\n---\nnot a message").Messages);
    }

    [Fact]
    public void Snippet_SkipsQuotedReply()
    {
        Assert.Equal("Looking now.", InboxModel.Snippet(InboxModel.ParseThread(Thread)));
    }

    [Theory]
    [InlineData("2026-09-23 Binance FTD numbers [18f2a9c0d1]", "18f2a9c0d1")]
    [InlineData("2026-09-23 [draft] notes [abc]", "abc")]
    [InlineData("Random folder", null)]
    public void ThreadIdFromFolder(string name, string? expected)
        => Assert.Equal(expected, InboxModel.ThreadIdFromFolder(name));

    [Fact]
    public void DisplayName_DropsAddress()
    {
        Assert.Equal("Dana Levi", InboxModel.DisplayName("\"Dana Levi\" <dana@x.com>"));
        Assert.Equal("dana@x.com", InboxModel.DisplayName("dana@x.com"));
    }

    private static InboxModel.StateFile File(params (string Id, string State, int Minute)[] entries)
    {
        var f = new InboxModel.StateFile();
        foreach (var (id, state, minute) in entries)
            f.Threads[id] = new InboxModel.Entry { State = state, UpdatedAt = new DateTimeOffset(2026, 9, 23, 10, minute, 0, TimeSpan.Zero) };
        return f;
    }

    [Fact]
    public void Merge_KeepsBothPcsEditsAndLaterWinsPerThread()
    {
        var pcA = File(("a", "done", 5), ("b", "read", 1));
        var pcB = File(("b", "pending", 3), ("c", "read", 2));
        var m = InboxModel.Merge(pcA, pcB);
        Assert.Equal("done", m.Threads["a"].State);
        Assert.Equal("pending", m.Threads["b"].State);
        Assert.Equal("read", m.Threads["c"].State);
        // Order doesn't matter.
        Assert.Equal("pending", InboxModel.Merge(pcB, pcA).Threads["b"].State);
    }

    [Fact]
    public void StateFile_RoundTripsAndToleratesEmptyOrBrokenFile()
    {
        var f = File(("a", "pending", 5));
        var back = InboxModel.ParseState(InboxModel.SerializeState(f));
        Assert.Equal("pending", back.Threads["a"].State);
        Assert.Equal(f.Threads["a"].UpdatedAt, back.Threads["a"].UpdatedAt);
        Assert.Empty(InboxModel.ParseState("").Threads);          // the user's freshly created empty file
        Assert.Empty(InboxModel.ParseState("{not json").Threads);
    }

    [Fact]
    public void Effective_NewMailRevivesReadOrDoneButNotPending()
    {
        var at = new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);
        var later = at.AddMinutes(10);
        Assert.Equal("new", InboxModel.Effective(null, at));
        Assert.Equal("done", InboxModel.Effective(new() { State = "done", UpdatedAt = later }, at));
        Assert.Equal("new", InboxModel.Effective(new() { State = "done", UpdatedAt = at }, later));
        Assert.Equal("new", InboxModel.Effective(new() { State = "read", UpdatedAt = at }, later));
        Assert.Equal("pending", InboxModel.Effective(new() { State = "pending", UpdatedAt = at }, later));
        Assert.Equal("new", InboxModel.Effective(new() { State = "bogus", UpdatedAt = later }, at));
    }

    [Fact]
    public void GcloudLoginExpired_OnlyForTheReauthError()
    {
        // The exact line the key command printed on 2026-09-24.
        Assert.True(InboxController.IsGcloudLoginExpired(
            "The key command failed: ERROR: (gcloud.secrets.versions.access) There was a problem refreshing your current auth tokens: Reauthentication failed. cannot prompt during non-interactive execution."));
        Assert.True(InboxController.IsGcloudLoginExpired(
            "The key command failed: ERROR: (gcloud.secrets.versions.access) You do not currently have an active account selected. Please run: $ gcloud auth login"));
        Assert.False(InboxController.IsGcloudLoginExpired(
            "The key command failed: ERROR: (gcloud.secrets.versions.access) NOT_FOUND: Secret [x] not found or has no versions."));
        Assert.False(InboxController.IsGcloudLoginExpired("Drive: 404 Not Found"));
    }
}
