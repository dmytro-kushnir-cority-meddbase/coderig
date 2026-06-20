using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Git;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.EntryPoints.EntryPointContext;

namespace Rig.Cli.Commands;

// The store WRITERS: `index` (analyze a solution/project into a fresh or merged store), `mine` (BFS-index a
// dependency closure in parallel), and `graph` (rebuild the derived call-graph views). These create/mutate
// the .rig store rather than query it.
internal static class IndexCommands
{
    internal static Command BuildIndex(TextWriter output, TextWriter error, string workingDirectory)
    {
        var target = CommonOptions.Pattern(name: "solution", description: "Solution (.slnx/.sln/.slnf) or project (.csproj) to index.");
        var rules = CommonOptions.Rules();
        var identity = new Option<string?>("--identity") { Description = "Store identity for an append (multi-solution) index." };
        var from = new Option<string?>("--from") { Description = "Index only the entry project's transitive closure (one workspace)." };
        var parallelism = new Option<int?>("--parallelism") { Description = "Max concurrent project analyses." };
        var merge = new Option<bool>("--merge") { Description = "Accumulate into an existing store (multi-solution unified store)." };
        var includeTests = new Option<bool>("--include-tests") { Description = "Keep test projects (excluded by default)." };
        var noGraph = new Option<bool>("--no-graph")
        {
            Description = "Skip building the call-graph views after indexing (run `rig graph` later to enable the fast query path).",
        };
        var time = new Option<bool>("--time")
        {
            Description = "Print a per-phase timing breakdown (workspace build, compile+read, extract, save, graph).",
        };
        // The design-time-build cache is ON BY DEFAULT (validated on MedDBase via --verify-build-cache, 2026-06-20):
        // a project whose build inputs are unchanged skips the dominant build phase. --reuse-build-cache is kept
        // as a deprecated no-op so existing scripts don't error; --no-build-cache opts out.
        var reuseBuildCache = new Option<bool>("--reuse-build-cache")
        {
            Description = "(deprecated; the build cache is on by default) — no-op. Use --no-build-cache to disable.",
            Hidden = true,
        };
        var noBuildCache = new Option<bool>("--no-build-cache")
        {
            Description = "Disable the design-time-build cache (always do a full build; don't read or write the cache).",
        };
        var verifyBuildCache = new Option<bool>("--verify-build-cache")
        {
            Description =
                "Guardrail: build EVERY project (ignore cache hits) and diff the fresh result against the cached one, "
                + "reporting any mismatch — proves the fingerprint captures every build input before the cache is trusted.",
        };

        var cmd = new Command(name: "index", description: "Index a solution/project into a .rig store.")
        {
            target,
            rules,
            identity,
            from,
            parallelism,
            merge,
            includeTests,
            noGraph,
            time,
            reuseBuildCache,
            noBuildCache,
            verifyBuildCache,
        };

        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunIndexAsync(
                        target: pr.GetValue(target)!,
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        identity: pr.GetValue(identity),
                        fromProject: pr.GetValue(from) is { } f ? Path.GetFullPath(f) : null,
                        parallelism: pr.GetValue(parallelism),
                        merge: pr.GetValue(merge),
                        includeTests: pr.GetValue(includeTests),
                        noGraph: pr.GetValue(noGraph),
                        time: pr.GetValue(time),
                        noBuildCache: pr.GetValue(noBuildCache),
                        verifyBuildCache: pr.GetValue(verifyBuildCache),
                        output: output,
                        error: error,
                        workingDirectory: workingDirectory
                    )
            )
        );
        return cmd;
    }

    internal static async Task<int> RunIndexAsync(
        string target,
        IReadOnlyList<string> extraRules,
        string? identity,
        string? fromProject,
        int? parallelism,
        bool merge,
        bool includeTests,
        bool noGraph,
        bool time,
        bool noBuildCache,
        bool verifyBuildCache,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        var timings = time ? new PhaseTimings() : null;
        // Sample CPU (process + whole-machine) / RAM / disk on a background timer for the whole run, so the
        // --time breakdown can show WHY a phase is slow — e.g. design-time-builds is low process-CPU but high
        // system-CPU (the work is in child MSBuild processes), not just how long it took. No-op without --time.
        timings?.StartSampling();
        // Design-time-build cache: ON BY DEFAULT, lives outside the per-commit store dir so it's shared across
        // indexes. --no-build-cache opts out; --verify-build-cache forces it on (it diffs against + refreshes
        // the sidecars), so verify wins over a contradictory --no-build-cache.
        var useBuildCache = !noBuildCache || verifyBuildCache;
        var buildCacheDir = useBuildCache ? Path.Combine(StoreLayout.RigDir(workingDirectory), "dtb-cache") : null;
        // --from <csproj>: index only the transitive ProjectReference closure of the entry project
        // (minus test projects) in ONE cross-project Roslyn workspace — skips every out-of-closure
        // test/tool project before its design-time build runs. The closure is written to
        // relevant-projects.json next to the .rig store.
        IReadOnlySet<string>? scopeProjectPaths = null;
        if (fromProject is not null)
        {
            scopeProjectPaths = await BuildEntryClosureAsync(
                solutionPath: target,
                fromProject: fromProject,
                workingDirectory: workingDirectory,
                output: output,
                error: error
            );
            if (scopeProjectPaths is null)
            {
                return 2;
            }
        }

        var rules = RuleSetLoader.Load(target);

        // Capture provenance + the destination store-id up front, so the store location and commit can be
        // announced BEFORE the (long) analysis — useful when monitoring a re-index. The commit IS the
        // store-id (docs/design-impact-behavioral-diff.md §4.4-4.5).
        var provenance = GitProvenanceProbe.Capture(fromProject ?? Path.GetFullPath(target));
        var storeId = StoreLayout.NewStoreId(provenance);

        var totalWatch = Stopwatch.StartNew();
        AnalysisResult result;
        var analyzeWatch = Stopwatch.StartNew();
        try
        {
            output.WriteLine($"Indexing: {Path.GetFullPath(target)}");
            if (extraRules.Count > 0)
            {
                output.WriteLine($"Rules: {string.Join(", ", extraRules)}");
            }

            if (identity is not null)
            {
                output.WriteLine($"Identity: {identity}");
            }

            if (fromProject is not null)
            {
                output.WriteLine($"From (closure): {fromProject}  ->  {scopeProjectPaths!.Count} project(s)");
            }

            if (parallelism is not null)
            {
                output.WriteLine($"Parallelism: {parallelism}");
            }

            output.WriteLine($"Store: {Path.Combine(StoreLayout.RigDir(workingDirectory), storeId)}");
            if (provenance.Commit is { } sourceCommit)
            {
                var shortSha = sourceCommit.Length >= 12 ? sourceCommit[..12] : sourceCommit;
                output.WriteLine(
                    $"Source commit: {shortSha}{(provenance.Branch is { } b ? $" ({b})" : "")}{(provenance.Dirty ? " +dirty" : "")}"
                );
            }

            result = await SolutionAnalyzer.AnalyzeAsync(
                target,
                rules,
                progress: message => output.WriteLine($"Progress: {message}"),
                projectIdentity: identity,
                scopeProjectPaths: scopeProjectPaths,
                parallelism: parallelism,
                // Tests are EXCLUDED by default (they add graph width, not reach); `--include-tests` opts
                // them back in.
                excludeTests: !includeTests,
                timings: timings,
                buildCacheDir: buildCacheDir,
                verifyBuildCache: verifyBuildCache
            );
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            // IOException/FileNotFoundException: the solution/project path doesn't exist or can't be read
            // (a clean "Failed to load" beats an uncaught stack trace). InvalidOperationException: the
            // workspace couldn't load/bind the target.
            error.WriteLine("Failed to load solution/project for analysis.");
            error.WriteLine(exception.ToString());
            error.WriteLine("Ensure the target solution has been restored and builds successfully, then retry.");
            error.WriteLine($"  dotnet restore {target}");
            error.WriteLine($"  dotnet build {target}");
            return 2;
        }
        analyzeWatch.Stop();
        output.WriteLine($"Progress: Analysis phase done in {FormatElapsed(analyzeWatch.Elapsed)}");

        // Publish model. A standalone `index` is a full REPLACE published via write-to-temp +
        // atomic rename, so a crash can't tear the live store (and the previous index survives a
        // failed re-index). Fast/durability-off pragmas are the DEFAULT here — a corrupt temp is
        // never published, so there's nothing to protect with a journal. The sole exception is the
        // APPEND path (`--merge`, or `mine`'s `--identity`): it writes IN PLACE into the live DB from
        // (potentially parallel) writers, so it MUST keep the journal — no fast pragmas, no atomic swap.
        // --merge accumulates this solution into an existing store (multi-solution unified store): append
        // in place, dedup assemblies by content-hash via the registry, NO atomic-replace. See
        // docs/multi-solution-storage.md.
        var appendMode = identity is not null || merge; // mine, or --merge into an existing store
        var fastBulkWrite = !appendMode; // fast pragmas on the standalone atomic-publish path
        var atomicPublish = !appendMode; // replace-via-rename for a standalone index

        // Per-commit store layout: write into .rig/<store-id>/ (storeId computed above, from the commit). On
        // a standalone index, move any pre-layout flat .rig/rig.db aside once, so the per-commit layout owns
        // .rig going forward. See docs/design-impact-behavioral-diff.md §4.4.
        if (atomicPublish)
        {
            StoreLayout.BackupLegacyFlatStore(workingDirectory);
        }

        var storeDirectory = StoreLayout.NewStoreDir(workingDirectory, storeId);
        var finalDbPath = Path.Combine(storeDirectory, StoreLayout.DbFileName);
        var dbPath = atomicPublish ? finalDbPath + ".tmp" : finalDbPath;

        if (atomicPublish)
        {
            DeleteDbFiles(dbPath); // clear any leftover temp from a previous aborted run
        }

        if (merge)
        {
            // Required DB state for a merge (declare + require, never migrate): an existing store WITH
            // the assembly registry. A pre-multi-solution store is told to re-mine, not silently altered.
            if (!File.Exists(finalDbPath))
            {
                error.WriteLine("--merge requires an existing store. Run `rig index <base-solution>` first, then merge others.");
                return 2;
            }
            await using var probe = new RigDbContext(finalDbPath, pooling: false, readOnly: true);
            if (!await Writes.HasAssemblyRegistryAsync(probe))
            {
                error.WriteLine(
                    "Store predates multi-solution support (no assembly registry). Re-mine the base solution: rig index <base-solution>"
                );
                return 2;
            }
        }

        output.WriteLine(
            $"Progress: Saving run ({(fastBulkWrite ? "fast" : "durable")}{(atomicPublish ? ", atomic-publish" : ", in-place")})"
        );
        var saveWatch = Stopwatch.StartNew();
        string runId;
        await using (var context = new RigDbContext(dbPath, pooling: !atomicPublish))
        {
            await context.Database.EnsureCreatedAsync();
            runId = await Writes.SaveAsync(
                context,
                result,
                fastBulkWrite: fastBulkWrite,
                progress: message => output.WriteLine($"Progress: {message}"),
                provenance: provenance
            );
        }

        if (atomicPublish)
        {
            DeleteDbFiles(finalDbPath); // drop the old published store + any sidecars
            File.Move(sourceFileName: dbPath, destFileName: finalDbPath, overwrite: true);
        }

        // Point read commands at this store as the latest-indexed one.
        StoreLayout.WriteLatestPointer(workingDirectory, storeId);
        saveWatch.Stop();
        totalWatch.Stop();
        timings?.Record("save", saveWatch.Elapsed);
        output.WriteLine(
            $"Progress: Save phase done in {FormatElapsed(saveWatch.Elapsed)}  (analysis {FormatElapsed(analyzeWatch.Elapsed)}, total {FormatElapsed(totalWatch.Elapsed)})"
        );

        output.WriteLine($"Indexed: {Path.GetFullPath(result.SolutionPath)}");
        output.WriteLine($"Run: {runId}");
        output.WriteLine($"Symbols: {result.Symbols?.Count ?? 0}");
        output.WriteLine($"References: {result.References?.Count ?? 0}");
        output.WriteLine($"DiRegistrations: {result.DiRegistrations.Count}");

        // Build the call-graph views now so the store is query-ready on the fast SQL path immediately —
        // no forgotten `rig graph` follow-up (the reason a "fresh" store kept paying the full in-memory
        // graph load per query). Idempotent; opt out with --no-graph. Skipped for append/merge — `mine`
        // builds once after all batches, and a --merge accumulation rebuilds via a final `rig graph`.
        if (!noGraph && !appendMode)
        {
            output.WriteLine("Progress: Building call-graph views");
            var graphWatch = Stopwatch.StartNew();
            await MaterializeGraphAsync(finalDbPath, rules, result, workingDirectory, output);
            graphWatch.Stop();
            timings?.Record("graph", graphWatch.Elapsed);
        }

        if (timings is not null)
        {
            var samples = timings.StopSampling();
            WriteTimingBreakdown(output, timings, samples);
            WriteTelemetryCsv(output, workingDirectory, timings, samples);
        }

        return 0;
    }

    // Per-phase timing + resource table for `rig index --time`: each recorded phase with its share of the
    // summed total AND the CPU/RAM/disk it cost, by bucketing the background samples into the phase's
    // [Start, End) interval. The headline is cpu:self vs cpu:sys — a phase that is low-self/high-sys is
    // bound by our child MSBuild processes, not our own work; high disk with low CPU is I/O-bound. Phases
    // are emitted in execution order so the analysis sub-phases group first.
    private static void WriteTimingBreakdown(TextWriter output, PhaseTimings timings, IReadOnlyList<ResourceSampler.Sample> samples)
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

    // Dump the raw per-sample telemetry to a CSV next to the store (working dir) for offline plotting. Each
    // sample is tagged with the phase it fell in (or "startup" before the first phase / "tail" after the
    // last). InvariantCulture throughout so the file parses the same on any locale.
    private static void WriteTelemetryCsv(
        TextWriter output,
        string workingDirectory,
        PhaseTimings timings,
        IReadOnlyList<ResourceSampler.Sample> samples
    )
    {
        if (samples.Count == 0)
        {
            return;
        }

        var entries = timings.Entries;
        var path = Path.Combine(workingDirectory, "rig-index-telemetry.csv");
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

        try
        {
            File.WriteAllLines(path, lines);
            output.WriteLine($"Telemetry: {samples.Count} samples -> {path}");
        }
        catch (IOException exception)
        {
            output.WriteLine($"Telemetry: could not write {path} ({exception.Message})");
        }
    }

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
    private static string FormatRate(long bytes, double seconds) => bytes < 0 || seconds <= 0 ? "n/a" : FormatBytes((long)(bytes / seconds)) + "/s";

    private static string FormatBytes(long bytes) =>
        bytes < 0 ? "n/a"
        : bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0}GB"
        : $"{bytes / (double)(1L << 20):0}MB";

    private static double Mb(long bytes) => bytes / (double)(1L << 20);

    private static string CsvDouble(double value) => double.IsNaN(value) ? "" : value.ToString("0.0", CultureInfo.InvariantCulture);

    private static string CsvMb(long bytes) => bytes < 0 ? "" : Mb(bytes).ToString("0.0", CultureInfo.InvariantCulture);

    // Build the derived call-graph views (call_edges + dispatch_edges) + the EP-site table into the store at
    // dbPath, using the already-loaded rules passed in by the caller (no second rule load). Run as the tail of
    // `index` so a freshly-indexed store is query-ready on the fast SQL path without a manual follow-up.
    // Idempotent — rerun any time, no rescan.
    private static async Task MaterializeGraphAsync(string dbPath, RuleSet rules, AnalysisResult result, string workingDirectory, TextWriter output)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var context = new RigDbContext(dbPath);
        // Build the call graph from the facts we just extracted (in memory) instead of re-reading the whole
        // fact store back off disk — FactGraphProjection.FromAnalysis is the field-for-field equivalent of
        // Reads.LoadFactGraphAsync. Classification rules flow in here so call_edges is written with
        // Kind="handoff" baked in; generic-factory rules flow to BuildFromGraphAsync so the factory
        // monomorphization is baked into call_edges (so the SQL bounding walk sees the rewritten edges the
        // in-memory traversal does — no effect-path divergence).
        var graph = FactGraphProjection.FromAnalysis(result, rules.Handoff);
        var stats = await GraphMaterializer.BuildFromGraphAsync(
            context,
            graph,
            rules.Factory,
            message => output.WriteLine($"Progress: {message}")
        );
        output.WriteLine(
            $"Graph: {stats.CallEdges} call edge(s), {stats.DispatchEdges} dispatch edge(s) "
                + $"({stats.DispatchEdges - stats.HeuristicDispatchEdges} roslyn-mined, {stats.HeuristicDispatchEdges} heuristic), "
                + $"{stats.Nodes} node(s) in {FormatElapsed(stopwatch.Elapsed)}"
        );
        // Materialize the pattern-independent EP-site set into a table now, so every later query reads it
        // directly instead of re-deriving from the whole-store fact tables. No-op without deployments.json.
        await MaterializeEntryPointSitesAsync(context, workingDirectory);
    }


    // Transitive ProjectReference closure of an entry project, minus test projects — the build scope
    // for `rig index --from`. Parses the dependency graph (XML only, no MSBuild), BFS from the entry,
    // drops test projects by name, and writes the closure to relevant-projects.json next to the .rig
    // store. Returns the normalised full project paths to build, or null on a usage error.
    private static async Task<IReadOnlySet<string>?> BuildEntryClosureAsync(
        string solutionPath,
        string fromProject,
        string workingDirectory,
        TextWriter output,
        TextWriter error
    )
    {
        var solutionFull = Path.GetFullPath(solutionPath);
        if (solutionFull.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            error.WriteLine("--from is only valid when indexing a solution (.slnx/.sln), not a single project.");
            return null;
        }

        var depGraph = await DependencyGraph.BuildAsync(solutionFull, output);
        var entry = Path.GetFullPath(fromProject);
        if (!depGraph.ContainsKey(entry))
        {
            error.WriteLine($"--from project not found in solution: {entry}");
            return null;
        }

        var visited = DependencyGraph.TransitiveClosure(entry, depGraph);

        // Drop test projects. Production projects don't reference them, so the closure is normally
        // test-free already; this honours --from's "without tests" contract defensively.
        var excludedTests = visited.Where(IsTestProjectPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var t in excludedTests)
        {
            visited.Remove(t);
        }

        var listPath = Path.Combine(workingDirectory, "relevant-projects.json");
        WriteJsonSidecar(
            listPath,
            new
            {
                solutionPath = solutionFull,
                entryProject = entry,
                projectCount = visited.Count,
                excludedTestProjects = excludedTests,
                projects = visited.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray(),
            }
        );
        output.WriteLine($"Entry closure: {visited.Count} project(s), {excludedTests.Length} test project(s) excluded -> {listPath}");

        return visited;
    }

    private static bool IsTestProjectPath(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("UnitTests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("IntegrationTests", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".Tests.", StringComparison.OrdinalIgnoreCase);
    }

    // Delete a SQLite DB file and its WAL/SHM/rollback-journal sidecars, ignoring missing files.
    private static void DeleteDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            var p = dbPath + suffix;
            if (File.Exists(p))
            {
                File.Delete(p);
            }
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:00}s" : $"{elapsed.TotalSeconds:0.0}s";

    private static string ComputeIdentity(string solutionPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(solutionPath))))[..16];

    // The indented-JSON sidecar write shared by `index --from` (relevant-projects.json) and `mine`
    // (reachable-projects.json).
    private static void WriteJsonSidecar(string path, object data) =>
        File.WriteAllText(path: path, contents: JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
}
