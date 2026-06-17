using Rig.Analysis.Rules;
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
    // Every query command opens the store READ-ONLY (see RigDbContext.readOnly): the engine rejects any
    // write to the main DB, so a read command can never mutate the index. Writers (index/mine/graph) use
    // the default read-write constructor.
    internal static RigDbContext OpenReadContext(string workingDirectory, string? storeRef = null) =>
        new(CommandLine.StoreLayout.DbPathForRef(workingDirectory, storeRef), readOnly: true);

    // The call graph for a traversal command (reaches/tree/path/callers). When the derived edge views
    // exist (`rig graph` has been run) it returns the BOUNDED subgraph for `pattern` in the given
    // direction — loaded on disk via recursive CTE, sized to the result, not the 1.6GB store. Otherwise
    // it falls back to the full in-memory EF graph (the reference path). The SAME FactPathFinder then
    // runs over whichever graph, so the output is identical — only the load cost differs.
    internal static async Task<FactGraphData> LoadTraversalGraphAsync(
        RigDbContext context,
        string pattern,
        SqlReachability.Direction direction,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        // SQL path: call_edges already carry the persisted handoff classification (from `rig graph`),
        // so the bounded graph is classified by construction. EF fallback: classify the loaded graph
        // with the rules so the in-memory traversal sees the same handoff edges.
        if (await SqlReachability.HasGraphAsync(context))
        {
            return await SqlReachability.LoadBoundedGraphAsync(context, pattern, direction);
        }

        return await Reads.LoadFactGraphAsync(context, handoffRules);
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
        var graph = await LoadTraversalGraphAsync(context, pattern, direction, rules.Handoff);
        return FactPathFinder.ShapeGraph(graph, rules.Factory, rules.Cut, rules.Context);
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
        var inputs = await SqlReachability.HasGraphAsync(context)
            ? await SqlReachability.LoadReachInputsAsync(context, pattern, direction)
            : await LoadReachInputsFromRowsAsync(context, rules.Handoff);

        // The single shaping pass (monomorphize generic factories + carry cut/context rules on the graph)
        // so reaches/tree walk the same shaped graph as path/callers. Edges with no concrete construct
        // keep their plumbing (the in-memory generic-dispatch narrowing covers those).
        inputs = inputs with
        {
            Graph = FactPathFinder.ShapeGraph(inputs.Graph, rules.Factory, rules.Cut, rules.Context),
        };
        return inputs;
    }

    internal static async Task<SqlReachability.ReachInputs> LoadReachInputsFromRowsAsync(
        RigDbContext context,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        return new SqlReachability.ReachInputs(graph, invocations, CtorRefs: epData.CtorRefs, ThrowRefs: throwRefs);
    }

    // Base edges in the (TypeId, BaseId) shape FactEffectDeriver.Derive expects, from a graph's edges.
    internal static (string, string)[] BaseEdgeTuples(FactGraphData graph) =>
        (graph.BaseEdges ?? []).Select(e => (e.SubType, e.BaseType)).ToArray();
}
