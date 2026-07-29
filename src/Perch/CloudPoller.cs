using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Perch;

/// One billable thing the panel can show you and let you kill. A Dataproc
/// cluster is ONE of these, not five — its member VMs are rolled up, because
/// "delete this cluster" is the action you actually want and deleting the master
/// VM out from under a live cluster is a way to make a mess.
internal sealed record CloudResource(
    string Id,               // stable key: cluster name, or "<zone>/<name>" for a VM
    string Name,
    string Kind,             // "instance" | "cluster"
    string MachineType,
    string Zone,
    string Region,           // needed to delete a Dataproc cluster
    int VmCount,
    bool IsGpu,
    long CreatedUnixMs,
    double UsdPerHour,
    bool PriceKnown,
    string? Session,
    string? PaneId,
    // Joined from the ledger — the half a GCP label can't carry.
    string? AgentName,
    string? Task,
    bool IsOrphan,
    string? AgentState,      // live panes only: working / done / waiting …
    bool StartedByPerch);    // false → surfaced by the GPU radar, not created here

/// Asks GCP what is actually running, filtered server-side to resources this
/// user's agents stamped. That filter is the whole trick: the project has 200+
/// running instances, almost all of them production, and listing them all would
/// bury the handful an agent actually made.
///
/// The entire feature no-ops when gcloud is absent or unauthenticated — no chip,
/// no panel, no cost to anyone who doesn't drive GCP from an agent.
internal sealed class CloudPoller
{
    /// Resolves a session id to a live pane's state, or null when the pane is
    /// gone. This is the ONLY thing that decides orphan-vs-live, and it's
    /// deliberately not time-based: an agent legitimately sits idle for 40
    /// minutes while a cluster grinds, and flagging that would train the user to
    /// ignore the panel.
    public Func<string?, string?>? LookupPaneState { get; set; }

    /// Session id → ledger entry (agent name + the prompt behind the machine).
    public Func<string?, CloudLedger.Entry?>? LookupLedger { get; set; }

    /// Live list prices from Google's catalog. Null (or a miss) → the static table
    /// in CloudPricing answers instead, which is exactly what the tests exercise.
    public CloudPriceCatalog? Catalog { get; set; }

    /// "zone/machineType" → the shape a price needs. `instances list` reports the
    /// machine TYPE but not its vCPU/RAM, so these come from `machine-types list`,
    /// fetched before Parse runs (Parse is synchronous) and kept for the session —
    /// a machine type's shape never changes.
    private readonly Dictionary<string, (int Cpus, double MemGiB)> _shapes = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _shapeZones = new(StringComparer.OrdinalIgnoreCase);

    /// Test seam: hand Parse a shape without going near gcloud.
    internal void SeedShape(string zone, string machineType, int cpus, double memGiB)
        => _shapes[$"{zone}/{machineType}"] = (cpus, memGiB);

    private static readonly char[] Slash = { '/' };
    private bool? _available;

