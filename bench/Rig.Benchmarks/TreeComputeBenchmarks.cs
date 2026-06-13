using BenchmarkDotNet.Attributes;
using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;

namespace Rig.Benchmarks;

// The PURE, no-IO compute of a `rig tree` query, isolated from the SQLite load. All IO happens once in
// [GlobalSetup] (open the store read-only, load the bounded reach inputs + rules + EP data into memory);
// every [Benchmark] then runs over those in-memory inputs only. Targets the phases the timing harness
// attributes to compute: ShapeGraph, MarkEventSubscriptionHandoffs, BuildTree, effect derivation, and the
// in-memory entry-point derivation.
//
//   Store:   env RIG_BENCH_STORE   (default C:\Git\meddbase-analysis) — must contain .rig\rig.db + rules.
//   Pattern: env RIG_BENCH_PATTERN (default Master.SubmitToHealthcode) — the traversal root.
[MemoryDiagnoser]
public class TreeComputeBenchmarks
{
    private const int MaxDepth = 20;

    private FactGraphData _rawGraph = null!; // unshaped bounded graph (input to ShapeGraph)
    private FactGraphData _shapedGraph = null!; // shaped (input to MarkEventSubscriptionHandoffs)
    private FactGraphData _markedGraph = null!; // shaped + event-marked (input to BuildTree)
    private ISet<(string Caller, string FilePath, int Line)> _eventSites = null!;

    private IReadOnlyList<FactInvocation> _invocations = null!;
    private IReadOnlyList<(string, string)> _baseEdges = null!;
    private IReadOnlyList<(string, string?, string, int)> _ctorRefs = null!;
    private IReadOnlyList<(string, string?, string, int)> _throwRefs = null!;

    private FactEntryPointDeriver.FactEntryPointData _epData = null!;

    private IReadOnlyList<FactGenericFactoryRule> _factoryRules = null!;
    private IReadOnlyList<FactTraversalCutRule> _cutRules = null!;
    private IReadOnlyList<FactContextDispatchRule> _contextRules = null!;
    private IReadOnlyList<FactEffectRule> _effectRules = null!;
    private FactObservationRules _observationRules = null!;
    private IReadOnlyList<FactEntryPointRule> _epRules = null!;
    private IReadOnlyList<FactClassInheritanceRule> _classRules = null!;

    private string _pattern = null!;

    [GlobalSetup]
    public async Task SetupAsync()
    {
        var dir = Environment.GetEnvironmentVariable("RIG_BENCH_STORE") ?? @"C:\Git\meddbase-analysis";
        _pattern = Environment.GetEnvironmentVariable("RIG_BENCH_PATTERN") ?? "Master.SubmitToHealthcode";
        var dbPath = Path.Combine(dir, ".rig", "rig.db");
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"No rig.db at {dbPath}. Set RIG_BENCH_STORE to an indexed store.");

        await using var context = new RigDbContext(dbPath, readOnly: true);

        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(dir);
        _factoryRules = FactGenericFactoryRuleProvider.LoadForWorkingDirectory(dir);
        _cutRules = FactTraversalCutRuleProvider.LoadForWorkingDirectory(dir);
        _contextRules = FactContextDispatchRuleProvider.LoadForWorkingDirectory(dir);
        _effectRules = FactEffectRuleProvider.LoadForWorkingDirectory(dir);
        _observationRules = FactObservationRuleProvider.LoadForWorkingDirectory(dir);
        _epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(dir);
        _classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(dir);

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
    public IReadOnlyList<TraceNode> BuildTree() => FactPathFinder.BuildTree(_markedGraph, _pattern, MaxDepth);

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
    public IReadOnlyList<DerivedEntryPoint> DeriveEntryPoints() =>
        FactEntryPointDeriver.Derive(_epData, _epRules, _classRules);
}
