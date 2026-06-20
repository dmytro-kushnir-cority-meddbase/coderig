using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Rig.Analysis;

// Background resource sampler for `rig index --time`. Wall-clock alone hides WHY a phase is slow, and the
// MedDBase numbers prove it: the out-of-process MSBuild design-time builds burn whole-machine CPU while
// THIS process sits nearly idle, and the disk-bound phases (metadata reads in workspace-assembly, the bulk
// save) show low CPU but heavy I/O and swing wildly with the OS file cache (workspace-assembly measured
// 1m41s cold vs 4.4s warm with no code change). So, on a background timer, we sample:
//   - process CPU%  — THIS process, normalised so 100 == every logical core busy with our work
//   - system CPU%   — the whole machine (captures the out-of-proc MSBuild/VBCSCompiler children we spawn)
//   - working set + managed heap — the memory ceiling that bounds how high --parallelism can safely go
//   - process disk read/write bytes (cumulative) — our own I/O (metadata reads, the bulk save)
// Samples are timestamped on the SHARED PhaseTimings master clock, so each is attributed to the phase it
// fell in. The three managed signals (process CPU / working set / heap) are one cross-platform code path;
// the two OS-specific signals (system CPU / process disk) sit behind IPlatformProbe — WindowsProbe (kernel32
// via [LibraryImport]), LinuxProbe (/proc reads, no P/Invoke), NullProbe (macOS/other → NaN/-1). Every probe
// is best-effort: a failure degrades that column rather than failing the index. Sampling cost is a handful
// of counter reads per interval — negligible, and gated entirely behind --time.
public sealed class ResourceSampler : IDisposable
{
    // One point-in-time reading. CPU percentages are rates over the prior interval; disk bytes are the
    // process-lifetime CUMULATIVE counters (the per-phase delta is taken at render time). System CPU is NaN
    // and disk is -1 where the platform can't supply them.
    [StructLayout(LayoutKind.Auto)]
    public readonly record struct Sample(
        TimeSpan At,
        double ProcessCpuPercent,
        double SystemCpuPercent,
        long WorkingSetBytes,
        long ManagedHeapBytes,
        long DiskReadBytes,
        long DiskWriteBytes
    );

    private readonly Func<TimeSpan> _clock;
    private readonly int _intervalMs;
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly List<Sample> _samples = [];
    private readonly Lock _gate = new();
    private readonly double _cpuCount = Math.Max(val1: 1, val2: Environment.ProcessorCount);
    private readonly IPlatformProbe _probe = CreateProbe();

    private Timer? _timer;
    private volatile bool _stopped;
    private TimeSpan _lastWall;
    private TimeSpan _lastProcCpu;

    public ResourceSampler(Func<TimeSpan> clock, int intervalMs)
    {
        _clock = clock;
        _intervalMs = Math.Max(val1: 50, val2: intervalMs);
    }

    // Prime the delta baselines so the first real sample reports a rate over the first interval rather than
    // a cold spike, then arm a non-reentrant timer (fixed delay, re-armed at the end of each tick).
    public void Start()
    {
        _lastWall = _clock();
        _self.Refresh();
        _lastProcCpu = _self.TotalProcessorTime;
        _probe.Prime();
        _timer = new Timer(callback: _ => Tick(), state: null, dueTime: _intervalMs, period: Timeout.Infinite);
    }

