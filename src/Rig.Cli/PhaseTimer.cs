using System.Diagnostics;

namespace Rig.Cli;

// Opt-in per-phase timing for the query commands (`--time`). Prints "[time] <phase>: <ms>" to stderr —
// stderr (not stdout) so it never pollutes piped/--format output. Disabled (the default) it is a no-op:
// the Stopwatches are never allocated and every Lap/Total returns immediately, so it costs a null check.
internal sealed class PhaseTimer
{
    private readonly TextWriter? _writer;
    private readonly Stopwatch? _phase;
    private readonly Stopwatch? _total;

    public PhaseTimer(bool enabled, TextWriter writer)
    {
        if (!enabled)
        {
            return;
        }

        _writer = writer;
        _phase = Stopwatch.StartNew();
        _total = Stopwatch.StartNew();
    }

    public void Lap(string phase)
    {
        if (_writer is null)
        {
            return;
        }

        _writer.WriteLine($"[time] {phase}: {_phase!.ElapsedMilliseconds} ms");
        _phase.Restart();
    }

    public void Total()
    {
        _writer?.WriteLine($"[time] total: {_total!.ElapsedMilliseconds} ms");
    }
}
