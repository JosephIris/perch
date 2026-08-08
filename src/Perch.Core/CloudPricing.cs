using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Perch;

/// Turns a machine type + uptime into a dollar figure.
///
/// This is an ESTIMATE and the UI says so. It is deliberately NOT the Cloud
/// Billing API: that needs extra IAM, is slow, and reports days late — useless
/// for "this cluster has burned $50 since breakfast". A static list-price table
/// answers that instantly and is right to within the discount you negotiated.
///
/// What it does NOT include: persistent disks, network egress, licensing, and
/// any sustained-use / committed-use discount. Real bills come in lower.
///
/// Prices are approximate us-central1 on-demand rates and WILL drift as Google
/// changes them. Rather than pretend otherwise, the table can be overridden
/// without a rebuild: drop a {"machine-type": usdPerHour} JSON object at
/// %LOCALAPPDATA%\Perch\cloud-prices.json and it wins over the defaults.
internal static class CloudPricing
{
    /// machine type → (USD/hour, vCPUs). vCPUs are needed for Dataproc, which
    /// bills a premium per vCPU on top of the underlying VMs.
    private static readonly Dictionary<string, (double Usd, int Vcpu)> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        // E2 — the general-purpose fleet
        ["e2-medium"]        = (0.0335, 2),
        ["e2-standard-2"]    = (0.0670, 2),
        ["e2-standard-4"]    = (0.1340, 4),
        ["e2-standard-8"]    = (0.2681, 8),
        ["e2-standard-16"]   = (0.5362, 16),
        ["e2-standard-32"]   = (1.0724, 32),
        ["e2-highmem-2"]     = (0.0904, 2),
        ["e2-highmem-4"]     = (0.1809, 4),
        ["e2-highmem-8"]     = (0.3618, 8),
        ["e2-highmem-16"]    = (0.7236, 16),

        // N2 / N2D / T2D
        ["n2-standard-2"]    = (0.0971, 2),
        ["n2-standard-4"]    = (0.1942, 4),
        ["n2-standard-8"]    = (0.3885, 8),
        ["n2-standard-16"]   = (0.7770, 16),
        ["n2-standard-32"]   = (1.5540, 32),
        ["n2-highmem-2"]     = (0.1310, 2),
        ["n2d-standard-2"]   = (0.0845, 2),
        ["n2d-standard-4"]   = (0.1690, 4),
        ["n2d-standard-8"]   = (0.3381, 8),
        ["n2d-standard-16"]  = (0.6762, 16),
        ["t2d-standard-2"]   = (0.0844, 2),
        ["t2d-standard-4"]   = (0.1688, 4),

        // A2 — the GPU boxes. These are the ones worth losing sleep over: an
        // a2-ultragpu-4g left up over a weekend is roughly $1,400.
        ["a2-highgpu-1g"]    = (3.6733, 12),
        ["a2-highgpu-2g"]    = (7.3466, 24),
        ["a2-highgpu-4g"]    = (14.6932, 48),
        ["a2-highgpu-8g"]    = (29.3864, 96),
        ["a2-ultragpu-1g"]   = (5.0688, 12),
        ["a2-ultragpu-2g"]   = (10.1376, 24),
        ["a2-ultragpu-4g"]   = (20.2752, 48),
        ["a2-ultragpu-8g"]   = (40.5504, 96),
    };

    /// Dataproc's own surcharge, per vCPU per hour, on top of the VM cost.
    private const double DataprocPerVcpuHour = 0.010;

    /// The same surcharge, for callers that priced the VM elsewhere (the live
    /// catalog) and still owe Dataproc its cut.
    public static double DataprocPremium(int vcpus) => vcpus * DataprocPerVcpuHour;

    private static Dictionary<string, double>? _overrides;

    public static string OverridePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Perch", "cloud-prices.json");

    private static Dictionary<string, double> Overrides()
    {
        if (_overrides != null) return _overrides;
        _overrides = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var p = OverridePath();
            if (File.Exists(p))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(p));
                if (parsed != null)
                    foreach (var kv in parsed) _overrides[kv.Key] = kv.Value;
            }
        }
        catch (Exception ex) { Log.Error("CloudPricing.Overrides", ex); }
        return _overrides;
    }

    /// USD/hour for one VM. Returns 0 for an unknown machine type — which the UI
    /// must render as "—" rather than "$0.00", because a confident zero next to a
    /// running A100 is worse than an honest blank.
    public static double PerHour(string? machineType, bool dataproc = false)
    {
        if (string.IsNullOrWhiteSpace(machineType)) return 0;
        var mt = machineType!.Trim();

        double usd;
        int vcpu;
        if (Overrides().TryGetValue(mt, out var ov)) { usd = ov; vcpu = Vcpus(mt); }
        else if (Defaults.TryGetValue(mt, out var d)) { usd = d.Usd; vcpu = d.Vcpu; }
        else return 0;   // unknown — say so, don't guess

        if (dataproc) usd += vcpu * DataprocPerVcpuHour;
        return usd;
    }

    /// True when we have no price for this machine type, so callers can render an
    /// honest "—" instead of a fabricated $0.00.
    public static bool IsKnown(string? machineType)
        => !string.IsNullOrWhiteSpace(machineType)
           && (Defaults.ContainsKey(machineType!.Trim()) || Overrides().ContainsKey(machineType!.Trim()));

    /// vCPU count. Known types come from the table; anything else falls back to
    /// the trailing number in the name (e2-standard-8 → 8), which holds for every
    /// standard/highmem/highcpu shape Google ships.
    private static int Vcpus(string machineType)
    {
        if (Defaults.TryGetValue(machineType, out var d)) return d.Vcpu;
        var dash = machineType.LastIndexOf('-');
        if (dash >= 0 && int.TryParse(machineType.AsSpan(dash + 1), out var n) && n > 0) return n;
        return 0;
    }
}