    public IReadOnlyList<Sample> Snapshot()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }

    private void Tick()
    {
        try
        {
            var now = _clock();
            var wallDelta = (now - _lastWall).TotalSeconds;
            if (wallDelta <= 0)
            {
                return;
            }

            _self.Refresh();
            var procCpu = _self.TotalProcessorTime;
            var procPercent = (procCpu - _lastProcCpu).TotalSeconds / wallDelta / _cpuCount * 100.0;
            var systemPercent = _probe.SystemCpuPercent();
            var (read, write) = _probe.ProcessDiskBytes();

            var sample = new Sample(
                At: now,
                ProcessCpuPercent: procPercent,
                SystemCpuPercent: systemPercent,
                WorkingSetBytes: _self.WorkingSet64,
                ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
                DiskReadBytes: read,
                DiskWriteBytes: write
            );
            lock (_gate)
            {
                _samples.Add(sample);
            }

            _lastWall = now;
            _lastProcCpu = procCpu;
        }
        catch
        {
            // Best-effort telemetry: a transient probe failure must never disturb the index.
        }
        finally
        {
            if (!_stopped)
            {
                _timer?.Change(dueTime: _intervalMs, period: Timeout.Infinite);
            }
        }
    }

    public void Dispose()
    {
        _stopped = true;
        _timer?.Dispose();
        _timer = null;
        _self.Dispose();
    }

    private static IPlatformProbe CreateProbe() =>
        OperatingSystem.IsWindows() ? new WindowsProbe()
        : OperatingSystem.IsLinux() ? new LinuxProbe()
        : new NullProbe();

    // The two OS-specific signals + their delta priming. SystemCpuPercent returns the whole-machine busy %
    // since the previous call (NaN if unavailable); ProcessDiskBytes returns the process-lifetime cumulative
    // read/write byte counters ((-1,-1) if unavailable). Implementations hold their own delta state.
    private interface IPlatformProbe
    {
        void Prime();
        double SystemCpuPercent();
        (long Read, long Write) ProcessDiskBytes();
    }

    // macOS / unknown: only the three managed signals are captured; these two show blank.
    private sealed class NullProbe : IPlatformProbe
    {
        public void Prime() { }

        public double SystemCpuPercent() => double.NaN;

        public (long Read, long Write) ProcessDiskBytes() => (-1, -1);
    }

    // Windows: GetSystemTimes (whole-machine kernel/user/idle ticks; kernel INCLUDES idle) for system CPU,
    // GetProcessIoCounters (against the current-process pseudo-handle) for cumulative process disk bytes.
    [SupportedOSPlatform("windows")]
    private sealed class WindowsProbe : IPlatformProbe
    {
        private long _idle;
        private long _kernel;
        private long _user;
        private bool _have;

        public void Prime() => _have = GetSystemTimes(lpIdleTime: out _idle, lpKernelTime: out _kernel, lpUserTime: out _user);

        public double SystemCpuPercent()
        {
            if (!_have || !GetSystemTimes(lpIdleTime: out var idle, lpKernelTime: out var kernel, lpUserTime: out var user))
            {
                return double.NaN;
            }

            var idleDelta = idle - _idle;
            var totalDelta = (kernel - _kernel) + (user - _user); // kernel already includes idle
            _idle = idle;
            _kernel = kernel;
            _user = user;
            return totalDelta > 0 ? (totalDelta - idleDelta) / (double)totalDelta * 100.0 : double.NaN;
        }

        public (long Read, long Write) ProcessDiskBytes()
        {
            try
            {
                return GetProcessIoCounters(hProcess: GetCurrentProcess(), lpIoCounters: out var counters)
                    ? ((long)counters.ReadTransferCount, (long)counters.WriteTransferCount)
                    : (-1, -1);
            }
            catch
            {
                return (-1, -1);
            }
        }

        // DllImport (not LibraryImport): the source-generated marshaller emits `unsafe` code, which this
        // assembly doesn't enable — and turning on AllowUnsafeBlocks for telemetry isn't warranted. These
        // three signatures are blittable, so classic P/Invoke is fine; suppress the SYSLIB1054 "prefer
        // LibraryImport" suggestion (an error under -warnaserror) the same way the Buildalyzer call sites
        // suppress CS0618.
#pragma warning disable SYSLIB1054 // Use 'LibraryImportAttribute' instead of 'DllImportAttribute'
        // FILETIME is two DWORDs (8 bytes) — marshalled as `long` directly.
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

        // Pseudo-handle ((HANDLE)-1) for the current process; needs no close, so no allocation/leak per tick.
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters lpIoCounters);
#pragma warning restore SYSLIB1054

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }
    }

    // Linux: /proc/stat first line ("cpu  user nice system idle iowait irq softirq steal …") for system CPU,
    // /proc/self/io (read_bytes / write_bytes) for cumulative process disk bytes. Plain file reads, no P/Invoke.
    [SupportedOSPlatform("linux")]
    private sealed class LinuxProbe : IPlatformProbe
    {
        private long _idleLast;
        private long _totalLast;
        private bool _have;

        public void Prime() => _have = TryReadStat(idle: out _idleLast, total: out _totalLast);

        public double SystemCpuPercent()
        {
            if (!_have || !TryReadStat(idle: out var idle, total: out var total))
            {
                return double.NaN;
            }

            var idleDelta = idle - _idleLast;
            var totalDelta = total - _totalLast;
            _idleLast = idle;
            _totalLast = total;
            return totalDelta > 0 ? (totalDelta - idleDelta) / (double)totalDelta * 100.0 : double.NaN;
        }

        public (long Read, long Write) ProcessDiskBytes()
        {
            try
            {
                long read = -1;
                long write = -1;
                foreach (var line in File.ReadLines("/proc/self/io"))
                {
                    if (line.StartsWith("read_bytes:", StringComparison.Ordinal))
                    {
                        read = ParseTail(line);
                    }
                    else if (line.StartsWith("write_bytes:", StringComparison.Ordinal))
                    {
                        write = ParseTail(line);
                    }
                }

                return (read, write);
            }
            catch
            {
                return (-1, -1);
            }
        }

        // Aggregate "cpu" line: idle is field index 4 (after the "cpu" label); total is the sum of all fields.
        private static bool TryReadStat(out long idle, out long total)
        {
            idle = 0;
            total = 0;
            try
            {
                var first = File.ReadLines("/proc/stat").FirstOrDefault();
                if (first is null || !first.StartsWith("cpu ", StringComparison.Ordinal))
                {
                    return false;
                }

                var parts = first.Split(separator: ' ', options: StringSplitOptions.RemoveEmptyEntries);
                long sum = 0;
                for (var i = 1; i < parts.Length; i++)
                {
                    if (long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                    {
                        sum += value;
                        if (i == 4)
                        {
                            idle = value;
                        }
                    }
                }

                total = sum;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long ParseTail(string line)
        {
            var colon = line.IndexOf(value: ':', comparisonType: StringComparison.Ordinal);
            return
                colon >= 0
                && long.TryParse(line.AsSpan(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : -1;
        }
    }
}
