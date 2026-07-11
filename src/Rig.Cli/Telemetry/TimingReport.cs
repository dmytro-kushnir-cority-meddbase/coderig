using System.Globalization;
using Rig.Analysis;

namespace Rig.Cli.Telemetry;

// Renders the `--time` phase/resource breakdown table and dumps the raw per-sample telemetry CSV.
// Extracted from IndexCommands so any long-running command can produce the same report — `index` today,
// `impact` (which runs two indexes + a diff) being the obvious next consumer. Pure presentation over a
// PhaseTimings record + the background ResourceSampler samples it collected; holds no state.
internal static class TimingReport
{
    // Per-phase timing + resource table: each recorded phase with its share of the summed total AND the
    // CPU/RAM/disk it cost, by bucketing the background samples into the phase's [Start, End) interval. The
    // headline is cpu:self vs cpu:sys — a phase that is low-self/high-sys is bound by our child MSBuild
    // processes, not our own work; high disk with low CPU is I/O-bound. Phases are emitted in execution
    // order so the analysis sub-phases group first.
    public static void WriteBreakdown(TextWriter output, PhaseTimings timings, IReadOnlyList<ResourceSampler.Sample> samples)
    {
        var entries = timings.Entries;
        var total = entries.Sum(e => e.Elapsed.TotalSeconds);
        output.WriteLine("Timing breakdown (cpu% normalised to all cores; self=this process, sys=whole machine; gc%=time paused for GC):");
        output.WriteLine(
            $"  {"phase", -20} {"wall", 8} {"%", 6}  {"cpu:self", 8} {"cpu:sys", 8} {"gc%", 6}  {"peakRAM", 8} {"alloc/s", 9}  {"diskR", 8} {"diskW", 8}"
        );
        foreach (var entry in entries)
        {
            var inPhase = samples.Where(s => s.At >= entry.Start && s.At < entry.End).ToArray();
            var pct = total > 0 ? entry.Elapsed.TotalSeconds / total * 100 : 0;
            var secs = entry.Elapsed.TotalSeconds;
            output.WriteLine(
                $"  {entry.Name, -20} {FormatElapsed(entry.Elapsed), 8} {pct, 5:0.0}%  "
                    + $"{FormatPercent(Average(inPhase, s => s.ProcessCpuPercent)), 8} "
                    + $"{FormatPercent(Average(inPhase, s => s.SystemCpuPercent)), 8} "
                    + $"{FormatGcPercent(inPhase, secs), 6}  "
                    + $"{FormatBytes(Peak(inPhase, s => s.WorkingSetBytes)), 8} "
                    + $"{FormatRate(DiskDelta(inPhase, s => s.AllocatedBytes), secs), 9}  "
                    + $"{FormatBytes(DiskDelta(inPhase, s => s.DiskReadBytes)), 8} "
                    + $"{FormatBytes(DiskDelta(inPhase, s => s.DiskWriteBytes)), 8}"
            );
        }

        output.WriteLine($"  {"total", -20} {FormatElapsed(TimeSpan.FromSeconds(total)), 8} 100.0%");
    }

    // Dump the raw per-sample telemetry to `fileName` in `directory` (next to the store) for offline
    // plotting. Each sample is tagged with the phase it fell in (or "startup" before the first phase /
    // "tail" after the last). InvariantCulture throughout so the file parses the same on any locale.
    public static void WriteCsv(
        TextWriter output,
        string directory,
        string fileName,
        PhaseTimings timings,
        IReadOnlyList<ResourceSampler.Sample> samples
    )
    {
        if (samples.Count == 0)
        {
            return;
        }

        var path = Path.Combine(directory, fileName);
        try
        {
            File.WriteAllLines(path, BuildCsvLines(timings.Entries, samples));
            output.WriteLine($"Telemetry: {samples.Count} samples -> {path}");
        }
        catch (IOException exception)
        {
            output.WriteLine($"Telemetry: could not write {path} ({exception.Message})");
        }
    }

    // The telemetry CSV as a single string (header + one row per sample), each row tagged with its phase.
    // Empty when no samples were taken. Shared by WriteCsv (writes it to a file next to the store) and the web
    // /api/impact/telemetry endpoint (returns it as the response body for the telemetry dashboard to fetch).
    public static string BuildCsv(PhaseTimings timings, IReadOnlyList<ResourceSampler.Sample> samples) =>
        samples.Count == 0 ? "" : string.Join('\n', BuildCsvLines(timings.Entries, samples));

