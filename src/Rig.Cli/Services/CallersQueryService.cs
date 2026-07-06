using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The reusable REVERSE-REACHABILITY computation ("who reaches X"), lifted out of CallersCommand.RunAsync so
// BOTH the CLI (`rig callers`) and the in-process web host (Web/) run the SAME engine — no shelling out, no
// re-parsing text. Covers the two lenses CallersCommand.Build exposes as --roots/--entrypoints (its default,
// flag-less lens — the depth-tagged flat listing of every upstream caller — is out of scope here; the web
// surface only needs the two precise/heuristic entry-point lenses).
//
// Deliberately public + primitives-in (workingDirectory/storeRef, not the internal WorkspaceLocation) so the
// contract survives a later lift to a standalone Rig.Web project — mirrors TreeQueryService/PathQueryService/
// ReachesQueryService's convention.
public static class CallersQueryService
{
    // Web-facing mode discriminator for `/api/callers`. CallersCommand itself has no shared enum for this —
    // it reads two independent bools (Options.RootsOnly / Options.EntrypointsOnly, mutually exclusive via a
    // System.CommandLine validator) — so there is nothing named "CallersMode" to reuse; this is a new type.
    public enum CallersMode
    {
        Roots,
        EntryPoints,
    }

    // mode=roots result row: one no-predecessor origin reaching the target, plus the SAME forward-verification
    // flag CallersCommand's --roots branch computes (false = reverse-only: in the reverse closure but with no
    // confirmed forward path — a reverse-dispatch over-approximation; always true under --raw).
    public sealed record CallerRoot(string SymbolId, bool ForwardConfirmed);

    // One reverse-reachable entry point + its owning deployed service(s) (loaded-in, from deployments.json;
    // empty when deployments.json is absent). The "what can trigger this, and where does it run" answer.
    public sealed record CallersEntryPoint(EntryPointService.EntryPointView View, IReadOnlyList<ServiceRef> Services);

    // Exactly one of Roots/EntryPoints is populated, selected by Mode — mirrors the two mutually-exclusive
    // CLI branches. Kept as one result type (rather than two BuildXAsync methods returning unrelated types)
    // so callers can go through a single BuildAsync the way the task's shape asks for.
    public sealed record CallersResult(
        string ToPattern,
        CallersMode Mode,
        bool Matched,
        // FLAT list of root callers — see the file-level note on BuildRoots for why this is not a nested
        // TraceNode forest (there is no reverse analog of FactPathFinder.BuildTree in the domain layer).
        IReadOnlyList<CallerRoot>? Roots,
        // Reuses EntryPointService.EntryPointView (Kind/Route/Fqn/File/Line) verbatim — the same shape
        // `/api/entrypoints` already exposes — rather than inventing a parallel record.
        IReadOnlyList<CallersEntryPoint>? EntryPoints
    );

