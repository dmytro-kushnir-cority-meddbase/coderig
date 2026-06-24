using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
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
        var async = CommonOptions.Async();
        var includeDelivery = CommonOptions.IncludeDelivery();
        var raw = CommonOptions.Raw();
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var store = CommonOptions.Store();
        var cmd = new Command(name: "callers", description: "Reverse reachability: which methods reach the target.")
        {
            to,
            orphans,
            entrypoints,
            async,
            includeDelivery,
            raw,
            rules,
            depth,
            format,
            limit,
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
                            Async: pr.GetValue(async),
                            IncludeDelivery: pr.GetValue(includeDelivery),
                            Raw: pr.GetValue(raw),
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Depth: pr.GetValue(depth),
                            Format: pr.GetValue(format),
                            Limit: pr.GetValue(limit)
                        ),
                        new CommandIo(Output: output, Error: error, WorkingDirectory: workingDirectory, StoreRef: pr.GetValue(store))
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
        bool Async,
        bool IncludeDelivery,
        bool Raw,
        IReadOnlyList<string> ExtraRules,
        int? Depth,
        string? Format,
        int? Limit
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var max = opts.Limit ?? int.MaxValue; // --limit absent => unbounded
        var maxDepth = CommonOptions.DepthOrUnbounded(opts.Depth);
        var mode = CommonOptions.Mode(async: opts.Async, includeDelivery: opts.IncludeDelivery);

        // --raw bypasses shaping (the exact unfiltered reverse closure); else monomorphize factories + cut +
        // context, honoured symmetrically by the reverse traversal (a cut node yields no successors forward,
        // so it is never a predecessor in reverse).
        var rules = RuleSetLoader.Load(io.WorkingDirectory, opts.ExtraRules);
        var shaped = opts.Raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;

        await using var context = OpenReadContext(io.WorkingDirectory, io.StoreRef);

        // One shaped reverse subgraph (bounded when `rig graph` has run, else the full EF graph) drives all
        // three callers modes — the set, the no-predecessor roots, and the rule-detected entrypoints.
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

        if (opts.EntrypointsOnly)
        {
            // F9: load the DeploymentMap here and pass it into RunEntryPointsAsync, eliminating the
            // LoadDeploymentsAsync call that was inside RunEntryPointsAsync (depth-1 in the call tree).
            var epDeployments = await LoadDeploymentsAsync(context, io.WorkingDirectory);
            return await RunEntryPointsAsync(
                context,
                graph,
                opts.ToPattern,
                maxDepth,
                mode,
                rules,
                io.WorkingDirectory,
                tsv,
                io.Output,
                epDeployments
            );
        }

        // Deployment/EP context for the from-symbol annotations (opt-in via deployments.json). Only the
        // --orphans text listing uses it for the chip; tsv and the reachable listing don't, so it's built
        // lazily to avoid the EP-site derivation when it isn't read.
        EpRenderContext? epContext = tsv
            ? null
            : await BuildEpContextAsync(
                context,
                graph,
                io.WorkingDirectory,
                opts.ExtraRules,
                rules,
                await LoadDeploymentsAsync(context, io.WorkingDirectory)
            );

        if (opts.RootsOnly)
        {
            var roots = FactPathFinder.EntryRootsReaching(graph, opts.ToPattern, maxDepth, mode: mode);
            if (roots.Count == 0)
            {
                if (!tsv)
                {
                    io.Output.WriteLine($"No root callers (no-predecessor origins) reach '{opts.ToPattern}' (or no symbol matches).");
                }

                return 1;
            }
            // --format tsv: one full-DocID root per line (no chip/header).
            if (tsv)
            {
                foreach (var r in roots.Take(max))
                {
                    io.Output.WriteLine(r);
                }

                return 0;
            }
            io.Output.WriteLine($"Root callers (heuristic — no-predecessor origins) reaching '{opts.ToPattern}': {roots.Count}");
            foreach (var r in roots.Take(max))
            {
                io.Output.WriteLine($"{Indent.L1}{r}{HeaderSuffix(epContext, r)}");
            }
            if (roots.Count > max)
            {
                io.Output.WriteLine($"{Indent.L1}… +{roots.Count - max} more (raise --limit)");
            }

            return 0;
        }

        var reachable = FactPathFinder.ReachedBy(graph, opts.ToPattern, maxDepth, mode: mode);
        if (reachable.Count == 0)
        {
            if (!tsv)
            {
                io.Output.WriteLine($"No symbol matches '{opts.ToPattern}'.");
            }

            return 1;
        }
        // --format tsv: depth + full DocID per reaching method (default unbounded; --limit caps it).
        // Depth-0 rows are the BFS start nodes (the matched target(s) and their lambdas), distinctly
        // identified by their `0` depth value — TSV consumers can filter depth > 0 for upstream callers only.
        if (tsv)
        {
            foreach (var kv in reachable.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(max))
            {
                io.Output.WriteLine($"{kv.Value}\t{kv.Key}");
            }

            return 0;
        }
        // Separate the BFS start nodes (depth=0, the matched target(s) and their lambdas) from actual
        // upstream callers (depth≥1). The headline count and --limit budget reflect upstream callers only
        // — the matched nodes are the SUBJECT of the query, not its answer.
        var matched = reachable.Where(k => k.Value == 0).OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
        var callers = reachable.Where(k => k.Value > 0).OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).ToList();
        io.Output.WriteLine($"Methods that reach '{opts.ToPattern}': {callers.Count}");
        if (matched.Count > 0)
        {
            io.Output.WriteLine($"{Indent.L1}Matched nodes ({matched.Count}):");
            foreach (var kv in matched)
            {
                io.Output.WriteLine($"{Indent.L2}{ShortName(kv.Key)}");
            }
        }
        foreach (var kv in callers.Take(max))
        {
            io.Output.WriteLine($"{Indent.L1}d{kv.Value}  {ShortName(kv.Key)}");
        }
        // The truncation guard uses the full reverse-closure count (matched + callers) so that --limit
        // never silently drops nodes visible in `--format tsv`. Callers already shown up to max; if
        // the total exceeds max, the user may want to raise the limit or switch to tsv for the full set.
        if (reachable.Count > max)
        {
            io.Output.WriteLine($"{Indent.L1}… +{reachable.Count - max} more (raise --limit, or --format tsv for all)");
        }

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

        var touching = derivedEps
            .Concat(promoted)
            .Where(e => reachableSites.Contains((e.FilePath, e.Line)))
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line, g.First().Requires))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        if (touching.Count == 0)
        {
            if (!tsv)
            {
                // BUG-rig-missed-entrypoints-healthcode (Defect 2): a sync 0 reads as "not reachable from any
                // entry point", but the scheduled / actor-message surface is sync-cut by default. Before
                // claiming "none", probe --async — a target reachable ONLY via a handoff (a background worker,
                // an actor message, an event) would otherwise be wrongly reported as dead/background-only, which
                // defeats the security-reachability use case. Only paid on the 0-result path, so no per-query cost.
                if (mode == FactPathFinder.TraversalMode.SyncCut)
                {
                    // Probe with AsyncExact — the default `--async` semantics we'd point the user at — so the
                    // hint never suggests `--async` on the strength of a fan-out-only (imprecise) reach that
                    // `--async` itself would then exclude.
                    var asyncReachable = FactPathFinder
                        .ReachedBy(graph, toPattern, maxDepth, mode: FactPathFinder.TraversalMode.AsyncExact)
                        .Keys.ToHashSet(StringComparer.Ordinal);
                    var asyncSites = graph
                        .Methods.Where(m => asyncReachable.Contains(m.SymbolId))
                        .Select(m => (m.FilePath, m.Line))
                        .ToHashSet();
                    var asyncCount = derivedEps
                        .Concat(promoted)
                        .Where(e => asyncSites.Contains((e.FilePath, e.Line)))
                        .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
                        .Count();
                    if (asyncCount > 0)
                    {
                        output.WriteLine(
                            $"No entry points reach '{toPattern}' synchronously — but {asyncCount} reach it via async/scheduled handoff. Re-run with --async."
                        );
                        return 1;
                    }
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

        // --format tsv: one row per touching EP (full path), with the loaded + capability-active services
        // plus a trailing forwardConfirmed flag (ADDITIVE column — existing columns are unchanged). ALL
        // touching EPs are emitted (confirmed + reverse-only) so TSV consumers can filter on the flag.
        // Columns: kind, route, file, line, requires, loadedServices, activeServices, forwardConfirmed.
        if (tsv)
        {
            for (var i = 0; i < touching.Count; i++)
            {
                var e = touching[i];
                var loaded = deployments.ServicesForFile(e.FilePath);
                var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
                output.WriteLine(
                    $"{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}\t{(confirmedFlags[i] ? "true" : "false")}"
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
                WriteEntryPointLine(output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
            }
        }
        // Reverse-only: in the reverse closure but no forward path found — list under a caveat instead of
        // dropping (preserves recall against the forward/reverse dispatch asymmetry).
        if (reverseOnly.Count > 0)
        {
            output.WriteLine($"Reverse-only (no forward path found — confirm with `rig path`): {reverseOnly.Count}");
            foreach (var kindGroup in reverseOnly.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
            {
                output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
                foreach (var e in kindGroup)
                {
                    WriteEntryPointLine(output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
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