    private static List<string> BuildCsvLines(IReadOnlyList<PhaseTimings.PhaseEntry> entries, IReadOnlyList<ResourceSampler.Sample> samples)
    {
        var lines = new List<string>(samples.Count + 1)
        {
            "elapsed_s,phase,proc_cpu_pct,sys_cpu_pct,ws_mb,heap_mb,disk_read_cum_mb,disk_write_cum_mb,"
                + "gen0_cum,gen1_cum,gen2_cum,gc_pause_ms_cum,alloc_mb_cum",
        };
        foreach (var s in samples)
        {
            var phase = PhaseAt(entries, s.At);
            lines.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{s.At.TotalSeconds:0.0},{phase},{s.ProcessCpuPercent:0.0},{CsvDouble(s.SystemCpuPercent)},{Mb(s.WorkingSetBytes):0.0},{Mb(s.ManagedHeapBytes):0.0},{CsvMb(s.DiskReadBytes)},{CsvMb(s.DiskWriteBytes)},"
                        + $"{s.Gen0},{s.Gen1},{s.Gen2},{s.GcPauseMs:0.0},{Mb(s.AllocatedBytes):0.0}"
                )
            );
        }

        return lines;
    }

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s" : $"{elapsed.TotalSeconds:0.0}s";

    // The phase whose [Start, End) contains the sample time, else "startup" (before any phase) / "tail".
    private static string PhaseAt(IReadOnlyList<PhaseTimings.PhaseEntry> entries, TimeSpan at)
    {
        foreach (var e in entries)
        {
            if (at >= e.Start && at < e.End)
            {
                return e.Name;
            }
        }

        return entries.Count > 0 && at < entries[0].Start ? "startup" : "tail";
    }

    // Mean of a sample projection, skipping NaN (system CPU is NaN where the platform can't supply it).
    private static double Average(IReadOnlyList<ResourceSampler.Sample> samples, Func<ResourceSampler.Sample, double> select)
    {
        double sum = 0;
        var count = 0;
        foreach (var s in samples)
        {
            var v = select(s);
            if (!double.IsNaN(v))
            {
                sum += v;
                count++;
            }
        }

        return count == 0 ? double.NaN : sum / count;
    }

    private static long Peak(IReadOnlyList<ResourceSampler.Sample> samples, Func<ResourceSampler.Sample, long> select)
    {
        long peak = -1;
        foreach (var s in samples)
        {
            var v = select(s);
            if (v > peak)
            {
                peak = v;
            }
        }

        return peak;
    }

    // Bytes transferred DURING the phase = last cumulative reading minus first, over samples with a valid
    // (non-negative) counter. -1 (counter unavailable on this platform) when none qualify.
    private static long DiskDelta(IReadOnlyList<ResourceSampler.Sample> samples, Func<ResourceSampler.Sample, long> select)
    {
        long first = -1;
        long last = -1;
        foreach (var s in samples)
        {
            var v = select(s);
            if (v < 0)
            {
                continue;
            }

            if (first < 0)
            {
                first = v;
            }

            last = v;
        }

        return first < 0 ? -1 : last - first;
    }

    private static string FormatPercent(double percent) => double.IsNaN(percent) ? "n/a" : $"{percent:0}%";

    // Share of the phase wall spent paused for GC: (GC pause-ms accrued in the phase) / phase-ms. A high
    // value on a low-cpu:self phase is the smoking gun that the phase is GC-bound, not compute-bound.
    private static string FormatGcPercent(IReadOnlyList<ResourceSampler.Sample> samples, double seconds)
    {
        if (samples.Count == 0 || seconds <= 0)
        {
            return "n/a";
        }

        var pauseMs = samples[^1].GcPauseMs - samples[0].GcPauseMs;
        return $"{Math.Max(val1: 0, val2: pauseMs / (seconds * 1000) * 100):0}%";
    }

    // Allocation throughput: bytes allocated during the phase / phase seconds, formatted as a byte rate.
    private static string FormatRate(long bytes, double seconds) =>
        bytes < 0 || seconds <= 0 ? "n/a" : FormatBytes((long)(bytes / seconds)) + "/s";

    private static string FormatBytes(long bytes) =>
        bytes < 0 ? "n/a"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0}GB"
        : $"{bytes / (double)(1L << 20):0}MB";

    private static double Mb(long bytes) => bytes / (double)(1L << 20);

    private static string CsvDouble(double value) => double.IsNaN(value) ? "" : value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string CsvMb(long bytes) => bytes < 0 ? "" : Mb(bytes).ToString("0.0", CultureInfo.InvariantCulture);
}