    /// True once we've confirmed gcloud exists and has an active account.
    /// Cached: a missing gcloud isn't going to appear mid-session, and we don't
    /// want to pay for the probe on every poll.
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is bool b) return b;
        var (code, stdout, _) = await RunAsync("auth list --filter=status:ACTIVE --format=value(account)", 8000, ct);
        _available = code == 0 && !string.IsNullOrWhiteSpace(stdout);
        if (_available == false) Log.Info("CloudPoller: gcloud unavailable or not authenticated — cloud panel disabled");
        return _available.Value;
    }

    // A RUNNING accelerator box, whoever made it. Regex the machineType for the
    // GPU families (a2/a3/g2 carry the GPU in the shape) and also catch n1/custom
    // VMs with a card attached. No inner double quotes — the whole filter is
    // already wrapped in escaped quotes for cmd.exe, and nesting would break it.
    private const string GpuFilter =
        "status=RUNNING AND (guestAccelerators:* OR machineType~a2- OR machineType~a3- OR machineType~g2-)";

    public async Task<IReadOnlyList<CloudResource>> PollAsync(CancellationToken ct = default)
    {
        if (!await IsAvailableAsync(ct)) return Array.Empty<CloudResource>();

        var owner = PerchCli.GcloudLabels.SanitizeValue(Environment.UserName);
        if (owner.Length == 0) return Array.Empty<CloudResource>();

        // 1) What THIS user's agents created. Server-side filter on OUR label buys:
        //   1. the 200-instance production fleet never crosses the wire, and
        //   2. a teammate running Perch against the same project sees their
        //      machines, not ours — so nobody deletes somebody else's cluster.
        // TERMINATED instances are excluded: they've stopped billing compute,
        // and showing them would make the panel a graveyard instead of a bill.
        var attributedJson = await ListRawAsync(
            $"labels.agent-owner={owner} AND status!=TERMINATED", ct);

        // 2) GPU radar. A forgotten a2-ultragpu is ~$20/hr — worth surfacing even
        //    without our label, and accelerators are rare enough that this stays
        //    quiet (unlike listing the whole fleet). These carry no agent labels,
        //    so they land as "not started here": visible + costed, but not ours to
        //    kill from here.
        var gpuJson = await ListRawAsync(GpuFilter, ct);

        // Pricing inputs must be in hand BEFORE Parse, which is synchronous. Both
        // are best-effort: without them Parse just falls back to the static table.
        await LoadPricingAsync(new[] { attributedJson, gpuJson }, ct);

        var attributed = ParseSafe(attributedJson, startedByPerch: true);
        var gpus = ParseSafe(gpuJson, startedByPerch: false);
        return Merge(attributed, gpus);
    }

    /// One `instances list`. Returns the raw JSON so the caller can learn which
    /// shapes it needs to price before parsing.
    private async Task<string?> ListRawAsync(string filter, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(
            $"compute instances list --filter=\"{filter}\" --format=json", 30000, ct);
        if (code == 0) return stdout;
        Log.Info($"CloudPoller: gcloud list failed ({code}): {stderr.Trim()}");
        return null;
    }

    private IReadOnlyList<CloudResource> ParseSafe(string? json, bool startedByPerch)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<CloudResource>();
        try { return Parse(json!, startedByPerch); }
        catch (Exception ex) { Log.Error("CloudPoller.Parse", ex); return Array.Empty<CloudResource>(); }
    }

    /// Load the price catalog + the machine shapes these instances need. Entirely
    /// best-effort: every failure here just leaves the static table in charge.
    private async Task LoadPricingAsync(IEnumerable<string?> instanceJsons, CancellationToken ct)
    {
        try
        {
            Catalog ??= new CloudPriceCatalog();
            if (!await Catalog.EnsureLoadedAsync(ct).ConfigureAwait(false)) { Catalog = null; return; }

            var zones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var json in instanceJsons)
            {
                if (string.IsNullOrWhiteSpace(json)) continue;
                using var doc = JsonDocument.Parse(json!);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var z = LastSegment(Str(el, "zone"));
                    if (!string.IsNullOrEmpty(z) && !_shapeZones.Contains(z!)) zones.Add(z!);
                }
            }
            if (zones.Count > 0) await LoadShapesAsync(zones, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Log.Error("CloudPoller.LoadPricing", ex); }
    }

    /// vCPU + RAM for every machine type in these zones. One call per zone-set,
    /// once per session — shapes are immutable, so there's nothing to invalidate.
    private async Task LoadShapesAsync(HashSet<string> zones, CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunAsync(
            $"compute machine-types list --zones={string.Join(",", zones)} --format=json", 30000, ct);
        if (code != 0)
        {
            Log.Info($"CloudPoller: machine-types list failed ({code}): {stderr.Trim()}");
            return;
        }
        using var doc = JsonDocument.Parse(stdout);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = Str(el, "name");
            var zone = LastSegment(Str(el, "zone"));
            var cpus = el.TryGetProperty("guestCpus", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var mem  = el.TryGetProperty("memoryMb", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetDouble() : 0;
            if (name != null && !string.IsNullOrEmpty(zone) && cpus > 0)
                _shapes[$"{zone}/{name}"] = (cpus, mem / 1024.0);
        }
        foreach (var z in zones) _shapeZones.Add(z);
    }

    /// Fold the radar set into the attributed one. A GPU WE created appears in
    /// both queries — the attributed copy wins (it carries the agent + task), so
    /// radar contributes only the strangers. Most-expensive-first, so the costliest
    /// thing you forgot never needs a scroll.
    internal static IReadOnlyList<CloudResource> Merge(
        IReadOnlyList<CloudResource> attributed, IReadOnlyList<CloudResource> gpus)
    {
        var seen = new HashSet<string>(attributed.Select(r => r.Id), StringComparer.Ordinal);
        var merged = new List<CloudResource>(attributed);
        foreach (var g in gpus) if (seen.Add(g.Id)) merged.Add(g);
        return merged
            .OrderByDescending(r => r.UsdPerHour * Hours(r.CreatedUnixMs))
            .ToList();
    }

    /// Turns the raw instance list into resources, collapsing Dataproc VMs into
    /// their cluster. Dataproc tags every VM it creates with
    /// goog-dataproc-cluster-name, so one `compute instances list` covers both
    /// resource kinds and we never need a second call per region.
    internal IReadOnlyList<CloudResource> Parse(string json, bool startedByPerch = true)
    {
        using var doc = JsonDocument.Parse(json);
        var vms = new List<Vm>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (el.TryGetProperty("labels", out var lb) && lb.ValueKind == JsonValueKind.Object)
                foreach (var p in lb.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) labels[p.Name] = p.Value.GetString() ?? "";

            var zone = LastSegment(Str(el, "zone"));
            var mt = LastSegment(Str(el, "machineType"));

            // How it was provisioned decides WHICH price it pays — the same A100
            // is $3.93/hr on-demand and $1.85/hr under DWS Flex-Start.
            string? provisioning = null;
            if (el.TryGetProperty("scheduling", out var sched) && sched.ValueKind == JsonValueKind.Object)
                provisioning = Str(sched, "provisioningModel");

            // Attached cards live on the INSTANCE (n1 + a card), while a2/a3/g2
            // carry them in the shape — but gcloud reports both here, so this is
            // the one place that covers each case.
            var accel = new List<(string Type, int Count)>();
            if (el.TryGetProperty("guestAccelerators", out var ga) && ga.ValueKind == JsonValueKind.Array)
                foreach (var a in ga.EnumerateArray())
                {
                    var t = LastSegment(Str(a, "acceleratorType"));
                    var c = a.TryGetProperty("acceleratorCount", out var cc) && cc.ValueKind == JsonValueKind.Number
                        ? cc.GetInt32() : 0;
                    if (!string.IsNullOrEmpty(t) && c > 0) accel.Add((t!, c));
                }

            vms.Add(new Vm(
                Name: Str(el, "name") ?? "",
                Zone: zone ?? "",
                MachineType: mt ?? "",
                CreatedUnixMs: ParseTime(Str(el, "creationTimestamp")),
                Cluster: labels.GetValueOrDefault("goog-dataproc-cluster-name"),
                Session: labels.GetValueOrDefault("agent-session"),
                PaneId: labels.GetValueOrDefault("agent-pane"),
                ProvisioningModel: provisioning,
                Accelerators: accel));
        }

        var result = new List<CloudResource>();

        // --- Dataproc clusters: one row per cluster ---
        foreach (var grp in vms.Where(v => !string.IsNullOrEmpty(v.Cluster)).GroupBy(v => v.Cluster!))
        {
            var members = grp.ToList();
            var first = members.OrderBy(m => m.CreatedUnixMs).First();
            // Bill every member VM, each with Dataproc's per-vCPU premium on top.
            var priced = members.Select(m => PriceOf(m, dataproc: true)).ToList();
            var rate = priced.Sum(p => p.Rate);
            var known = priced.All(p => p.Known);
            result.Add(Build(
                id: $"cluster/{grp.Key}",
                name: grp.Key,
                kind: "cluster",
                machineType: first.MachineType,
                zone: first.Zone,
                vmCount: members.Count,
                // A cluster's clock starts with its FIRST vm — autoscaled workers
                // that joined an hour ago must not make an old cluster look young.
                createdUnixMs: members.Min(m => m.CreatedUnixMs),
                rate: rate,
                priceKnown: known,
                session: first.Session,
                paneId: first.PaneId,
                startedByPerch: startedByPerch));
        }

        // --- Plain VMs ---
        foreach (var vm in vms.Where(v => string.IsNullOrEmpty(v.Cluster)))
        {
            var p = PriceOf(vm, dataproc: false);
            result.Add(Build(
                id: $"{vm.Zone}/{vm.Name}",
                name: vm.Name,
                kind: "instance",
                machineType: vm.MachineType,
                zone: vm.Zone,
                vmCount: 1,
                createdUnixMs: vm.CreatedUnixMs,
                rate: p.Rate,
                priceKnown: p.Known,
                session: vm.Session,
                paneId: vm.PaneId,
                startedByPerch: startedByPerch));
        }

        // Orphans first, then most expensive: the costliest mistake is the thing
        // you should see without scrolling.
        return result
            .OrderByDescending(r => r.IsOrphan)
            .ThenByDescending(r => r.UsdPerHour * Hours(r.CreatedUnixMs))
            .ToList();
    }

    /// What one VM costs per hour, and whether we actually know.
    ///
    /// The catalog gets first refusal because it's the only source that knows the
    /// machine was provisioned as Spot or DWS — the static table has exactly one
    /// number per shape and is simply wrong for those (a DWS A100 box by ~72%).
    /// Anything the catalog can't resolve falls straight through to the table, so
    /// a catalog that's unreachable, stale, or missing a SKU costs us nothing.
    private (double Rate, bool Known) PriceOf(Vm vm, bool dataproc)
    {
        if (Catalog != null && _shapes.TryGetValue($"{vm.Zone}/{vm.MachineType}", out var shape))
        {
            var spec = new MachineSpec(shape.Cpus, shape.MemGiB,
                vm.Accelerators ?? Array.Empty<(string, int)>());
            var dyn = Catalog.PerHour(vm.MachineType, RegionOf(vm.Zone),
                CloudPriceCatalog.VariantOf(vm.ProvisioningModel), spec);
            if (dyn is double d && d > 0)
                return (dataproc ? d + CloudPricing.DataprocPremium(shape.Cpus) : d, true);
        }
        return (CloudPricing.PerHour(vm.MachineType, dataproc), CloudPricing.IsKnown(vm.MachineType));
    }

    private CloudResource Build(string id, string name, string kind, string machineType, string zone,
                                int vmCount, long createdUnixMs, double rate, bool priceKnown,
                                string? session, string? paneId, bool startedByPerch)
    {
        // A radar row has no agent labels, so there's no session to resolve and no
        // ledger to join — skip the lookups and let it stand on its own.
        var state = startedByPerch ? LookupPaneState?.Invoke(session) : null;
        var led = startedByPerch ? LookupLedger?.Invoke(session) : null;
        return new CloudResource(
            Id: id,
            Name: name,
            Kind: kind,
            MachineType: machineType,
            Zone: zone,
            Region: RegionOf(zone),
            VmCount: vmCount,
            IsGpu: IsGpuType(machineType),
            CreatedUnixMs: createdUnixMs,
            UsdPerHour: rate,
            PriceKnown: priceKnown,
            Session: session,
            PaneId: paneId,
            AgentName: led?.AgentName,
            Task: led?.Task,
            // For our own machines: no live pane owns this session → it's an orphan.
            // Radar rows aren't ours, so "orphan" doesn't apply — they're their own
            // bucket, flagged by StartedByPerch instead.
            IsOrphan: startedByPerch && state == null,
            AgentState: state,
            StartedByPerch: startedByPerch);
    }

    private sealed record Vm(string Name, string Zone, string MachineType, long CreatedUnixMs,
                             string? Cluster, string? Session, string? PaneId,
                             string? ProvisioningModel = null,
                             IReadOnlyList<(string Type, int Count)>? Accelerators = null);

    /// The GPU families. Used only to tag the row — the $/hr figure already tells
    /// the real story, so this is a label, not an alarm.
    private static bool IsGpuType(string? mt)
        => mt != null && (mt.StartsWith("a2-", StringComparison.OrdinalIgnoreCase)
                       || mt.StartsWith("a3-", StringComparison.OrdinalIgnoreCase)
                       || mt.StartsWith("g2-", StringComparison.OrdinalIgnoreCase));

    /// us-east5-b → us-east5. Dataproc deletes are region-scoped, not zone-scoped.
    internal static string RegionOf(string? zone)
    {
        if (string.IsNullOrEmpty(zone)) return "";
        var i = zone!.LastIndexOf('-');
        return i > 0 ? zone.Substring(0, i) : zone;
    }

    private static double Hours(long createdUnixMs)
    {
        if (createdUnixMs <= 0) return 0;
        var ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - createdUnixMs;
        return ms <= 0 ? 0 : ms / 3_600_000.0;
    }

    private static string? Str(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// gcloud returns fully-qualified URLs for zone/machineType.
    private static string? LastSegment(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var i = url!.LastIndexOfAny(Slash);
        return i >= 0 ? url.Substring(i + 1) : url;
    }

    private static long ParseTime(string? iso)
        => DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto)
            ? dto.ToUnixTimeMilliseconds()
            : 0;

    /// Run gcloud off the UI thread. gcloud on Windows is a .cmd shim, so it has
    /// to go through the shell.
    internal static async Task<(int Code, string Stdout, string Stderr)> RunAsync(
        string args, int timeoutMs, CancellationToken ct)
    {
        var verb = args.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } p
            ? p[0] : "?";
        return await ProcRunner.RunAsync(
            "cmd.exe", $"/c gcloud {args}", site: $"gcloud.{verb}",
            timeoutMs: timeoutMs, ct: ct);
    }
}
