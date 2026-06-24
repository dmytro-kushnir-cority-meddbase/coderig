using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;

namespace Rig.Cli.Graph;

// Loads the call graph (and the bounded effect-derivation inputs) a traversal command walks. Centralizes
// the "SQL bounded subgraph when `rig graph` has run, else the full in-memory EF graph" decision and the
// single FactPathFinder.ShapeGraph pass, so path/reaches/tree/callers all load through identical code and
// thus walk the identical shaped graph.
internal static class TraversalGraphLoader
{
    // SQL FAST PATH HARDCODED OFF (2026-06-24, monomorphization rework): the bounded `call_edges` views
    // drop the reference_facts type-arg bindings (DeclaringTypeArgBinding/MethodTypeArgBinding), so the
    // monomorphization seam in ShapeGraph sees no bindings on the SQL path and materializes nothing. Route
    // every traversal/effect-reach load through the EF path (Reads.LoadFactGraphAsync /
    // LoadReachInputsFromRowsAsync), which projects those bindings onto CallEdge — so materialization has
    // the data UNIFORMLY. Correctness/uniformity first; the SqlReachability code is kept intact to
    // RE-OPTIMIZE later (make the views binding-aware), at which point flip this back to true.
    // (static readonly, not const, so the disabled SQL branches don't trip unreachable-code warnings.)
    private static readonly bool SqlFastPathEnabled = false;

    // Every query command opens the store READ-ONLY (see RigDbContext.readOnly): the engine rejects any
    // write to the main DB, so a read command can never mutate the index. Writers (index/mine/graph) use
    // the default read-write constructor.
    internal static RigDbContext OpenReadContext(string workingDirectory, string? storeRef = null) =>
        new(StoreLayout.DbPathForRef(workingDirectory, storeRef), readOnly: true);

    // F7: overload that also surfaces the resolved store DIRECTORY so the caller can reuse it for a
    // cache-key computation (StoreKey), avoiding a second ResolveReadStoreDir call. The caller receives
    // the dir as an `out` parameter; the existing no-out-param overload is unchanged for all other callers.
    internal static RigDbContext OpenReadContext(string workingDirectory, string? storeRef, out string storeDir)
    {
        storeDir = StoreLayout.ResolveReadStoreDir(workingDirectory, storeRef);
        return new(Path.Combine(storeDir, StoreLayout.DbFileName), readOnly: true);
    }

    // The SINGLE schema-gate chokepoint for read/query commands. Opens the read context exactly as
    // OpenReadContext does, then runs SchemaGate.AssertReadableAsync ONCE — the hard fail-fast that
    // replaces the scattered per-table TableExistsAsync schema probes. Every query command opens through
    // here so an uninitialized / schema-drifted store fails at open with a clear "re-index" message rather
    // than a cryptic mid-query `no such column`. Writers (index/mine/graph) use the raw constructor — they
    // CREATE the store and must not be gated. On a gate failure the context is disposed before rethrow.
    internal static async Task<RigDbContext> OpenReadContextGatedAsync(string workingDirectory, string? storeRef = null)
    {
        var context = OpenReadContext(workingDirectory, storeRef);
        await AssertReadableAsync(context);
        return context;
    }

    // Gated counterpart of the F7 out-param overload (storeDir surfaced for StoreKey reuse).
    internal static async Task<(RigDbContext Context, string StoreDir)> OpenReadContextGatedAsync(
        string workingDirectory,
        string? storeRef,
        bool withStoreDir
    )
    {
        var context = OpenReadContext(workingDirectory, storeRef, out var storeDir);
        await AssertReadableAsync(context);
        return (context, storeDir);
    }

