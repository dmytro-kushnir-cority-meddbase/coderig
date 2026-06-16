using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace Rig.Benchmarks;

// Ultra-short job for fast iteration on the no-IO compute paths. These ops run 10ms–1s+ each, so the
// usual statistical job (15 warmups × 100 invocations) would take minutes per benchmark. Monitoring +
// InvocationCount=1 measures one real invocation per iteration (no amortization games), with a single
// warmup and three measured iterations — a few seconds per benchmark, enough signal to see a regression
// or an alloc win while iterating. Switch to the default job for a publishable final number.
//
// Built on DefaultConfig so the standard console logger, columns, and exporters are present (a bare
// ManualConfig has none — "No loggers defined"). Allocation tracking comes from [MemoryDiagnoser] on the
// benchmark class — allocation count is a first-class target here, not just wall time.
public sealed class ShortConfig : ManualConfig
{
    public ShortConfig()
    {
        Add(DefaultConfig.Instance);
        AddJob(
            Job.Default.WithStrategy(RunStrategy.Monitoring)
                .WithLaunchCount(1)
                .WithWarmupCount(1)
                .WithIterationCount(3)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithId("short")
        );
    }
}
