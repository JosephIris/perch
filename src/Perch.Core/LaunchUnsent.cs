using System;
using System.Collections.Generic;
using System.Linq;

namespace Perch;

/// Lines that were still waiting to go into a tab when Perch last closed
/// (Session.DeliveryUnsent), held from launch until RestorePendingWork
/// queues them again a few seconds in.
///
/// Taken once, at load, and kept apart from what gets queued meanwhile. Read
/// off the session at restore time instead, a line queued in those first
/// seconds (the phone sending to a tab whose Claude was just starting) was in
/// the live queue AND in DeliveryUnsent, and went in twice; and the save that
/// wrote it there had replaced the previous run's lines, which were lost.
internal sealed class LaunchUnsent
{
    private Dictionary<Guid, string[]>? _left;

    public LaunchUnsent(IEnumerable<(Guid Id, string[] Lines)> atLaunch) =>
        _left = atLaunch.Where(x => x.Lines.Length > 0).ToDictionary(x => x.Id, x => x.Lines);

    /// What to save as a tab's unsent lines: the previous run's that are not
    /// queued again yet, then the live queue's.
    public string[] Persisted(Guid id, string[] live) =>
        _left != null && _left.TryGetValue(id, out var old) ? old.Concat(live).ToArray() : live;

    /// The previous run's lines, once; after this only the live queue counts.
    public IReadOnlyList<(Guid Id, string[] Lines)> Take()
    {
        var all = _left?.Select(kv => (kv.Key, kv.Value)).ToList() ?? new List<(Guid, string[])>();
        _left = null;
        return all;
    }
}
