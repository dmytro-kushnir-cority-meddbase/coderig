using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;

namespace Rig.Benchmarks;

// Ultra-short job for fast iteration on the no-IO compute paths. InvocationCount=1 + Monitoring measures
// one real invocation per iteration (no amortization games), one warmup, three measured iterations — a
// few seconds per benchmark vs. minutes for the default statistical job. Switch to the default job for a
// publishable final number. Built ON DefaultConfig (not a bare ManualConfig, which has "No loggers
// defined") so the standard logger/columns/exporters are present; allocation tracking is [MemoryDiagnoser].
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
