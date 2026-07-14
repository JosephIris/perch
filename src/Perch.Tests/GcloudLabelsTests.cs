using System.Collections.Generic;
using PerchCli;
using Xunit;

namespace Perch.Tests;

// The command rewriter runs on every Bash call an agent makes. A bug here
// doesn't produce a wrong label — it produces a CORRUPTED gcloud command, or
// worse, a label pasted onto whatever command happened to follow an `&&`.
// Hence the paranoia below.
public class GcloudLabelsTests
{
    private static readonly List<KeyValuePair<string, string>> Labels = new()
    {
        new("agent-owner", "joseph"),
        new("agent-session", "5dc1e171-7f05-4528-9849-51b59786927c"),
        new("agent-pane", "3"),
    };

    // ---------- Detect ----------

    [Theory]
    [InlineData("gcloud compute instances create vm-1", GcloudLabels.Kind.Instance)]
    [InlineData("gcloud dataproc clusters create dp-1 --region=us-east5", GcloudLabels.Kind.Cluster)]
    [InlineData("gcloud beta compute instances create vm-1", GcloudLabels.Kind.Instance)]
    [InlineData("gcloud alpha dataproc clusters create c", GcloudLabels.Kind.Cluster)]
    public void Detects_billable_creates(string cmd, GcloudLabels.Kind expected)
        => Assert.Equal(expected, GcloudLabels.Detect(cmd));

    [Theory]
    [InlineData("gcloud compute instances list")]
    [InlineData("gcloud compute instances delete vm-1")]
    [InlineData("gcloud storage buckets create gs://x")]   // not hourly-billed; out of scope
    [InlineData("echo gcloud compute instances create")]   // still matches? see below
    [InlineData("")]
    [InlineData(null)]
    public void Ignores_everything_else(string? cmd)
    {
        // NOTE: `echo gcloud compute instances create` DOES match — we can't tell
        // it apart without a real shell parser. Stamping it only appends a flag to
        // an echo, which is harmless, so we accept the false positive rather than
        // risk missing a real create.
        if (cmd == "echo gcloud compute instances create")
        {
            Assert.Equal(GcloudLabels.Kind.Instance, GcloudLabels.Detect(cmd));
            return;
        }
        Assert.Equal(GcloudLabels.Kind.None, GcloudLabels.Detect(cmd));
    }

    // ---------- Stamp: the dangerous cases ----------

    [Fact]
    public void Inserts_after_create_not_at_end_of_line()
    {
        // The whole reason we insert at `create`: appending at the end would put
        // the flag on `echo`, not on gcloud.
        var got = GcloudLabels.Stamp("gcloud compute instances create vm-1 && echo done", Labels);
        Assert.Equal(
            "gcloud compute instances create --labels=agent-owner=joseph,agent-session=5dc1e171-7f05-4528-9849-51b59786927c,agent-pane=3 vm-1 && echo done",
            got);
        Assert.EndsWith("&& echo done", got);
    }

    [Fact]
    public void Survives_line_continuations()
    {
        var cmd = "gcloud dataproc clusters create dp-1 \\\n  --region=us-east5 \\\n  --num-workers=4";
        var got = GcloudLabels.Stamp(cmd, Labels);
        Assert.Contains("create --labels=agent-owner=joseph", got);
        Assert.Contains("--num-workers=4", got);
        Assert.Contains("\\\n", got);   // continuations intact
    }

    [Fact]
    public void Merges_into_an_existing_labels_flag()
    {
        var got = GcloudLabels.Stamp("gcloud compute instances create vm --labels=env=dev", Labels);
        Assert.Contains("--labels=env=dev,agent-owner=joseph", got);
        Assert.Equal(2, got.Split("--labels").Length);   // exactly one --labels flag survives
    }

    [Fact]
    public void Merges_into_a_quoted_labels_value_and_keeps_the_quotes()
    {
        var got = GcloudLabels.Stamp("gcloud compute instances create vm --labels=\"env=dev,team=ml\"", Labels);
        Assert.Contains("--labels=\"env=dev,team=ml,agent-owner=joseph", got);
    }

    [Fact]
    public void Does_not_merge_into_a_labels_flag_belonging_to_a_later_command()
    {
        // The --labels here belongs to the SECOND gcloud call. Merging into it
        // would leave the actual create unlabelled and corrupt the other command.
        var cmd = "gcloud compute instances create vm-1 && gcloud compute instances update vm-2 --labels=env=dev";
        var got = GcloudLabels.Stamp(cmd, Labels);
        Assert.Contains("create --labels=agent-owner=joseph", got);
        Assert.EndsWith("update vm-2 --labels=env=dev", got);   // untouched
    }

    [Fact]
    public void Leaves_non_create_commands_byte_identical()
    {
        const string cmd = "gcloud compute instances list --filter=status=RUNNING";
        Assert.Equal(cmd, GcloudLabels.Stamp(cmd, Labels));
    }

    [Fact]
    public void Drops_labels_it_cannot_make_valid_rather_than_failing_the_create()
    {
        // An empty/garbage value must not produce `--labels=agent-owner=` — gcloud
        // would reject the whole call, breaking the agent's work to satisfy our
        // bookkeeping. Better to lose the label.
        var bad = new List<KeyValuePair<string, string>> { new("agent-owner", "!!!"), new("agent-pane", "3") };
        var got = GcloudLabels.Stamp("gcloud compute instances create vm", bad);
        Assert.Equal("gcloud compute instances create --labels=agent-pane=3 vm", got);
    }

    [Fact]
    public void Stamps_nothing_when_no_label_survives_sanitizing()
    {
        var bad = new List<KeyValuePair<string, string>> { new("agent-owner", "!!!") };
        const string cmd = "gcloud compute instances create vm";
        Assert.Equal(cmd, GcloudLabels.Stamp(cmd, bad));
    }

    // ---------- Sanitize ----------

    [Theory]
    [InlineData("Joseph", "joseph")]
    [InlineData("Offline Eval", "offline-eval")]
    [InlineData("com.example.shop", "com-example-shop")]   // dots are illegal in a label
    [InlineData("!!!", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void SanitizeValue_coerces_to_gcp_label_charset(string? raw, string expected)
        => Assert.Equal(expected, GcloudLabels.SanitizeValue(raw));

    [Fact]
    public void SanitizeValue_preserves_a_uuid_that_starts_with_a_digit()
    {
        // Regression: label VALUES have no leading-letter rule (only KEYS do).
        // Stripping the leading digit here would corrupt the session id, which is
        // the key that decides whether a running machine is an orphan — and it
        // would fail silently, because a mangled id simply never matches a pane.
        const string uuid = "5dc1e171-7f05-4528-9849-51b59786927c";
        Assert.Equal(uuid, GcloudLabels.SanitizeValue(uuid));
    }

    [Fact]
    public void SanitizeKey_must_start_with_a_letter()
        => Assert.Equal("agent-pane", GcloudLabels.SanitizeKey("9agent-pane"));

    [Fact]
    public void Sanitize_caps_at_63_chars()
        => Assert.Equal(63, GcloudLabels.SanitizeValue(new string('a', 100)).Length);

    [Fact]
    public void A_real_session_id_round_trips_through_a_full_stamp()
    {
        // End to end: the label the poller will later read back must contain the
        // session id verbatim, or live-vs-orphan classification is broken.
        var got = GcloudLabels.Stamp("gcloud compute instances create tt-train", Labels);
        Assert.Contains("agent-session=5dc1e171-7f05-4528-9849-51b59786927c", got);
    }
}
