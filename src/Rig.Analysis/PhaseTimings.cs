namespace Rig.Analysis;

// Optional per-phase timing collector for `rig index --time`. Phases are recorded in completion order
// from the ORCHESTRATING thread only — always after the awaited work for that phase has fully
// completed, never from the parallel workers inside a phase — so the appends are sequential (a
// happens-before chain through the awaits) and no locking is needed.
public sealed class PhaseTimings
{
    private readonly List<KeyValuePair<string, TimeSpan>> _entries = [];

    public void Record(string name, TimeSpan elapsed) => _entries.Add(new KeyValuePair<string, TimeSpan>(name, elapsed));

    public IReadOnlyList<KeyValuePair<string, TimeSpan>> Entries => _entries;
}
