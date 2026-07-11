using System.Diagnostics;

namespace Rig.Analysis;

// Optional per-phase timing collector for `rig index --time`. Phases are recorded in completion order
// from the ORCHESTRATING thread only — always after the awaited work for that phase has fully
// completed, never from the parallel workers inside a phase — so the appends are sequential (a
// happens-before chain through the awaits) and no locking is needed.
//
// Beyond duration, it owns a single MASTER stopwatch (started at construction) against which every phase
// is stamped as an absolute [Start, End) interval, and — when StartSampling is called — a background
// ResourceSampler stamped on the SAME clock. That lets the renderer attribute each CPU/memory/disk sample
// to the phase it fell in, so `--time` can show WHY a phase is slow (out-of-proc build CPU, disk-bound
// metadata reads, the memory ceiling) instead of just how long it took.
public sealed class PhaseTimings
{
    // A phase as an absolute interval on the master clock. Start is backed out as End - measured-elapsed,
    // so it stays accurate even when work happened before the first phase or between phases.
    public readonly record struct PhaseEntry(string Name, TimeSpan Start, TimeSpan End)
    {
        public TimeSpan Elapsed => End - Start;
    }

    private readonly List<PhaseEntry> _entries = [];
    private readonly Stopwatch _wall = Stopwatch.StartNew();
    private ResourceSampler? _sampler;

    // Record a phase by its measured duration. The END is stamped on the master clock now (the call always
    // follows the awaited work); the START is end - elapsed. Resource samples in [Start, End) belong to it.
    public void Record(string name, TimeSpan elapsed)
    {
        var end = _wall.Elapsed;
        _entries.Add(new PhaseEntry(Name: name, Start: end - elapsed, End: end));
    }

    public IReadOnlyList<PhaseEntry> Entries => _entries;

    // Total elapsed on the master clock — covers everything since construction (incl. work outside any
    // recorded phase, e.g. the --from closure build and provenance capture before analysis starts).
    public TimeSpan Elapsed => _wall.Elapsed;

    // Begin background CPU/memory/disk sampling on the master clock. Idempotent; no-op if already sampling.
    public void StartSampling(int intervalMs = 250)
    {
        if (_sampler is not null)
        {
            return;
        }

        _sampler = new ResourceSampler(clock: () => _wall.Elapsed, intervalMs: intervalMs);
        _sampler.Start();
    }

    // Stop sampling and return every sample taken (empty if sampling was never started).
    public IReadOnlyList<ResourceSampler.Sample> StopSampling()
    {
        if (_sampler is null)
        {
            return [];
        }

        var samples = _sampler.Snapshot();
        _sampler.Dispose();
        _sampler = null;
        return samples;
    }
}
