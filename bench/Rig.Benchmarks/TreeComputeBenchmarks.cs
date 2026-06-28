using BenchmarkDotNet.Attributes;
using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;

namespace Rig.Benchmarks;

// The PURE, no-IO compute of a `rig tree` query, isolated from the SQLite load: ALL IO happens once in
// [GlobalSetup] (open the store read-only, load reach inputs + rules + EP data into memory), and every
// [Benchmark] runs over those in-memory inputs only — so measurements attribute to compute, not IO.
//   Store:   env RIG_BENCH_STORE   — default: rig's OWN indexed source (the repo-root .rig store), so the
//            benchmark is SELF-CONTAINED and needs no external store. Index it once with:
//              dotnet run -c Release --project src/Rig.Cli -- index RuntimeIntelligenceGraph.slnx
//            (~13s). Point RIG_BENCH_STORE at the MedDBase store for a large-scale run instead.
//   Pattern: env RIG_BENCH_PATTERN (default RunIndexAsync) — the traversal root, a meaty rig method.
[MemoryDiagnoser]
public class TreeComputeBenchmarks
{
    private FactGraphData _rawGraph = null!; // unshaped bounded graph (input to ShapeGraph)
    private FactGraphData _shapedGraph = null!; // shaped (input to MarkEventSubscriptionHandoffs)
    private FactGraphData _markedGraph = null!; // shaped + event-marked (input to BuildTree)
    private ISet<EventSubscriptionSite> _eventSites = null!;

    private IReadOnlyList<FactInvocation> _invocations = null!;
    private IReadOnlyList<(string, string)> _baseEdges = null!;
    private IReadOnlyList<SymbolRef> _ctorRefs = null!;
    private IReadOnlyList<SymbolRef> _throwRefs = null!;

    private FactEntryPointDeriver.FactEntryPointData _epData = null!;

    private IReadOnlyList<FactGenericFactoryRule> _factoryRules = null!;
    private IReadOnlyList<FactTraversalCutRule> _cutRules = null!;
    private IReadOnlyList<FactContextDispatchRule> _contextRules = null!;
    private IReadOnlyList<FactEffectRule> _effectRules = null!;
    private FactObservationRules _observationRules = null!;
    private IReadOnlyList<FactEntryPointRule> _epRules = null!;
    private IReadOnlyList<FactClassInheritanceRule> _classRules = null!;

    private string _pattern = null!;

    // Walk up from the running assembly to the repo root (the dir holding the rig solution), whose .rig is
    // rig's own self-indexed store — so the benchmark defaults to rig's own source, no external store.
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "RuntimeIntelligenceGraph.slnx")))
        {
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return dir ?? AppContext.BaseDirectory;
    }

    // rig writes COMMIT-SCOPED stores: .rig/<short-sha>[-dirty]/rig.db, with a LATEST file naming the
    // current one (NOT a flat .rig/rig.db). Resolve the db the way rig does — via LATEST — falling back to
    // a flat layout or the newest commit-scoped subdir. Returns null when no store exists under <root>.
    private static string? ResolveDbPath(string root)
    {
        var rigDir = Path.Combine(root, ".rig");
        if (!Directory.Exists(rigDir))
        {
            return null;
        }

        var latest = Path.Combine(rigDir, "LATEST");
        if (File.Exists(latest))
        {
            var db = Path.Combine(rigDir, File.ReadAllText(latest).Trim(), "rig.db");
            if (File.Exists(db))
            {
                return db;
            }
        }

        var flat = Path.Combine(rigDir, "rig.db");
        if (File.Exists(flat))
        {
            return flat;
        }

        return Directory
            .GetDirectories(rigDir)
            .Select(d => Path.Combine(d, "rig.db"))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var dir = Environment.GetEnvironmentVariable("RIG_BENCH_STORE") ?? RepoRoot();
        _pattern = Environment.GetEnvironmentVariable("RIG_BENCH_PATTERN") ?? "RunIndexAsync";
        var dbPath = ResolveDbPath(dir);
        if (dbPath is null)
        {
            throw new FileNotFoundException(
                $"No indexed store under {Path.Combine(dir, ".rig")}. Index rig's own source first:\n"
                    + "  dotnet run -c Release --project src/Rig.Cli -- index RuntimeIntelligenceGraph.slnx\n"
                    + "or set RIG_BENCH_STORE to an existing indexed store (e.g. the MedDBase store)."
            );
        }

        await using var context = new RigDbContext(dbPath, readOnly: true);

        var rules = RuleSetLoader.Load(dir);
        _factoryRules = rules.Factory;
        _cutRules = rules.Cut;
        _contextRules = rules.Context;
        _effectRules = rules.Effects;
        _observationRules = rules.Observations;
        _epRules = rules.EntryPoints;
        _classRules = rules.ClassInheritance;

        var inputs = await SqlReachability.LoadReachInputsAsync(context, _pattern, SqlReachability.Direction.Forward);
        _rawGraph = inputs.Graph;
        _invocations = inputs.Invocations;
        _ctorRefs = inputs.CtorRefs;
        _throwRefs = inputs.ThrowRefs;
        _baseEdges = (_rawGraph.BaseEdges ?? []).Select(e => (e.SubType, e.BaseType)).ToArray();

        _shapedGraph = FactPathFinder.ShapeGraph(_rawGraph, _factoryRules, _cutRules, _contextRules);
        _eventSites = await Reads.EventSubscriptionSitesAsync(context);
        _markedGraph = FactPathFinder.MarkEventSubscriptionHandoffs(_shapedGraph, _eventSites);

        _epData = await Reads.LoadFactEntryPointDataAsync(context);
    }

    [Benchmark]
    public FactGraphData ShapeGraph() => FactPathFinder.ShapeGraph(_rawGraph, _factoryRules, _cutRules, _contextRules);

    [Benchmark]
    public FactGraphData MarkEventHandoffs() => FactPathFinder.MarkEventSubscriptionHandoffs(_shapedGraph, _eventSites);

    [Benchmark]
    public IReadOnlyList<TraceNode> BuildTree() => FactPathFinder.BuildTree(_markedGraph, _pattern);

    // For the no-BDN gcdump harness (Program.cs `gcloop`): set up the inputs ONCE, then hand back a thunk
    // that runs one BuildTree — so a tight loop + a heap/alloc capture can profile it outside BDN's
    // per-process noise.
    internal async Task<Func<IReadOnlyList<TraceNode>>> PrepareBuildTreeAsync()
    {
        await SetupAsync();
        return () => FactPathFinder.BuildTree(_markedGraph, _pattern);
    }

    [Benchmark]
    public IReadOnlyList<DerivedEffect> DeriveEffects() =>
        FactEffectDeriver.Derive(
            _invocations,
            _effectRules,
            providerFilter: null,
            baseEdges: _baseEdges,
            ctorRefs: _ctorRefs,
            observationRules: _observationRules,
            throwRefs: _throwRefs
        );

    [Benchmark]
    public IReadOnlyList<DerivedEntryPoint> DeriveEntryPoints() => FactEntryPointDeriver.Derive(_epData, _epRules, _classRules);
}
