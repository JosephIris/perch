using System.Linq;
using Perch;
using Xunit;

namespace Perch.Tests;

// Fixture shaped exactly like `gcloud compute instances list --format=json`:
// fully-qualified zone/machineType URLs, RFC3339 timestamps, and the
// goog-dataproc-* labels Dataproc stamps on its own VMs.
public class CloudPollerTests
{
    private const string Json = """
    [
      {
        "name": "dp-audience-8f2c-m",
        "zone": "https://www.googleapis.com/compute/v1/projects/p/zones/us-east5-c",
        "machineType": "https://www.googleapis.com/compute/v1/projects/p/zones/us-east5-c/machineTypes/e2-standard-4",
        "status": "RUNNING",
        "creationTimestamp": "2026-07-14T02:44:00.000-07:00",
        "labels": {
          "goog-dataproc-cluster-name": "dp-audience-8f2c",
          "agent-owner": "joseph",
          "agent-session": "5dc1e171-7f05-4528-9849-51b59786927c",
          "agent-pane": "3"
        }
      },
      {
        "name": "dp-audience-8f2c-w-0",
        "zone": "https://www.googleapis.com/compute/v1/projects/p/zones/us-east5-c",
        "machineType": "https://www.googleapis.com/compute/v1/projects/p/zones/us-east5-c/machineTypes/e2-standard-8",
        "status": "RUNNING",
        "creationTimestamp": "2026-07-14T03:44:00.000-07:00",
        "labels": {
          "goog-dataproc-cluster-name": "dp-audience-8f2c",
          "agent-owner": "joseph",
          "agent-session": "5dc1e171-7f05-4528-9849-51b59786927c"
        }
      },
      {
        "name": "gpu-train-h1",
        "zone": "https://www.googleapis.com/compute/v1/projects/p/zones/us-central1-a",
        "machineType": "https://www.googleapis.com/compute/v1/projects/p/zones/us-central1-a/machineTypes/a2-highgpu-1g",
        "status": "RUNNING",
        "creationTimestamp": "2026-07-12T08:00:00.000-07:00",
        "labels": {
          "agent-owner": "joseph",
          "agent-session": "dead-beef-session",
          "agent-pane": "9"
        }
      }
    ]
    """;

    /// Pane 3's session is live; the GPU box's session is not.
    private static CloudPoller Poller() => new()
    {
        LookupPaneState = s => s == "5dc1e171-7f05-4528-9849-51b59786927c" ? "working" : null,
        LookupLedger = s => s == "dead-beef-session"
            ? new CloudLedger.Entry(s!, "train-sweep", "Sweep learning rates for v3", null, "9", 0, 0)
            : null,
    };

    [Fact]
    public void Rolls_a_dataproc_cluster_up_into_one_row()
    {
        var got = Poller().Parse(Json);

        // Two VMs + one standalone = 3 machines, but only 2 ROWS: the cluster is
        // one thing you delete, not two.
        Assert.Equal(2, got.Count);
        var cluster = got.Single(r => r.Kind == "cluster");
        Assert.Equal("dp-audience-8f2c", cluster.Name);
        Assert.Equal(2, cluster.VmCount);
    }

    [Fact]
    public void Cluster_age_comes_from_its_oldest_vm()
    {
        // An autoscaled worker that joined an hour ago must not make a cluster
        // that's been up since 02:44 look one hour old.
        var cluster = Poller().Parse(Json).Single(r => r.Kind == "cluster");
        var master = new System.DateTimeOffset(2026, 7, 14, 2, 44, 0, System.TimeSpan.FromHours(-7));
        Assert.Equal(master.ToUnixTimeMilliseconds(), cluster.CreatedUnixMs);
    }

    [Fact]
    public void Cluster_rate_sums_every_member_plus_the_dataproc_premium()
    {
        var cluster = Poller().Parse(Json).Single(r => r.Kind == "cluster");
        // e2-standard-4 (0.1340 + 4 vCPU × $0.01) + e2-standard-8 (0.2681 + 8 × $0.01)
        var expected = (0.1340 + 0.04) + (0.2681 + 0.08);
        Assert.Equal(expected, cluster.UsdPerHour, precision: 4);
    }

    [Fact]
    public void A_session_with_no_live_pane_is_an_orphan()
    {
        var got = Poller().Parse(Json);
        var gpu = got.Single(r => r.Name == "gpu-train-h1");
        var cluster = got.Single(r => r.Kind == "cluster");

        Assert.True(gpu.IsOrphan);          // its pane is gone
        Assert.False(cluster.IsOrphan);     // pane 3 is still working
        Assert.Equal("working", cluster.AgentState);
    }

    [Fact]
    public void An_orphan_still_knows_what_it_was_for()
    {
        // The whole point: the pane is gone, but the ledger still explains the
        // machine. Without this an orphan is just an anonymous $200 charge.
        var gpu = Poller().Parse(Json).Single(r => r.Name == "gpu-train-h1");
        Assert.Equal("train-sweep", gpu.AgentName);
        Assert.Equal("Sweep learning rates for v3", gpu.Task);
    }

    [Fact]
    public void Orphans_sort_first()
        => Assert.True(Poller().Parse(Json)[0].IsOrphan);

