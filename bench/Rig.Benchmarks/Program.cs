using BenchmarkDotNet.Running;
using Rig.Benchmarks;

// Iterate fast with the ultra-short job, e.g.:
//   dotnet run -c Release --project bench/Rig.Benchmarks -- --filter *BuildTree*
//   dotnet run -c Release --project bench/Rig.Benchmarks -- --filter *Tree*   (all tree-compute paths)
// Override the store/pattern via env: RIG_BENCH_STORE, RIG_BENCH_PATTERN.
BenchmarkSwitcher.FromAssembly(typeof(TreeComputeBenchmarks).Assembly).Run(args, new ShortConfig());
