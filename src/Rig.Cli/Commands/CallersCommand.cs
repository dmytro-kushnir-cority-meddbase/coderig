using System.CommandLine;
using System.Diagnostics;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
using Rig.Cli.Telemetry;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig callers <to>` — reverse reachability over the fact graph: every method that can reach <to> (transitive
// callers, incl. reverse interface/override dispatch). DEFAULTS TO SYNCHRONOUS (handoffs cut) — the right lens
// for "who touches X"; `--async` also walks handoffs. --orphans filters to entry-point candidates (reachable
// methods with no predecessor); --entrypoints filters to the RULE-DETECTED entry points (the `rig derive` set)
// that reach the target. Walks the SAME shaped graph as path/reaches/tree; --raw bypasses shaping.
internal static class CallersCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var to = CommonOptions.Pattern(name: "to", description: "Target method pattern (who reaches this?).");
        var orphans = new Option<bool>("--orphans", "--roots")
        {
            Description =
                "Heuristic: all no-predecessor origins that reach the target (includes test/bench/unbound-interface origins). Superset of --entrypoints.",
        };
        var entrypoints = new Option<bool>("--entrypoints")
        {
            Description =
                "Precise: rule-detected entry points only (same set as `rig derive`). Subset of --roots; may miss test/bench or unbound-interface origins.",
        };
        var includeReverseOnly = new Option<bool>("--include-reverse-only")
        {
            // DIAGNOSTIC / hidden: the reverse closure is only a candidate generator — forward-verification is
            // the arbiter, so the forward-confirmed set IS the answer. The reverse-only remainder is a
            // CHA-over-approximation (no forward path) plus a small recall hedge for forward's own
            // interface/lambda misses; it is noise for the precise question and is hidden by default (no
            // footer). This flag is the debug escape hatch that lists it (e.g. when chasing a suspected
            // forward false-negative). Hidden from --help so it doesn't read as a normal lens.
            Description =
                "DIAGNOSTIC: list the reverse-only callers/roots/entry points (reverse-dispatch over-approximation, no forward path). Hidden by default — the forward-confirmed set is the answer.",
            Hidden = true,
        };
        var async = CommonOptions.Async();
        var includeDelivery = CommonOptions.IncludeDelivery();
        var raw = CommonOptions.Raw();
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var time = CommonOptions.Time();
        var store = CommonOptions.Store();
        var cmd = new Command(name: "callers", description: "Reverse reachability: which methods reach the target.")
        {
            to,
            orphans,
            entrypoints,
            includeReverseOnly,
            async,
            includeDelivery,
            raw,
            rules,
            depth,
            format,
            limit,
            time,
            store,
        };
        // --orphans (the candidate heuristic) and --entrypoints (the precise rule set) are distinct lenses.
        cmd.Validators.Add(result =>
        {
            if (result.GetValue(orphans) && result.GetValue(entrypoints))
            {
                result.AddError("Options --orphans and --entrypoints can't be combined for 'rig callers'.");
            }
        });
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        new Options(
                            ToPattern: pr.GetValue(to)!,
                            RootsOnly: pr.GetValue(orphans),
                            EntrypointsOnly: pr.GetValue(entrypoints),
                            IncludeReverseOnly: pr.GetValue(includeReverseOnly),
                            Async: pr.GetValue(async),
                            IncludeDelivery: pr.GetValue(includeDelivery),
                            Raw: pr.GetValue(raw),
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Depth: pr.GetValue(depth),
                            Format: pr.GetValue(format),
                            Limit: pr.GetValue(limit),
                            Time: pr.GetValue(time)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, pr.GetValue(store)))
                    )
            )
        );
        return cmd;
    }

    // Bound option values for `rig callers`. Raw user inputs (Format kept as the parsed string);
    // the flag derivations (tsv, max, maxDepth, mode) live at the top of RunAsync.
    private sealed record Options(
        string ToPattern,
        bool RootsOnly,
        bool EntrypointsOnly,
        bool IncludeReverseOnly,
        bool Async,
        bool IncludeDelivery,
        bool Raw,
        IReadOnlyList<string> ExtraRules,
        int? Depth,
        string? Format,
        int? Limit,
        bool Time
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var max = opts.Limit ?? int.MaxValue; // --limit absent => unbounded
        var maxDepth = CommonOptions.DepthOrUnbounded(opts.Depth);
        var mode = CommonOptions.Mode(async: opts.Async, includeDelivery: opts.IncludeDelivery);

        using var timing = QueryTiming.Start(opts.Time, io.TextOutput.Error);

        // --raw bypasses shaping (the exact unfiltered reverse closure); else monomorphize factories + cut +
        // context, honoured symmetrically by the reverse traversal (a cut node yields no successors forward,
        // so it is never a predecessor in reverse).
        var rules = RuleSetLoader.Load(io.WorkspaceLocation.WorkingDirectory, opts.ExtraRules);
        var shaped = opts.Raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation);

        // One shaped reverse subgraph (bounded when `rig graph` has run, else the full EF graph) drives all
        // three callers modes — the set, the no-predecessor roots, and the rule-detected entrypoints.
        var graphWatch = Stopwatch.StartNew();
        var graph = await LoadShapedTraversalGraphAsync(context, opts.ToPattern, SqlReachability.Direction.Reverse, shaped);

        // Reclassify event-subscription (`+=`) method-group edges to `handoff` — mirroring reaches/tree
        // (and now path). The handler runs LATER via the event, not synchronously at the `+=` site, so it
        // is sync-cut by default and only crossed under --async. Marks edges by (Caller, FilePath, Line),
        // which is direction-agnostic, so it applies to this REVERSE subgraph the same way. Consequence
        // (intended, matches reaches/tree): a `+=` handler is no longer a synchronous reverse caller, so
        // event handlers surface under --roots/--entrypoints only via --async. `--raw` bypasses shaping.
        if (!opts.Raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        graphWatch.Stop();
        timing.Record("graph load", graphWatch.Elapsed);

        if (opts.EntrypointsOnly)
        {
            // F9: load the DeploymentMap here and pass it into RunEntryPointsAsync, eliminating the
            // LoadDeploymentsAsync call that was inside RunEntryPointsAsync (depth-1 in the call tree).
            var epDeployments = await LoadDeploymentsAsync(context, io.WorkspaceLocation.WorkingDirectory);
            var epResult = await RunEntryPointsAsync(
                context,
                graph,
                toPattern: opts.ToPattern,
                maxDepth: maxDepth,
                mode: mode,
                rules: rules,
                workingDirectory: io.WorkspaceLocation.WorkingDirectory,
                tsv: tsv,
                output: io.TextOutput.Output,
                includeReverseOnly: opts.IncludeReverseOnly,
                deployments: epDeployments
            );
            return epResult;
        }

        // Deployment/EP context for the from-symbol annotations (opt-in via deployments.json). Only the
        // --orphans text listing uses it for the chip; tsv and the reachable listing don't, so it's built
        // lazily to avoid the EP-site derivation when it isn't read.
        EpRenderContext? epContext = tsv
            ? null
            : await BuildEpContextAsync(
                context,
                graph,
                io.WorkspaceLocation.WorkingDirectory,
                opts.ExtraRules,
                rules,
                await LoadDeploymentsAsync(context, io.WorkspaceLocation.WorkingDirectory)
            );

        if (opts.RootsOnly)
        {
            var traversalWatch = Stopwatch.StartNew();
            var roots = FactPathFinder.EntryRootsReaching(graph, opts.ToPattern, maxDepth, mode: mode);
            if (roots.Count == 0)
            {
                traversalWatch.Stop();
                timing.Record("traversal", traversalWatch.Elapsed);
                if (!tsv)
                {
                    io.TextOutput.Output.WriteLine(
                        $"No root callers (no-predecessor origins) reach '{opts.ToPattern}' (or no symbol matches)."
                    );
                }

                return 1;
            }

            // FORWARD-VERIFY each root against the SAME graph (mirrors RunEntryPointsAsync), unless --raw —
            // which keeps the exact unfiltered reverse superset. Reverse reachability is set-based BFS, so a
            // shared base/interface virtual node pulls in roots whose FORWARD (receiver-narrowed) dispatch
            // resolves to a sibling override that never reaches the target. The depth-0 entries of the reverse
            // closure are the matched target nodes; each root forward-reaches them or is partitioned as
            // reverse-only (recall-safe — a forward reach can legitimately miss an interface/lambda-only path).
            var rootsConfirmed = roots;
            var rootsReverseOnly = (IReadOnlyList<string>)[];
            if (!opts.Raw)
            {
                var targetIds = FactPathFinder
                    .ReachedBy(graph, opts.ToPattern, maxDepth, mode: mode)
                    .Where(kv => kv.Value == 0)
                    .Select(kv => kv.Key)
                    .ToHashSet(StringComparer.Ordinal);
                var seedGroups = roots.Select(r => (IReadOnlyList<string>)new[] { r }).ToList();
                var confirmedFlags = FactPathFinder.SeedsReachTarget(graph, seedGroups, targetIds, maxDepth, mode);
                rootsConfirmed = roots.Where((_, i) => confirmedFlags[i]).ToList();
                rootsReverseOnly = roots.Where((_, i) => !confirmedFlags[i]).ToList();
            }

            traversalWatch.Stop();
            timing.Record("traversal", traversalWatch.Elapsed);

            var rootsRenderWatch = Stopwatch.StartNew();
            // --format tsv: one full-DocID root per line plus a trailing forwardConfirmed flag (ADDITIVE
            // column — the existing single-DocID-per-line shape is unchanged). ALL roots are emitted (confirmed
            // + reverse-only) so TSV consumers can filter on the flag. --raw emits the raw superset (all true).
            if (tsv)
            {
                var reverseOnlySet = rootsReverseOnly.ToHashSet(StringComparer.Ordinal);
                foreach (var r in roots.Take(max))
                {
                    io.TextOutput.Output.WriteLine($"{r}\t{(reverseOnlySet.Contains(r) ? "false" : "true")}");
                }

                rootsRenderWatch.Stop();
                timing.Record("render", rootsRenderWatch.Elapsed);

                return 0;
            }

            io.TextOutput.Output.WriteLine(
                $"Root callers (heuristic — no-predecessor origins) reaching '{opts.ToPattern}': {rootsConfirmed.Count}"
            );
            foreach (var r in rootsConfirmed.Take(max))
            {
                io.TextOutput.Output.WriteLine($"{Indent.L1}{r}{HeaderSuffix(epContext, r)}");
            }
            if (rootsConfirmed.Count > max)
            {
                io.TextOutput.Output.WriteLine($"{Indent.L1}… +{rootsConfirmed.Count - max} more (raise --limit)");
            }
            // Reverse-only = in the reverse closure but with NO forward path: a reverse-dispatch
            // over-approximation. HIDDEN by default (it's diagnostic noise — the confirmed set is the
            // answer); the hidden --include-reverse-only flag lists it as a recall escape hatch.
            if (opts.IncludeReverseOnly && rootsReverseOnly.Count > 0)
            {
                io.TextOutput.Output.WriteLine($"Reverse-only (no forward path found — confirm with `rig path`): {rootsReverseOnly.Count}");
                foreach (var r in rootsReverseOnly.Take(max))
                {
                    io.TextOutput.Output.WriteLine($"{Indent.L1}{r}{HeaderSuffix(epContext, r)}");
                }
            }

            rootsRenderWatch.Stop();
            timing.Record("render", rootsRenderWatch.Elapsed);

            return 0;
        }

        var defaultTraversalWatch = Stopwatch.StartNew();
        var reachable = MonomorphCollapse.CollapseDepthMap(FactPathFinder.ReachedBy(graph, opts.ToPattern, maxDepth, mode: mode));
        if (reachable.Count == 0)
        {
            defaultTraversalWatch.Stop();
            timing.Record("traversal", defaultTraversalWatch.Elapsed);
            if (!tsv)
            {
                io.TextOutput.Output.WriteLine($"No symbol matches '{opts.ToPattern}'.");
            }

            return 1;
        }

        // Separate the BFS start nodes (depth=0, the matched target(s) and their lambdas) from actual
        // upstream callers (depth≥1). The headline count and --limit budget reflect upstream callers only
        // — the matched nodes are the SUBJECT of the query, not its answer.
        var matched = reachable.Where(k => k.Value == 0).OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
        var callers = reachable.Where(k => k.Value > 0).OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();

        // FORWARD-VERIFY each upstream caller against the SAME graph (mirrors RunEntryPointsAsync), unless
        // --raw — which keeps the exact unfiltered reverse superset. Reverse reachability is set-based BFS, so
        // a shared base/interface virtual node pulls in callers whose FORWARD (receiver-narrowed) dispatch
        // resolves to a sibling override that never reaches the target. Each caller forward-reaches a matched
        // (depth-0) target node or is partitioned as reverse-only (recall-safe — a forward reach can
        // legitimately miss an interface/lambda-only path, so we caveat rather than drop).
        var forwardConfirmed = new Dictionary<string, bool>(StringComparer.Ordinal);
        if (!opts.Raw)
        {
            var targetIds = matched.Select(k => k.Key).ToHashSet(StringComparer.Ordinal);
            var seedGroups = callers.Select(c => (IReadOnlyList<string>)new[] { c.Key }).ToList();
            var confirmedFlags = FactPathFinder.SeedsReachTarget(graph, seedGroups, targetIds, maxDepth, mode);
            for (var i = 0; i < callers.Count; i++)
            {
                forwardConfirmed[callers[i].Key] = confirmedFlags[i];
            }
        }
        bool IsReverseOnly(string id) => !opts.Raw && forwardConfirmed.TryGetValue(id, out var ok) && !ok;

        var confirmedCallers = callers.Where(kv => !IsReverseOnly(kv.Key)).ToList();
        var reverseOnlyCallers = callers.Where(kv => IsReverseOnly(kv.Key)).ToList();

        defaultTraversalWatch.Stop();
        timing.Record("traversal", defaultTraversalWatch.Elapsed);

        var renderWatch = Stopwatch.StartNew();
        // --format tsv: depth + full DocID per reaching method, plus a trailing forwardConfirmed flag
        // (ADDITIVE column — the existing depth\tdocid columns are unchanged). Depth-0 rows are the BFS start
        // nodes (the matched target(s) and their lambdas, always forwardConfirmed=true), distinctly identified
        // by their `0` depth — TSV consumers can filter depth > 0 for upstream callers only. ALL rows emitted
        // (confirmed + reverse-only) so consumers can filter on the flag. --raw emits the raw superset (true).
        if (tsv)
        {
            foreach (var kv in reachable.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(max))
            {
                io.TextOutput.Output.WriteLine($"{kv.Value}\t{kv.Key}\t{(IsReverseOnly(kv.Key) ? "false" : "true")}");
            }

            renderWatch.Stop();
            timing.Record("render", renderWatch.Elapsed);

            return 0;
        }

        io.TextOutput.Output.WriteLine($"Methods that reach '{opts.ToPattern}': {confirmedCallers.Count}");
        if (matched.Count > 0)
        {
            io.TextOutput.Output.WriteLine($"{Indent.L1}Matched nodes ({matched.Count}):");
            foreach (var kv in matched)
            {
                io.TextOutput.Output.WriteLine($"{Indent.L2}{ShortName(kv.Key)}");
            }
        }
        foreach (var kv in confirmedCallers.Take(max))
        {
            io.TextOutput.Output.WriteLine($"{Indent.L1}d{kv.Value}  {ShortName(kv.Key)}");
        }
        if (confirmedCallers.Count > max)
        {
            io.TextOutput.Output.WriteLine($"{Indent.L1}… +{confirmedCallers.Count - max} more (raise --limit, or --format tsv for all)");
        }
        // Reverse-only = in the reverse closure but with NO forward path: a reverse-dispatch over-approximation
        // (a shared base/interface seam pulls in every caller of ANY override). HIDDEN by default (diagnostic
        // noise — the confirmed set is the answer); the hidden --include-reverse-only flag lists it.
        if (opts.IncludeReverseOnly && reverseOnlyCallers.Count > 0)
        {
            io.TextOutput.Output.WriteLine($"Reverse-only (no forward path found — confirm with `rig path`): {reverseOnlyCallers.Count}");
            foreach (var kv in reverseOnlyCallers.Take(max))
            {
                io.TextOutput.Output.WriteLine($"{Indent.L1}d{kv.Value}  {ShortName(kv.Key)}");
            }
        }

        renderWatch.Stop();
        timing.Record("render", renderWatch.Elapsed);

        return 0;
    }

    // `rig callers <to> --entrypoints` — the RULE-DETECTED entry points (same set `rig derive` emits) whose
    // body is in the REVERSE closure of <to>, i.e. the real entry points that actually reach the target code.
    // The join key is the declaration site (FilePath, Line): a derived EP carries no DocID, but its handler
    // method's symbol fact shares the same site, so an EP is "touching" when some reverse-reachable method is
    // declared at the EP's site. Default is synchronous-only; --async also counts scheduled paths.
    // F9: `deployments` is passed in from `RunAsync` (already loaded there) so this method no longer
    // calls `LoadDeploymentsAsync` itself. Default null so future callers that don't have a pre-loaded
    // map still work (they pass null and the method loads its own below). All current callers pass it in.
    private static async Task<int> RunEntryPointsAsync(
        RigDbContext context,
        FactGraphData graph,
        string toPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        RuleSet rules,
        string workingDirectory,
        bool tsv,
        TextWriter output,
        bool includeReverseOnly = false,
        DeploymentMap? deployments = null
    )
    {
        // Reverse closure of the target (every method that reaches it) over the SAME shaped graph the caller
        // loaded — so the rule-detected entry points are intersected with the cut-shaped reach. Keep the
        // depth-bearing result: the depth-0 entries are the matched TARGET nodes, the forward-verify pass
        // (below) reaches each candidate EP toward them.
        var reachedBy = FactPathFinder.ReachedBy(graph, toPattern, maxDepth, mode: mode);
        var reachable = reachedBy.Keys.ToHashSet(StringComparer.Ordinal);
        if (reachable.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine($"No symbol matches '{toPattern}'.");
            }

            return 1;
        }

        // F9: use the passed-in map when the caller already loaded it; fall back to loading if null
        // (defensive — the current caller always passes it).
        deployments ??= await LoadDeploymentsAsync(context, workingDirectory);

        // (FilePath, Line) of every reverse-reachable method — the join key against derived EP sites. Sourced
        // from the already-loaded graph's method nodes (the same Kind==Method set, deduped by SymbolId) rather
        // than a second whole-method-table EF scan (LoadDeadCodeMethodsAsync) of the identical rows.
        var reachableSites = graph.Methods.Where(m => reachable.Contains(m.SymbolId)).Select(m => (m.FilePath, m.Line)).ToHashSet();

        // The rule-detected entry points (identical derivation to `rig derive`) + promoted handoff origins.
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var (derivedEps, _, promoted) = await DeriveEntryPointsAsync(context, epData, rules);

        // (file,line) -> handler DocID, so each entry-point line/row carries the queryable FQN beside its
        // slash route (the route matches nothing as a `rig tree`/`reaches` pattern — this is the handle).
        var docIdBySite = MethodDocIdBySite(epData);

        var touching = derivedEps
            .Concat(promoted)
            .Where(e => reachableSites.Contains((e.FilePath, e.Line)))
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line, g.First().Requires))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        // BUG-rig-missed-entrypoints-healthcode (Defect 2): the sync surface hides the scheduled/actor-handoff
        // paths, so a sync EP answer can UNDER-report — "0 sync" misreads as "unreachable from any entry point"
        // (which de-risks a change wrongly), and a non-zero sync count can still omit EPs that reach the target
        // ONLY via a handoff. Count the rule-detected EPs reachable on the --async surface so both the 0-EP and
        // the non-zero hints below can point the user at --async. Probed with AsyncExact (the semantics we'd
        // suggest), never AsyncInclude, so the hint never leans on imprecise delivery fan-out that --async would
        // itself exclude. Returns 0 when already walking handoffs (nothing hidden) — also the cheap early-out.
        int AsyncReachableEpCount()
        {
            // Already walking handoffs (nothing hidden), or the graph has none at all — skip the extra reverse
            // walk. The handoff-presence check is an O(E) scan that spares handoff-free stores the whole probe.
            if (mode != FactPathFinder.TraversalMode.SyncCut || !graph.CallEdges.Any(e => e.Kind == EdgeKinds.Handoff))
            {
                return 0;
            }

            var asyncReachable = FactPathFinder
                .ReachedBy(graph, toPattern, maxDepth, mode: FactPathFinder.TraversalMode.AsyncExact)
                .Keys.ToHashSet(StringComparer.Ordinal);
            var asyncSites = graph.Methods.Where(m => asyncReachable.Contains(m.SymbolId)).Select(m => (m.FilePath, m.Line)).ToHashSet();
            return derivedEps
                .Concat(promoted)
                .Where(e => asyncSites.Contains((e.FilePath, e.Line)))
                .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
                .Count();
        }

        if (touching.Count == 0)
        {
            if (!tsv)
            {
                // A target reachable ONLY via a handoff (background worker, actor message, event) would
                // otherwise be wrongly reported as dead/background-only — defeating the security-reachability
                // use case. Paid only on the 0-result path here.
                var asyncCount = AsyncReachableEpCount();
                if (asyncCount > 0)
                {
                    output.WriteLine(
                        $"No entry points reach '{toPattern}' synchronously — but {asyncCount} reach it via async/scheduled handoff. Re-run with --async."
                    );
                    return 1;
                }

                output.WriteLine($"No rule-detected entry points reach '{toPattern}'.");
            }

            return 1;
        }
        // FORWARD-VERIFY each candidate EP against the SAME graph (recall-safe partition, NOT a drop).
        // Reverse reachability is set-based BFS, so once a shared base/interface virtual node enters the
        // reverse closure ALL its callers rejoin — including callers whose FORWARD (receiver-narrowed)
        // dispatch resolves to a DIFFERENT sibling override, never the target's (the documented "reverse
        // narrowing is dispatch-hop-precise, not path-precise" limitation). For each candidate EP we
        // forward-reach its handler-method nodes (the graph.Methods declared at the EP's (file,line)) and
        // keep it as CONFIRMED iff one of them forward-reaches a matched target node; the rest are listed
        // under a caveat (reverse-only) rather than dropped — a forward reach can legitimately miss an
        // interface-dispatch/lambda-only reach, so dropping would risk a false negative.
        var targetIds = reachedBy.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
        // (FilePath,Line) -> the method symbol ids declared there, inverting the same graph.Methods set
        // reachableSites was built from — the candidate EP's handler nodes to seed the forward reach with.
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
        var confirmed = touching.Where((_, i) => confirmedFlags[i]).ToList();
        var reverseOnly = touching.Where((_, i) => !confirmedFlags[i]).ToList();

        // --format tsv: one row per touching EP (full path), with the loaded + capability-active services,
        // a forwardConfirmed flag, and a trailing fqn (both ADDITIVE columns — existing columns unchanged).
        // ALL touching EPs are emitted (confirmed + reverse-only) so TSV consumers can filter on the flag.
        // Columns: kind, route, file, line, requires, loadedServices, activeServices, forwardConfirmed, fqn
        // (the queryable dotted name; == route when the route already is the FQN, route fallback otherwise).
        if (tsv)
        {
            for (var i = 0; i < touching.Count; i++)
            {
                var e = touching[i];
                var loaded = deployments.ServicesForFile(e.FilePath);
                var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
                output.WriteLine(
                    $"{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}\t{(confirmedFlags[i] ? "true" : "false")}\t{FqnOrRoute(route: e.Route, filePath: e.FilePath, line: e.Line, docIdBySite: docIdBySite)}"
                );
            }
            return 0;
        }
        // Headline count is the PRECISE answer — confirmed (forward-verified) EPs only.
        output.WriteLine(
            $"Rule-detected entry points reaching '{toPattern}': {confirmed.Count}"
                + mode switch
                {
                    FactPathFinder.TraversalMode.AsyncExact => "  (--async: incl. scheduled paths; delivery fan-out excluded)",
                    FactPathFinder.TraversalMode.AsyncInclude => "  (--async --include-delivery: incl. delivery fan-out)",
                    _ => "",
                }
        );
        foreach (var kindGroup in confirmed.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup)
            {
                WriteEntryPointLine(
                    output,
                    deployments,
                    route: e.Route,
                    filePath: e.FilePath,
                    line: e.Line,
                    requires: e.Requires,
                    fqn: FqnOrRoute(route: e.Route, filePath: e.FilePath, line: e.Line, docIdBySite: docIdBySite)
                );
            }
        }
        // Defect 2 (non-zero under-report): even with sync EPs present, the async surface can reach MORE — a
        // target some EPs touch only via a scheduled/actor handoff. Compared against the sync REACHABLE set
        // (touching), not the confirmed headline, so the delta isolates the async contribution rather than
        // conflating it with the reverse-only partition. Cost is one extra reverse walk, paid only on the text
        // path in SyncCut mode (the helper early-outs otherwise). The precise per-EP confirmation lives on the
        // --async run, so this is a "go look" pointer, not a verified count.
        var asyncEpCount = AsyncReachableEpCount();
        if (asyncEpCount > touching.Count)
        {
            output.WriteLine(
                $"{Indent.L1}… +{asyncEpCount - touching.Count} more entry point(s) reach this via async/scheduled handoff (not shown) — re-run with --async."
            );
        }

        // Reverse-only = in the reverse closure but with NO forward path: a reverse-dispatch over-approximation
        // (a shared base/interface seam — e.g. EntityBase.Delete — pulls in every caller of ANY override, which
        // can dwarf the real answer by 100s–1000s). HIDDEN by default — the headline confirmed set IS the
        // answer; the hidden --include-reverse-only flag lists it as a diagnostic recall escape hatch.
        if (includeReverseOnly && reverseOnly.Count > 0)
        {
            output.WriteLine($"Reverse-only (no forward path found — confirm with `rig path`): {reverseOnly.Count}");
            foreach (var kindGroup in reverseOnly.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
            {
                output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
                foreach (var e in kindGroup)
                {
                    WriteEntryPointLine(
                        output,
                        deployments,
                        route: e.Route,
                        filePath: e.FilePath,
                        line: e.Line,
                        requires: e.Requires,
                        fqn: FqnOrRoute(route: e.Route, filePath: e.FilePath, line: e.Line, docIdBySite: docIdBySite)
                    );
                }
            }
        }
        // The service summary reflects the precise answer (confirmed EPs).
        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(confirmed.Select(t => (t.Kind, (string?)t.FilePath, t.Requires)), deployments, output);
        }

        return 0;
    }
}
