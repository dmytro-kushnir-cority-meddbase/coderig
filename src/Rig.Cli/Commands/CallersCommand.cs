using System.CommandLine;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Cli.Rules;
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
        var orphans = new Option<bool>("--orphans", "--roots") { Description = "Only no-predecessor entry-point candidates (heuristic)." };
        var entrypoints = new Option<bool>("--entrypoints")
        {
            Description = "Only RULE-DETECTED entry points that reach the target (precise).",
        };
        var async = CommonOptions.Async();
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
                        toPattern: pr.GetValue(to)!,
                        rootsOnly: pr.GetValue(orphans),
                        entrypointsOnly: pr.GetValue(entrypoints),
                        async: pr.GetValue(async),
                        raw: pr.GetValue(raw),
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        depth: pr.GetValue(depth),
                        format: pr.GetValue(format),
                        limit: pr.GetValue(limit),
                        output: output,
                        workingDirectory: workingDirectory,
                        storeRef: pr.GetValue(store)
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        string toPattern,
        bool rootsOnly,
        bool entrypointsOnly,
        bool async,
        bool raw,
        IReadOnlyList<string> extraRules,
        int? depth,
        string? format,
        int? limit,
        TextWriter output,
        string workingDirectory,
        string? storeRef
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var max = limit ?? int.MaxValue; // --limit absent => unbounded
        var maxDepth = CommonOptions.DepthOrUnbounded(depth);
        var mode = CommonOptions.Mode(async);
        // callers walks the SAME shaped graph as path/reaches/tree — monomorphized generic factories + cut +
        // context rules, honoured SYMMETRICALLY by the reverse traversal (a cut node yields no successors
        // forward, so it is never a predecessor in reverse). `--raw` bypasses shaping (the unfiltered reverse
        // closure, for inspecting the exact plumbing).
        var shaping = ShapingRuleSet.Load(workingDirectory, extraRules, raw);

        await using var context = OpenReadContext(workingDirectory, storeRef);

        // One shaped reverse subgraph (bounded when `rig graph` has run, else the full EF graph) drives all
        // three callers modes — the set, the no-predecessor roots, and the rule-detected entrypoints.
        var graph = await LoadShapedTraversalGraphAsync(
            context,
            toPattern,
            SqlReachability.Direction.Reverse,
            shaping.Handoff,
            shaping.Factory,
            shaping.Cut,
            shaping.Context
        );
        // Reclassify event-subscription (`+=`) method-group edges to `handoff` — mirroring reaches/tree
        // (and now path). The handler runs LATER via the event, not synchronously at the `+=` site, so it
        // is sync-cut by default and only crossed under --async. Marks edges by (Caller, FilePath, Line),
        // which is direction-agnostic, so it applies to this REVERSE subgraph the same way. Consequence
        // (intended, matches reaches/tree): a `+=` handler is no longer a synchronous reverse caller, so
        // event handlers surface under --roots/--entrypoints only via --async. `--raw` bypasses shaping.
        if (!raw)
        {
            graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
        }

        if (entrypointsOnly)
        {
            return await RunEntryPointsAsync(
                context,
                graph,
                toPattern,
                maxDepth,
                mode,
                shaping.Handoff,
                extraRules,
                workingDirectory,
                tsv,
                output
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
                workingDirectory,
                extraRules,
                shaping.Handoff,
                await LoadDeploymentsAsync(context, workingDirectory)
            );

        if (rootsOnly)
        {
            var roots = FactPathFinder.EntryRootsReaching(graph, toPattern, maxDepth, mode: mode);
            if (roots.Count == 0)
            {
                if (!tsv)
                {
                    output.WriteLine($"No entry-point candidates reach '{toPattern}' (or no symbol matches).");
                }

                return 1;
            }
            // --format tsv: one full-DocID root per line (no chip/header).
            if (tsv)
            {
                foreach (var r in roots.Take(max))
                {
                    output.WriteLine(r);
                }

                return 0;
            }
            output.WriteLine($"Entry-point candidates reaching '{toPattern}': {roots.Count}");
            foreach (var r in roots.Take(max))
            {
                output.WriteLine($"{Indent.L1}{r}{HeaderSuffix(epContext, r)}");
            }
            if (roots.Count > max)
            {
                output.WriteLine($"{Indent.L1}… +{roots.Count - max} more (raise --limit)");
            }

            return 0;
        }

        var reachable = FactPathFinder.ReachedBy(graph, toPattern, maxDepth, mode: mode);
        if (reachable.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine($"No symbol matches '{toPattern}'.");
            }

            return 1;
        }
        // --format tsv: depth + full DocID per reaching method (default unbounded; --limit caps it).
        if (tsv)
        {
            foreach (var kv in reachable.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(max))
            {
                output.WriteLine($"{kv.Value}\t{kv.Key}");
            }

            return 0;
        }
        output.WriteLine($"Methods that reach '{toPattern}': {reachable.Count}");
        foreach (var kv in reachable.OrderBy(k => k.Value).ThenBy(k => k.Key, StringComparer.Ordinal).Take(max))
        {
            output.WriteLine($"{Indent.L1}d{kv.Value}  {ShortName(kv.Key)}");
        }
        if (reachable.Count > max)
        {
            output.WriteLine($"{Indent.L1}… +{reachable.Count - max} more (raise --limit, or --format tsv for all)");
        }

        return 0;
    }

    // `rig callers <to> --entrypoints` — the RULE-DETECTED entry points (same set `rig derive` emits) whose
    // body is in the REVERSE closure of <to>, i.e. the real entry points that actually reach the target code.
    // The join key is the declaration site (FilePath, Line): a derived EP carries no DocID, but its handler
    // method's symbol fact shares the same site, so an EP is "touching" when some reverse-reachable method is
    // declared at the EP's site. Default is synchronous-only; --async also counts scheduled paths.
    private static async Task<int> RunEntryPointsAsync(
        RigDbContext context,
        FactGraphData graph,
        string toPattern,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        IReadOnlyList<FactHandoffRule> handoffRules,
        IReadOnlyList<string> extraRules,
        string workingDirectory,
        bool tsv,
        TextWriter output
    )
    {
        // Reverse closure of the target (every method that reaches it) over the SAME shaped graph the caller
        // loaded — so the rule-detected entry points are intersected with the cut-shaped reach.
        var reachable = FactPathFinder.ReachedBy(graph, toPattern, maxDepth, mode: mode).Keys.ToHashSet(StringComparer.Ordinal);
        if (reachable.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine($"No symbol matches '{toPattern}'.");
            }

            return 1;
        }

        var deployments = await LoadDeploymentsAsync(context, workingDirectory);

        // (FilePath, Line) of every reverse-reachable method — the join key against derived EP sites.
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        var reachableSites = methods.Where(m => reachable.Contains(m.SymbolId)).Select(m => (m.FilePath, m.Line)).ToHashSet();

        // The rule-detected entry points (identical derivation to `rig derive`) + promoted handoff origins.
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var (derivedEps, _, promoted) = await DeriveEntryPointsAsync(context, epData, workingDirectory, extraRules, handoffRules);

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
                output.WriteLine($"No rule-detected entry points reach '{toPattern}'.");
            }

            return 1;
        }
        // --format tsv: one row per touching EP (full path), with the loaded + capability-active services.
        // Columns: kind, route, file, line, requires, loadedServices, activeServices (comma-joined).
        if (tsv)
        {
            foreach (var e in touching)
            {
                var loaded = deployments.ServicesForFile(e.FilePath);
                var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
                output.WriteLine(
                    $"{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}"
                );
            }
            return 0;
        }
        output.WriteLine(
            $"Rule-detected entry points reaching '{toPattern}': {touching.Count}"
                + (mode == FactPathFinder.TraversalMode.AsyncInclude ? "  (--async: incl. scheduled paths)" : "")
        );
        foreach (var kindGroup in touching.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup)
            {
                WriteEntryPointLine(output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
            }
        }
        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(touching.Select(t => (t.Kind, (string?)t.FilePath, t.Requires)), deployments, output);
        }

        return 0;
    }
}