    private static async Task AssertReadableAsync(RigDbContext context)
    {
        try
        {
            await SchemaGate.AssertReadableAsync(context);
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    // The call graph for a traversal command (reaches/tree/path/callers). When the derived edge views
    // exist (`rig graph` has been run) it returns the BOUNDED subgraph for `pattern` in the given
    // direction — loaded on disk via recursive CTE, sized to the result, not the 1.6GB store. Otherwise
    // it falls back to the full in-memory EF graph (the reference path). The SAME FactPathFinder then
    // runs over whichever graph, so the output is identical — only the load cost differs.
    internal static async Task<FactGraphData> LoadTraversalGraphAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        IReadOnlyList<FactHandoffRule> handoffRules,
        IReadOnlyList<FactRedirectRule> redirectRules
    )
    {
        // SQL path: call_edges already carry the persisted handoff classification (from `rig graph`),
        // so the bounded graph is classified by construction. EF fallback: classify the loaded graph
        // with the rules so the in-memory traversal sees the same handoff edges.
        if (SqlFastPathEnabled && await SqlReachability.HasGraphAsync(context))
        {
            return await SqlReachability.LoadBoundedGraphAsync(context, pattern, direction);
        }

        return await Reads.LoadFactGraphAsync(context, handoffRules, redirectRules);
    }

    // The SHAPED traversal graph: LoadTraversalGraphAsync + the single FactPathFinder.ShapeGraph pass
    // (monomorphize generic factories + carry cut/context rules on the graph). EVERY attribution command
    // — forward (path) or reverse (callers) — loads through here so they all walk the identical shaped
    // graph; this is what keeps `callers` consistent with `path`/`reaches`. `dead` deliberately does NOT
    // use this (it needs the sound CHA superset). Pass empty rule sets (the `--raw` path) for no shaping.
    // Takes the (already `--raw`-gated) RuleSet the command built; it reads only the shaping slices
    // (Handoff/Factory/Cut/Context). Gating is the command's policy (a `with` on the RuleSet), so this
    // stays policy-free — it shapes with whatever those slices carry.
    internal static async Task<FactGraphData> LoadShapedTraversalGraphAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        RuleSet rules
    )
    {
        var graph = await LoadTraversalGraphAsync(context, pattern, direction, rules.Handoff, rules.Redirect);
        var monoSigs = await Reads.LoadMonomorphizationSignaturesAsync(context);
        return FactPathFinder.ShapeGraph(graph, rules.Factory, rules.Cut, rules.Context, monomorphizeSignatures: monoSigs);
    }

    // Like LoadTraversalGraphAsync, but also returns the effect-derivation inputs (invocations / ctor
    // refs / throw refs) bounded to the SAME closure — so reaches/tree don't scan every invocation in
    // the codebase. SQL path: one reach_set drives the graph + bounded inputs. EF fallback: the full
    // reference loads (the original path), so output is identical when no derived views exist.
    internal static async Task<SqlReachability.ReachInputs> LoadEffectReachInputsAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        RuleSet rules
    )
    {
        var inputs =
            SqlFastPathEnabled && await SqlReachability.HasGraphAsync(context)
                ? await SqlReachability.LoadReachInputsAsync(context, pattern, direction)
                : await LoadReachInputsFromRowsAsync(context, rules.Handoff, rules.Redirect);

        // The single shaping pass (monomorphize generic factories + carry cut/context rules on the graph)
        // so reaches/tree walk the same shaped graph as path/callers. Edges with no concrete construct
        // keep their plumbing (the in-memory generic-dispatch narrowing covers those).
        var monoSigs = await Reads.LoadMonomorphizationSignaturesAsync(context);
        inputs = inputs with
        {
            Graph = FactPathFinder.ShapeGraph(inputs.Graph, rules.Factory, rules.Cut, rules.Context, monomorphizeSignatures: monoSigs),
        };
        return inputs;
    }

    internal static async Task<SqlReachability.ReachInputs> LoadReachInputsFromRowsAsync(
        RigDbContext context,
        IReadOnlyList<FactHandoffRule> handoffRules,
        IReadOnlyList<FactRedirectRule> redirectRules
    )
    {
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules, redirectRules);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        // F2: surface epData so callers that also need the EP site map (DeriveEpSiteKindAsync) can
        // reuse it instead of issuing a second LoadFactEntryPointDataAsync on the EF-fallback path.
        return new SqlReachability.ReachInputs(graph, invocations, CtorRefs: epData.CtorRefs, ThrowRefs: throwRefs, EpData: epData);
    }

    // Base edges in the (TypeId, BaseId) shape FactEffectDeriver.Derive expects, from a graph's edges.
    internal static (string, string)[] BaseEdgeTuples(FactGraphData graph) =>
        (graph.BaseEdges ?? []).Select(e => (e.SubType, e.BaseType)).ToArray();
}
