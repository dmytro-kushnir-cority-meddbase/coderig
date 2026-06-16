using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Cli.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Caching.QueryCacheKeys;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;
using static Rig.Cli.Rendering.TreeRenderer;

namespace Rig.Cli.Commands;

// `rig tree <from>` — the full first-party call TREE from an entry point over the fact graph (same edges
// as reaches/path: interface->impl + base->override dispatch + loop context). Default prunes to paths that
// REACH an effect; --full prints every reachable method AND promotes effects/unresolved library calls to
// call-site leaf nodes; --summary prints the effect-count rollup; --effects collapses to one line per
// effectful method. Forest + effects are query-cached (the dominant cost); a render sidecar lets a warm
// query skip the graph load entirely.
internal static class TreeCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var from = CommonOptions.Pattern("from", "Entry-point method pattern.");
        var full = new Option<bool>("--full") { Description = "Print every reachable method; effects/unresolved calls as leaf nodes." };
        var summary = new Option<bool>("--summary") { Description = "Print only the effect-count rollup." };
        var effects = new Option<bool>("--effects") { Description = "List only effectful methods (one line each, source order)." };
        var async = CommonOptions.Async();
        var raw = CommonOptions.Raw();
        var files = CommonOptions.Files();
        var signatures = CommonOptions.Signatures();
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var only = CommonOptions.Only();
        var exclude = CommonOptions.Exclude();
        var noCache = CommonOptions.NoCache();
        var time = CommonOptions.Time();
        var cmd = new Command("tree", "Print the first-party call tree from an entry point, annotated with effects.")
        {
            from,
            full,
            summary,
            effects,
            async,
            raw,
            files,
            signatures,
            rules,
            depth,
            only,
            exclude,
            noCache,
            time,
        };
        // --full / --summary / --effects are three distinct projections of the same tree; only one applies.
        cmd.Validators.Add(result =>
        {
            var present = new List<string>();
            if (result.GetValue(full))
                present.Add("--full");
            if (result.GetValue(summary))
                present.Add("--summary");
            if (result.GetValue(effects))
                present.Add("--effects");
            if (present.Count > 1)
                result.AddError($"Options {string.Join(" and ", present)} can't be combined for 'rig tree'.");
        });
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        pr.GetValue(from)!,
                        pr.GetValue(full),
                        pr.GetValue(summary),
                        pr.GetValue(effects),
                        pr.GetValue(async),
                        pr.GetValue(raw),
                        pr.GetValue(files),
                        pr.GetValue(signatures),
                        CommonOptions.RulesOf(pr.GetValue(rules)),
                        pr.GetValue(depth),
                        CommonOptions.FilterSet(pr.GetValue(only)),
                        CommonOptions.FilterSet(pr.GetValue(exclude)),
                        pr.GetValue(noCache),
                        pr.GetValue(time),
                        output,
                        error,
                        workingDirectory
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        string fromPattern,
        bool full,
        bool summary,
        bool effectsOnly,
        bool async,
        bool raw,
        bool files,
        bool signatures,
        IReadOnlyList<string> extraRules,
        int? depth,
        HashSet<string> only,
        HashSet<string> exclude,
        bool noCache,
        bool time,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        var maxDepth = CommonOptions.DepthOrUnbounded(depth);
        var mode = CommonOptions.Mode(async);
        var shaping = ShapingRuleSet.Load(workingDirectory, extraRules, raw);
        // Codebase-specific render rules (collapse fan-out seams / opaque infra types). Presentation
        // only — never affects reach. `--raw` bypasses them to print the exact unfiltered tree.
        var renderRules = raw ? FactRenderRules.Empty : FactRenderRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);

        await using var context = OpenReadContext(workingDirectory);
        var timer = new PhaseTimer(time, error);

        // Query cache (best-effort, opt-out via --no-cache). A `rig tree` query recomputes the call-tree
        // forest (BuildTree) AND its effects (the ~3.8s dominant cost); both are a pure function of the
        // store + effective rules + traversal params. Cache the pair in a separate writable `.rig/cache.db`
        // (rig.db itself is opened read-only); a repeat query skips both and only re-loads the cheaper graph
        // to render. Auto-invalidates on reindex: the key embeds a store identity that index/graph change.
        var rigDir = Path.Combine(workingDirectory, ".rig");
        var storeKey = StoreKey(Path.Combine(rigDir, "rig.db"));
        using var cache = noCache ? null : QueryCache.Open(rigDir, storeKey);
        var cacheKey = cache is null
            ? null
            : TreeCacheKey(storeKey, RulesFingerprint.Compute(workingDirectory, extraRules), fromPattern, maxDepth, mode, raw);
        var cached = cacheKey is not null && cache!.Get(cacheKey) is { } blob ? TreeCacheCodec.Decode(blob) : null;
        // Render sidecar: everything render needs from the graph (seam effects + locations), keyed by the
        // forest key PLUS the effect filters. Seam effects are derived from the FILTERED effects, and filters
        // are absent from the forest key (effects cached unfiltered, re-filtered per query), so the sidecar
        // must key on them — else a differently-filtered warm query would render stale seam summaries.
        var sidecarKey = cacheKey is null ? null : cacheKey + ":sidecar:" + EffectFilterSignature(only, exclude);
        var sidecar = cached is not null && sidecarKey is not null && cache!.Get(sidecarKey) is { } scBlob ? RenderSidecarCodec.Decode(scBlob) : null;
        timer.Lap($"cache lookup (forest={cached is not null}, sidecar={sidecar is not null})");

        FactGraphData? graph = null; // stays null on a full hit (forest + sidecar) — the graph is never loaded
        IReadOnlyList<TraceNode> roots;
        IReadOnlyList<DerivedEffect> effects;
        if (cached is not null && sidecar is not null)
        {
            // FULL HIT: forest + effects + render sidecar all cached → render without touching the graph.
            roots = cached.Forest;
            effects = cached.Effects;
            timer.Lap("forest + sidecar hit (no graph load)");
        }
        else if (cached is not null)
        {
            // Forest hit but no sidecar (a pre-sidecar entry, or first run under this filter): load the
            // shaped graph to render — the sidecar is written below so the NEXT query is a full hit.
            roots = cached.Forest;
            effects = cached.Effects;
            graph = await LoadShapedTraversalGraphAsync(
                context: context,
                pattern: fromPattern,
                direction: SqlReachability.Direction.Forward,
                handoffRules: shaping.Handoff,
                factoryRules: shaping.Factory,
                cutRules: shaping.Cut,
                contextRules: shaping.Context
            );
            if (!raw)
                graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
            timer.Lap("graph load + event marking (cache hit)");
        }
        else
        {
            var inputs = await LoadEffectReachInputsAsync(
                context: context,
                pattern: fromPattern,
                direction: SqlReachability.Direction.Forward,
                handoffRules: shaping.Handoff,
                factoryRules: shaping.Factory,
                cutRules: shaping.Cut,
                contextRules: shaping.Context
            );
            graph = inputs.Graph;
            timer.Lap("graph + invocations load");
            // Event subscriptions (`someEvent += Handler`) are deferred handlers, not synchronous calls —
            // mark them as handoffs so the sync tree doesn't expand the handler as if RegisterEvents ran it.
            if (!raw)
                graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));

            roots = FactPathFinder.BuildTree(graph, fromPattern, maxDepth, mode: mode);
            timer.Lap("event marking + BuildTree");
            if (roots.Count == 0)
            {
                effects = [];
            }
            else
            {
                // Effects per enclosing method — same derivation as `reaches` (incl. throws). Cache them
                // UNFILTERED; --only/--exclude are applied below so they don't fragment the key.
                effects = DeriveEffects(
                    workingDirectory: workingDirectory,
                    extraRules: extraRules,
                    invocations: inputs.Invocations,
                    baseEdges: BaseEdgeTuples(graph),
                    ctorRefs: inputs.CtorRefs,
                    throwRefs: inputs.ThrowRefs
                );
                if (cacheKey is not null)
                    TryCache(() => cache!.Put(cacheKey, TreeCacheCodec.Encode(new TreeCachePayload(roots, effects))));
                timer.Lap("derive effects + cache store");
            }
        }

        if (roots.Count == 0)
        {
            output.WriteLine($"No symbol matches '{fromPattern}'.");
            return 1;
        }

        // Deployment attribution (opt-in via deployments.json) + EP-site lookup, so tree nodes that are
        // themselves entry points get the ▶ kind + service chip. Null when unconfigured (default tree).
        // Locations (method DocID -> file:line): from the sidecar on a full hit, else from the graph.
        // One map serves both the EP-chip site lookup and `--files` links.
        IReadOnlyDictionary<string, (string? File, int Line)> locations =
            sidecar?.Locations
            ?? graph!
                .Methods.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().FilePath, g.First().Line), StringComparer.Ordinal);

        var deployments = await LoadDeploymentsAsync(context, workingDirectory);
        // EP context is built from `locations` (not the graph), so it works on the no-graph full-hit path.
        // The expensive, pattern-independent site->kind map is its own cache (LoadOrDeriveEpSiteKind).
        var epContext = deployments.IsEmpty
            ? null
            : new EpRenderContext(
                Deployments: deployments,
                SiteById: locations,
                EpSiteKind: await LoadOrDeriveEpSiteKindAsync(context, workingDirectory, extraRules, shaping.Handoff, !noCache)
            );
        timer.Lap("deployment map + entry-point derivation");

        effects = ApplyEffectFilters(effects, only, exclude); // --only / --exclude (e.g. --exclude throw)

        var emoji = FactEffectEmojiProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var effectsByMethod = effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => FormatEffectGroup(g, emoji), StringComparer.Ordinal);

        // `--full` renders effects AND unresolved library calls as leaf nodes (call site + line), source-
        // ordered per method, rather than the compact inline tag. Only built in --full; other modes never
        // read it, so the extra library-call query never touches the default/compact path.
        IReadOnlyDictionary<string, List<string>>? effectLeavesByMethod = null;
        if (full)
        {
            var leafRows = new List<(string Method, int Line, string Body)>();
            foreach (var e in effects.Where(e => e.EnclosingSymbolId is not null))
                leafRows.Add((e.EnclosingSymbolId!, e.Line, FormatEffectLeaf(e, emoji)));

            // Unresolved library calls: invocations to a referenced-assembly target that produced no effect
            // (no rule matched). Bounded to the rendered tree's methods; subtract the effect call-sites so a
            // call already shown as an effect leaf isn't doubled.
            var treeMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
                CollectTreeMethods(root, treeMethods);
            var effectSites = effects.Where(e => e.EnclosingSymbolId is not null).Select(e => (e.EnclosingSymbolId!, e.Line)).ToHashSet();
            var libCalls = await Reads.LoadLibraryCallSitesAsync(context, treeMethods);
            foreach (
                var c in libCalls
                    .Where(c => c.Enclosing is not null && !effectSites.Contains((c.Enclosing!, c.Line)))
                    .DistinctBy(c => (c.Enclosing, c.Target, c.Line))
            )
                leafRows.Add((c.Enclosing!, c.Line, FormatUnresolvedLeaf(c.Target, c.FilePath, c.Line)));

            effectLeavesByMethod = leafRows
                .GroupBy(r => r.Method, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Line).Select(r => r.Body).ToList(), StringComparer.Ordinal);
        }

        if (summary)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
                CollectTreeMethods(root, seen);
            var hits = effects.Where(e => e.EnclosingSymbolId is not null && seen.Contains(e.EnclosingSymbolId)).ToList();
            output.WriteLine($"From: {fromPattern}");
            output.WriteLine($"Reachable methods: {seen.Count}");
            output.WriteLine($"Effects on reachable methods: {hits.Count}");
            foreach (var g in hits.GroupBy(h => (h.Provider, h.Operation)).OrderByDescending(g => g.Count()))
                output.WriteLine($"{Indent.L1}{g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
            timer.Total();
            return 0;
        }

        // --effects: the compact view — ONLY the methods that carry an effect, listed in source/DFS order
        // (deduped), each with its effect glyphs. Drops the entire call skeleton, so a 10-screen tree
        // collapses to one line per effectful method — "what does this entry point actually DO".
        if (effectsOnly)
        {
            var ordered = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
                CollectEffectful(root, effectsByMethod, ordered, seen);
            output.WriteLine($"From: {fromPattern}  ({ordered.Count} effectful method(s), source order)");
            foreach (var sym in ordered)
                output.WriteLine($"{Indent.L1}{ShortName(sym)}\n{Indent.L3}{string.Join("  ", effectsByMethod[sym])}");
            timer.Total();
            return 0;
        }

        // Seam effects: from the sidecar on a full hit, else computed from the (filtered) effects + graph.
        IReadOnlyDictionary<string, List<string>> seamEffects;
        if (sidecar is not null)
            seamEffects = sidecar.Value.SeamEffects;
        else
        {
            var structuredByMethod = effects
                .Where(e => e.EnclosingSymbolId is not null)
                .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
            seamEffects = ComputeSeamEffects(
                roots: roots,
                renderRules: renderRules,
                graph: graph!,
                maxDepth: maxDepth,
                mode: mode,
                structuredByMethod: structuredByMethod,
                emojiFor: (p, o) => FactEffectEmojiProvider.For(emoji, p, o)
            );
        }

        // `--files`: per-node definition location (relpath:line) for source links. Populate the render
        // sidecar (best-effort) so the next warm query renders with NO graph load — only when a graph was
        // actually loaded (cold or sidecar-miss) and caching is on.
        var locById = files ? locations : null;
        if (graph is not null && sidecarKey is not null)
            TryCache(() => cache!.Put(sidecarKey, RenderSidecarCodec.Encode(seamEffects, locations)));

        foreach (var root in roots)
        {
            if (!full && !SubtreeHasEffect(root, effectsByMethod))
                continue;
            // Fold single-impl interface/base hops (IFoo.M -> Foo.M when there's exactly one target)
            // into the impl, with a «via IFoo» marker — exact, no info loss. --raw shows the raw hops.
            RenderTreeNode(
                node: raw ? root : FoldSingleImplHops(root, effectsByMethod),
                prefix: "",
                isLast: true,
                isRoot: true,
                effectsByMethod: effectsByMethod,
                prune: !full,
                renderRules: renderRules,
                seamEffects: seamEffects,
                output: output,
                files: files,
                locById: locById,
                signatures: signatures,
                cutRules: shaping.Cut,
                epContext: epContext,
                full: full,
                effectLeavesByMethod: effectLeavesByMethod
            );
        }
        timer.Lap("seam effects + render");
        timer.Total();
        return 0;
    }
}
