using Rig.Analysis;

namespace Rig.Cli.Telemetry;

// Disposable `--time` scope for the query commands. Collapses the repeated
//   var timings = enabled ? new PhaseTimings() : null;
//   timings?.StartSampling();
//   ... if (timings is not null) { var s = timings.StopSampling(); TimingReport.WriteBreakdown(err, timings, s); }
// boilerplate (which otherwise has to be copy-pasted at EVERY return point — callers has 7) into one
// `using var t = QueryTiming.Start(opts.Time, io.TextOutput.Error);` + `t.Record(...)` per phase. Dispose()
// emits the breakdown to stderr at scope exit, so it fires on every return path automatically. Disabled
// (the default) it is a no-op — no PhaseTimings, no sampler, Record/Dispose return immediately.
internal sealed class QueryTiming : IDisposable
{
    private readonly PhaseTimings? _timings;
    private readonly TextWriter _writer;
    private bool _disposed;

    private QueryTiming(PhaseTimings? timings, TextWriter writer)
    {
        _timings = timings;
        _writer = writer;
    }

    // `writer` MUST be stderr (io.TextOutput.Error) — the breakdown must never pollute stdout/--format output.
    public static QueryTiming Start(bool enabled, TextWriter writer)
    {
        PhaseTimings? timings = enabled ? new PhaseTimings() : null;
        timings?.StartSampling();
        return new QueryTiming(timings, writer);
    }

    public void Record(string phase, TimeSpan elapsed) => _timings?.Record(phase, elapsed);

    public void Dispose()
    {
        if (_timings is null || _disposed)
        {
            return;
        }

        _disposed = true;
        var samples = _timings.StopSampling();
        TimingReport.WriteBreakdown(output: _writer, timings: _timings, samples: samples);
    }
}
