using System.IO;
using PerchCli;
using Xunit;

namespace Perch.Tests;

/// Whether a codex approval is the user's to answer or the reviewer model's.
/// The journal lines below are cut from a real 0.153.2 rollout: a turn_context
/// record (written every turn) and a thread_settings_applied event (written
/// when the user changes the setting mid-thread).
public class CodexAutoReviewTests
{
    private const string TurnAuto =
        "{\"timestamp\":\"2026-09-05T16:44:25.838Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"t2\",\"cwd\":\"C:\\x\",\"approval_policy\":\"on-request\",\"approvals_reviewer\":\"auto_review\",\"sandbox_policy\":{\"type\":\"workspace-write\"}}}";
    private const string TurnUser =
        "{\"timestamp\":\"2026-09-05T16:41:49.392Z\",\"type\":\"turn_context\",\"payload\":{\"turn_id\":\"t1\",\"cwd\":\"C:\\x\",\"approval_policy\":\"on-request\",\"approvals_reviewer\":\"user\",\"sandbox_policy\":{\"type\":\"workspace-write\"}}}";
    private const string SettingsAuto =
        "{\"timestamp\":\"2026-09-05T16:44:22.693Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"thread_settings_applied\",\"thread_settings\":{\"model\":\"gpt-6-astra\",\"approval_policy\":\"on-request\",\"approvals_reviewer\":\"auto_review\",\"permission_profile\":{\"type\":\"managed\"}}}}";
    private const string Noise =
        "{\"timestamp\":\"2026-09-05T16:44:25.795Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null}}";

    [Fact]
    public void TheReviewerAnswers_WhenTheLastRecordSaysAutoReview()
        => Assert.True(CodexAutoReview.Detect(string.Join("\n", TurnUser, SettingsAuto, TurnAuto, Noise)));

    [Fact]
    public void ThePersonAnswers_WhenTheyTurnedItBackOff()
        => Assert.False(CodexAutoReview.Detect(string.Join("\n", TurnAuto, Noise, TurnUser)));

    [Fact]
    public void ThePersonAnswers_UnderAPolicyCodexNeverRoutesToTheReviewer()
    {
        var untrusted = TurnAuto.Replace("\"on-request\"", "\"untrusted\"");
        Assert.False(CodexAutoReview.Detect(untrusted));
    }

    [Fact]
    public void AGranularPolicyStillRoutesToTheReviewer()
    {
        var granular = TurnAuto.Replace("\"approval_policy\":\"on-request\"", "\"approval_policy\":{\"granular\":{\"sandbox_approval\":\"auto_review\"}}");
        Assert.True(CodexAutoReview.Detect(granular));
    }

    [Fact]
    public void NoRecordSpeaks_IsNotAnAnswer()
        => Assert.Null(CodexAutoReview.Detect(Noise + "\n" + Noise));

    [Fact]
    public void AMissingJournalMeansThePersonAnswers()
        => Assert.False(CodexAutoReview.IsOn(@"C:\definitely\not\here.jsonl"));

    [Fact]
    public void TheJournalIsReadFromItsTail()
    {
        // A setting stated far back, then megabytes of noise: the tail scan has
        // to keep walking until it finds the record that speaks.
        var path = Path.Combine(Path.GetTempPath(), "perch-test-rollout-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            using (var w = new StreamWriter(path))
            {
                w.WriteLine(TurnAuto);
                for (var i = 0; i < 4000; i++) w.WriteLine(Noise.PadRight(400, ' '));
            }
            Assert.True(CodexAutoReview.IsOn(path));
        }
        finally { File.Delete(path); }
    }
}