    // Build the callers result for `fromPattern` over the store at `workingDirectory` (optionally a specific
    // `storeRef` commit/id). Mirrors the cold path of CallersCommand.RunAsync: same shaping rules, same
    // event-subscription handoff marking, same REVERSE-direction graph load — so the web reports exactly what
    // `rig callers --roots` / `rig callers --entrypoints` would (minus the CLI-only render chrome: TSV/pretty
    // rendering, deployment/EP header chips, --time, the hidden --include-reverse-only diagnostic listing).
    public static async Task<CallersResult> BuildAsync(
        string workingDirectory,
        string fromPattern,
        string? storeRef,
        CallersMode mode,
        bool async,
        int? depth = null,
        bool raw = false,
        IReadOnlyList<string>? extraRules = null
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory, extraRules ?? []);
        // --raw parity: zero the graph-shaping rules so the reverse walk runs over the exact unfiltered graph.
        var shaped = raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );

        var traversalMode = CommonOptions.Mode(async: async);
        var maxDepth = CommonOptions.DepthOrUnbounded(depth);

        // The SAME shaped REVERSE-bounded subgraph CallersCommand loads for ALL of its lenses (default/
        // --roots/--entrypoints). Direction only bounds WHICH nodes/edges the SQL fast path loads onto —
        // FactPathFinder's own Predecessors/BuildReverseMaps (inside EntryRootsReaching/ReachedBy below) do
        // the actual reverse walk over whichever edges got loaded.
        var graph = await LoadShapedTraversalGraphAsync(context, fromPattern, SqlReachability.Direction.Reverse, shaped);

        // Reclassify event-subscription (`+=`) method-group edges to `handoff` — mirrors CallersCommand (and
        // reaches/tree/path): the handler runs LATER via the event, not synchronously at the `+=` site.
        // Skipped under --raw, same as the CLI.
        if (!raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        if (mode == CallersMode.EntryPoints)
        {
            // Load the deployment attribution once (file-path → owning service, from deployments.json) so each
            // reverse-reachable EP is annotated with where it runs — the "which services can trigger this" lens.
            var deploy = await DeploymentAttributionLookup.LoadAsync(context, workingDirectory);
            return await BuildEntryPointsAsync(context, graph, fromPattern, maxDepth, traversalMode, rules, raw, deploy);
        }

        return BuildRoots(graph, fromPattern, maxDepth, traversalMode, raw);
    }

    // `rig callers <to> --roots`: FLAT list of no-predecessor origins reaching the target
    // (FactPathFinder.EntryRootsReaching — the exact function CallersCommand's RootsOnly branch calls),
    // forward-verified exactly as that branch does (FactPathFinder.SeedsReachTarget against the depth-0
    // matched-target ids from ReachedBy), unless --raw.
    //
    // NOT a nested tree. The domain layer has no reverse analog of FactPathFinder.BuildTree: BuildTree only
    // walks FORWARD Successors to materialize a parent-linked TraceNode forest. The reverse walk
    // (FactPathFinder's private Predecessors/BuildReverseMaps, which EntryRootsReaching/ReachedBy are built
    // on) only ever produces a depth-map or a flat root list — it never records WHICH predecessor reached
    // WHICH node, so there is no parent linkage available to reconstruct a tree from without writing NEW
    // parent-tracking BFS logic. That is out of scope for a create-new-files-only change that must not touch
    // FactPathFinder.cs, and hand-rolling a fresh traversal here would risk diverging from the
    // already-reviewed Predecessors semantics (dispatch fan-out, receiver narrowing, handoff cuts) that
    // ReachedBy/EntryRootsReaching already encode correctly. This mirrors ReachesQueryService's own
    // precedent — a FLAT result, because the underlying domain computation is flat.
    private static CallersResult BuildRoots(
        FactGraphData graph,
        string toPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        bool raw
    )
    {
        var roots = FactPathFinder.EntryRootsReaching(graph, toPattern, maxDepth, mode: mode);
        if (roots.Count == 0)
        {
            return new CallersResult(toPattern, CallersMode.Roots, Matched: false, Roots: [], EntryPoints: null);
        }

        bool[] confirmedFlags;
        if (raw)
        {
            confirmedFlags = roots.Select(_ => true).ToArray();
        }
        else
        {
            var targetIds = FactPathFinder
                .ReachedBy(graph, toPattern, maxDepth, mode: mode)
                .Where(kv => kv.Value == 0)
                .Select(kv => kv.Key)
                .ToHashSet(StringComparer.Ordinal);
            var seedGroups = roots.Select(r => (IReadOnlyList<string>)new[] { r }).ToList();
            confirmedFlags = FactPathFinder.SeedsReachTarget(graph, seedGroups, targetIds, maxDepth, mode);
        }

        var dto = roots.Select((r, i) => new CallerRoot(r, confirmedFlags[i])).ToList();
        return new CallersResult(toPattern, CallersMode.Roots, Matched: true, Roots: dto, EntryPoints: null);
    }

    // `rig callers <to> --entrypoints`: the rule-detected entry points (same set `rig derive`/`/api/entrypoints`
    // emit) whose declaration site is in the reverse closure of `to`, forward-verified exactly as
    // CallersCommand.RunEntryPointsAsync does. Each confirmed EP is annotated with its owning deployed
    // service(s) via `deploy` (file-path attribution; loaded-in upper bound — see DeploymentAttributionLookup).
    private static async Task<CallersResult> BuildEntryPointsAsync(
        RigDbContext context,
        FactGraphData graph,
        string toPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        RuleSet rules,
        bool raw,
        DeploymentAttributionLookup deploy
    )
    {
        var reachedBy = FactPathFinder.ReachedBy(graph, toPattern, maxDepth, mode: mode);
        var reachable = reachedBy.Keys.ToHashSet(StringComparer.Ordinal);
        if (reachable.Count == 0)
        {
            return new CallersResult(toPattern, CallersMode.EntryPoints, Matched: false, Roots: null, EntryPoints: []);
        }

        // (FilePath, Line) of every reverse-reachable method — the join key against derived EP sites, sourced
        // from the already-loaded graph's method nodes rather than a second whole-method-table scan.
        var reachableSites = graph.Methods.Where(m => reachable.Contains(m.SymbolId)).Select(m => (m.FilePath, m.Line)).ToHashSet();

        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var (derivedEps, _, promoted) = await DeriveEntryPointsAsync(context, epData, rules);
        var docIdBySite = MethodDocIdBySite(epData);

        var touching = derivedEps
            .Concat(promoted)
            .Where(e => reachableSites.Contains((e.FilePath, e.Line)))
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        if (touching.Count == 0)
        {
            return new CallersResult(toPattern, CallersMode.EntryPoints, Matched: false, Roots: null, EntryPoints: []);
        }

        // FORWARD-VERIFY each candidate EP against the SAME graph (mirrors CallersCommand.RunEntryPointsAsync),
        // unless --raw. Reverse reachability is set-based BFS, so a shared base/interface virtual node pulls
        // in every caller of ANY override; forward-reaching each candidate's handler-method nodes toward the
        // depth-0 matched targets partitions the confirmed set from the reverse-only over-approximation.
        var confirmed = touching;
        if (!raw)
        {
            var targetIds = reachedBy.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            var methodsBySite = new Dictionary<(string, int), List<string>>();
            foreach (var m in graph.Methods)
            {
                var key = (m.FilePath, m.Line);
                if (!methodsBySite.TryGetValue(key, out var ids))
                {
                    ids = new List<string>();
                    methodsBySite[key] = ids;
                }

                ids.Add(m.SymbolId);
            }

            var seedGroups = touching
                .Select(e => (IReadOnlyList<string>)(methodsBySite.TryGetValue((e.FilePath, e.Line), out var ids) ? ids : []))
                .ToList();
            var confirmedFlags = FactPathFinder.SeedsReachTarget(graph, seedGroups, targetIds, maxDepth, mode);
            confirmed = touching.Where((_, i) => confirmedFlags[i]).ToList();
        }

        var hits = confirmed
            .Select(e =>
            {
                var file = string.IsNullOrEmpty(e.FilePath) ? null : e.FilePath;
                var view = new EntryPointService.EntryPointView(
                    Kind: e.Kind,
                    Route: e.Route,
                    Fqn: FqnOrRoute(route: e.Route, filePath: e.FilePath, line: e.Line, docIdBySite: docIdBySite),
                    File: file,
                    Line: e.Line
                );
                return new CallersEntryPoint(view, deploy.IsEmpty ? [] : deploy.ServicesWithKindFor(file));
            })
            .ToList();

        return new CallersResult(toPattern, CallersMode.EntryPoints, Matched: hits.Count > 0, Roots: null, EntryPoints: hits);
    }
}
