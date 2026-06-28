using System.Diagnostics;
using BenchmarkDotNet.Running;
using Rig.Benchmarks;
using Rig.Domain.Data;

// `gcloop`: BYPASS BenchmarkDotNet — run 100 BuildTree calls in a tight loop, report total allocation +
// GC pressure, HOLD the results, then collect a gcdump of OURSELVES via an external `dotnet-gcdump collect
// -p <self>` call:
//   dotnet run -c Release --project bench/Rig.Benchmarks -- gcloop
//   dotnet-gcdump report <printed .gcdump path>     (text type/size table)
// The gcdump shows the RETAINED output (the held TraceNode forests); transient churn (the per-call
// BuildIndex, MutableNodes) is reflected in the gen0 count below.
if (args.Length > 0 && args[0] == "gcloop")
{
    const int iterations = 100;

    var bench = new TreeComputeBenchmarks();
    var buildTree = await bench.PrepareBuildTreeAsync();

    for (var i = 0; i < 3; i++)
    {
        buildTree(); // warm up / JIT before measuring
    }

    var results = new List<IReadOnlyList<TraceNode>>(iterations);
    var g0 = GC.CollectionCount(0);
    var g1 = GC.CollectionCount(1);
    var g2 = GC.CollectionCount(2);
    var before = GC.GetAllocatedBytesForCurrentThread();
    var sw = Stopwatch.StartNew();
    for (var i = 0; i < iterations; i++)
    {
        results.Add(buildTree());
    }
    sw.Stop();
    var alloc = GC.GetAllocatedBytesForCurrentThread() - before;

    Console.WriteLine(
        $"{iterations}x BuildTree: {alloc / 1048576.0:F1} MB total | {alloc / (double)iterations / 1024:F0} KB/call | {sw.Elapsed.TotalMilliseconds / iterations:F2} ms/call"
    );
    Console.WriteLine(
        $"GC over loop: gen0 +{GC.CollectionCount(0) - g0}  gen1 +{GC.CollectionCount(1) - g1}  gen2 +{GC.CollectionCount(2) - g2}"
    );

    // Collect a gcdump of OURSELVES by shelling out to dotnet-gcdump with our own PID. The runtime's
    // diagnostic-server thread services the heap-dump request while this main thread blocks on the child;
    // the held `results` keep the retained TraceNode forests live in the snapshot. (gcdump forces a full GC
    // as part of capture, so it shows RETAINED heap — the output footprint; transient churn shows in the
    // gen0 count above.)  Analyse with: dotnet-gcdump report <file>
    var dumpPath = Path.Combine(Path.GetTempPath(), $"build-tree-{Environment.ProcessId}.gcdump");
    var toolExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools", "dotnet-gcdump.exe");
    if (!File.Exists(toolExe))
    {
        toolExe = "dotnet-gcdump"; // fall back to PATH
    }

    Console.WriteLine($"PID={Environment.ProcessId} holding {results.Count} forests; collecting gcdump -> {dumpPath}");
    var psi = new ProcessStartInfo(toolExe, $"collect -p {Environment.ProcessId} -o \"{dumpPath}\"")
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    using var proc = Process.Start(psi)!;
    var stdout = proc.StandardOutput.ReadToEnd();
    var stderr = proc.StandardError.ReadToEnd();
    proc.WaitForExit();
    Console.WriteLine(stdout);
    if (!string.IsNullOrWhiteSpace(stderr))
    {
        Console.WriteLine("gcdump stderr: " + stderr);
    }
    Console.WriteLine($"gcdump exit={proc.ExitCode}  file={dumpPath}");
    GC.KeepAlive(results);
    return;
}

// Iterate fast with the ultra-short job (ShortConfig), e.g.:
//   dotnet run -c Release --project bench/Rig.Benchmarks -- --filter *BuildTree*
// Override the store/pattern via env: RIG_BENCH_STORE, RIG_BENCH_PATTERN.
BenchmarkSwitcher.FromAssembly(typeof(TreeComputeBenchmarks).Assembly).Run(args, new ShortConfig());