    [Fact]
    public void Urls_are_reduced_to_bare_zone_and_machine_type()
    {
        var gpu = Poller().Parse(Json).Single(r => r.Name == "gpu-train-h1");
        Assert.Equal("us-central1-a", gpu.Zone);
        Assert.Equal("a2-highgpu-1g", gpu.MachineType);
        Assert.True(gpu.IsGpu);
    }

    [Fact]
    public void Dataproc_deletes_need_a_region_not_a_zone()
    {
        var cluster = Poller().Parse(Json).Single(r => r.Kind == "cluster");
        Assert.Equal("us-east5", cluster.Region);
    }

    [Fact]
    public void An_unknown_machine_type_is_flagged_rather_than_priced_at_zero()
    {
        // A confident "$0.00" next to a running machine is worse than an honest
        // blank — it reads as "this is free".
        const string exotic = """
        [{ "name":"weird","zone":"z/us-east5-b","machineType":"m/c4-hypernova-99",
           "status":"RUNNING","creationTimestamp":"2026-07-14T02:44:00Z",
           "labels":{"agent-owner":"joseph","agent-session":"x"} }]
        """;
        var r = Poller().Parse(exotic).Single();
        Assert.False(r.PriceKnown);
        Assert.Equal(0, r.UsdPerHour);
    }

    [Fact]
    public void Empty_list_is_not_a_crash()
        => Assert.Empty(Poller().Parse("[]"));

    [Fact]
    public void Parses_a_real_gcloud_response()
    {
        // Captured verbatim from a live `gcloud compute instances create` that was
        // stamped by the real PreToolUse hook, then read back with the exact filter
        // CloudPoller uses. Trimmed to the fields we actually read, but the SHAPES
        // are untouched: zone and machineType arrive as fully-qualified URLs, the
        // timestamp carries an offset, and labels is a flat string map.
        const string real = """
        [
          {
            "creationTimestamp": "2026-07-14T04:35:21.808-07:00",
            "id": "3878246550953762838",
            "kind": "compute#instance",
            "labels": {
              "agent-owner": "josep",
              "agent-pane": "7",
              "agent-session": "5dc1e171-7f05-4528-9849-51b59786927c"
            },
            "machineType": "https://www.googleapis.com/compute/v1/projects/p/zones/us-east1-b/machineTypes/e2-micro",
            "name": "perch-label-test",
            "status": "RUNNING",
            "zone": "https://www.googleapis.com/compute/v1/projects/p/zones/us-east1-b"
          }
        ]
        """;

        var r = Poller().Parse(real).Single();
        Assert.Equal("perch-label-test", r.Name);
        Assert.Equal("e2-micro", r.MachineType);
        Assert.Equal("us-east1-b", r.Zone);
        Assert.Equal("us-east1-b/perch-label-test", r.Id);   // the key cloud.delete takes back

        // The session id must survive the round trip byte-for-byte. It starts with
        // a DIGIT, and an earlier sanitizer wrongly applied GCP's leading-letter
        // rule (which governs label KEYS, not values) and chewed it off — which
        // would have made every machine look like an orphan, forever, silently.
        Assert.Equal("5dc1e171-7f05-4528-9849-51b59786927c", r.Session);
    }

    // A GPU the project is running that Perch never created — no agent labels,
    // just Terraform's. Exactly the case the radar exists for.
    private const string RadarGpuJson = """
    [{ "name":"ds-ml-dws","zone":"z/us-central1-c",
       "machineType":"m/a2-ultragpu-4g","status":"RUNNING",
       "creationTimestamp":"2026-07-15T20:10:00-07:00",
       "labels":{"goog-terraform-provisioned":"true","env":"ds"} }]
    """;

    [Fact]
    public void Radar_rows_are_flagged_and_never_orphaned()
    {
        // Parsed as radar, a box with no live pane is NOT an orphan — orphan is a
        // status only OUR machines can have. It's its own bucket instead.
        var radar = Poller().Parse(RadarGpuJson, startedByPerch: false);
        var ds = Assert.Single(radar);
        Assert.False(ds.StartedByPerch);
        Assert.False(ds.IsOrphan);
        Assert.True(ds.IsGpu);
        Assert.Equal(20.2752, ds.UsdPerHour, precision: 4);   // priced → cost shows
        Assert.Null(ds.AgentName);                             // no ledger join attempted
    }

    [Fact]
    public void Merge_keeps_the_attributed_copy_of_a_gpu_seen_in_both()
    {
        // A GPU WE created surfaces in both the label query and the GPU query. The
        // attributed copy must win — it carries the agent + task the radar row lacks.
        var attributed = Poller().Parse(Json, startedByPerch: true);
        var radar = Poller().Parse(Json, startedByPerch: false);
        var merged = CloudPoller.Merge(attributed, radar);

        Assert.Equal(attributed.Count, merged.Count);          // no duplicates
        var gpu = merged.Single(r => r.Name == "gpu-train-h1");
        Assert.True(gpu.StartedByPerch);
        Assert.Equal("train-sweep", gpu.AgentName);            // ledger join survived
    }

    [Fact]
    public void Merge_adds_a_radar_gpu_we_did_not_create()
    {
        var attributed = Poller().Parse(Json, startedByPerch: true);
        var radar = Poller().Parse(RadarGpuJson, startedByPerch: false);
        var merged = CloudPoller.Merge(attributed, radar);

        Assert.Equal(attributed.Count + 1, merged.Count);
        var ds = merged.Single(r => r.Name == "ds-ml-dws");
        Assert.False(ds.StartedByPerch);
    }
}
