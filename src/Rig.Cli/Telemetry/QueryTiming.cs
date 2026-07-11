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
    private readonly string? _csvDirectory;
    private readonly string? _csvFileName;
    private bool _disposed;

    private QueryTiming(PhaseTimings? timings, TextWriter writer, string? csvDirectory, string? csvFileName)
    {
        _timings = timings;
        _writer = writer;
        _csvDirectory = csvDirectory;
        _csvFileName = csvFileName;
    }

    // `writer` MUST be stderr (io.TextOutput.Error) — the breakdown must never pollute stdout/--format output.
    // When csvDirectory + csvFileName are supplied, Dispose ALSO dumps the raw per-sample telemetry CSV there
    // (the same rig-*-telemetry.csv format the telemetry dashboard renders) — off by default, so the query
    // commands that only want the stderr table are unaffected.
    public static QueryTiming Start(bool enabled, TextWriter writer, string? csvDirectory = null, string? csvFileName = null)
    {
        PhaseTimings? timings = enabled ? new PhaseTimings() : null;
        timings?.StartSampling();
        return new QueryTiming(timings, writer, csvDirectory, csvFileName);
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
        if (_csvDirectory is not null && _csvFileName is not null)
        {
            TimingReport.WriteCsv(output: _writer, directory: _csvDirectory, fileName: _csvFileName, timings: _timings, samples: samples);
        }
    }
}
