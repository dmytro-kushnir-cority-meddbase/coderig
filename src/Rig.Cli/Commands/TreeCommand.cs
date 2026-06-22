using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Caching.QueryCacheKeys;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.LlmSummaryRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;
using static Rig.Cli.Rendering.TreeRenderer;

namespace Rig.Cli.Commands;

// `rig tree <from>` — the full first-party call TREE from an entry point over the fact graph (same edges
// as reaches/path: interface->impl + base->override dispatch + loop context). Default prunes to paths that
// REACH an effect; `--view full` prints every reachable method AND promotes effects/unresolved library
// calls to call-site leaf nodes; `--view summary` prints the effect-count rollup; `--view effects`
// collapses to one line per effectful method; `--view hazards` marks pattern hazards inline. Forest +
// effects are query-cached (the dominant cost); a render sidecar lets a warm query skip the graph load
// entirely.
//
// --format llm: compact TSV for LLM consumption. Composes with --view:
//   paths (default) → EffectfulPaths — effectful-paths with the ancestor spine kept; reconstructable from
//                     depth+order (6-column header: depth name arity calls effects flags).
//   full            → Full — every reachable node (same 6-column header, no parent column).
//   effects         → EffectsFlat — flat effect-bearing list (7-column header adds a parent-name column
//                     because the parent row may be absent in this gappy view).
// --format llm is rejected when combined with --view summary (different output shape) or --view hazards
// (distinct rendering).
//
// --format llm-ids: same as llm but adds explicit surrogate-id linkage (8-column header):
//   id  parent_id  depth  name  arity  calls  effects  flags
// seen rows: flags = "seen:<canonicalId>" where canonicalId is the id of the first expanded emission.
// Same --view composition rules as llm (rejects summary and hazards).
internal static class TreeCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var from = CommonOptions.Pattern(name: "from", description: "Entry-point method pattern.");
        var view = new Option<string>("--view")
        {
            Description =
                "Projection view: paths (default) — effectful-paths tree; full — every reachable method with effects/unresolved calls as leaf nodes; effects — flat list of effectful methods; summary — effect-count rollup; hazards — tree with pattern hazards (race_window/dual_write/…) inline. --format llm/llm-ids composes with paths/full/effects; summary and hazards are rejected with --format llm/llm-ids.",
            DefaultValueFactory = _ => "paths",
        };
        var async = CommonOptions.Async();
        var raw = CommonOptions.Raw();
        var files = CommonOptions.Files();
        var signatures = CommonOptions.Signatures();
        var plain = new Option<bool>("--plain")
        {
            Description = "Drop box-drawing connectors (├─ └─ │) for pure indentation — diff-friendly.",
        };
        var rules = CommonOptions.Rules();
        var depth = CommonOptions.Depth();
        var only = CommonOptions.Only();
        var exclude = CommonOptions.Exclude();
        var noCache = CommonOptions.NoCache();
        var time = CommonOptions.Time();
        var format = CommonOptions.Format();
        var store = CommonOptions.Store();
        var suppress = new Option<string>("--suppress")
        {
            Description =
                "Comma-separated list of node kinds to suppress in --format llm/llm-ids output: ctors, lambdas, none. Default when --format llm/llm-ids: ctors,lambdas. Use none to disable all suppression. Ignored for other formats.",
        };
        var cmd = new Command(name: "tree", description: "Print the first-party call tree from an entry point, annotated with effects.")
        {
            from,
            view,
            async,
            raw,
            files,
            signatures,
            plain,
            rules,
            depth,
            only,
            exclude,
            noCache,
            time,
            format,
            store,
            suppress,
        };
        // --view selects one of five mutually-exclusive projections (paths/full/effects/summary/hazards).
        // --format llm and --format llm-ids are compatible with paths/full/effects but not with summary or hazards.
        cmd.Validators.Add(result =>
        {
            var viewValue = result.GetValue(view) ?? "paths";
            var formatValue = result.GetValue(format);
            var isLlmFormat = string.Equals(formatValue, "llm", StringComparison.OrdinalIgnoreCase);
            var isLlmIdsFormat = string.Equals(formatValue, "llm-ids", StringComparison.OrdinalIgnoreCase);

            var validViews = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "paths", "full", "effects", "summary", "hazards" };
            if (!validViews.Contains(viewValue))
            {
                result.AddError($"Unknown --view '{viewValue}'. Valid values: paths, full, effects, summary, hazards.");
            }

            // --format llm and --format llm-ids are incompatible with summary (different output shape) and hazards (distinct rendering).
            if (isLlmFormat || isLlmIdsFormat)
            {
                var formatName = isLlmIdsFormat ? "--format llm-ids" : "--format llm";
                var incompatible = new List<string>();
                if (string.Equals(viewValue, "summary", StringComparison.OrdinalIgnoreCase))
                {
                    incompatible.Add("--view summary");
                }

                if (string.Equals(viewValue, "hazards", StringComparison.OrdinalIgnoreCase))
                {
                    incompatible.Add("--view hazards");
                }

                if (incompatible.Count > 0)
                {
                    result.AddError($"{formatName} can't be combined with {string.Join(" and ", incompatible)} for 'rig tree'.");
                }
            }
        });
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                {
                    var viewValue = (pr.GetValue(view) ?? "paths").ToLowerInvariant();
                    var formatValue = pr.GetValue(format);
                    var isLlmFmt = string.Equals(formatValue, "llm", StringComparison.OrdinalIgnoreCase);
                    var isLlmIdsFmt = string.Equals(formatValue, "llm-ids", StringComparison.OrdinalIgnoreCase);
                    // --suppress is only meaningful for --format llm / llm-ids; parse it when either, else no-op.
                    var suppressSet =
                        isLlmFmt || isLlmIdsFmt
                            ? LlmSummaryRenderer.ParseSuppressSet(pr.GetValue(suppress))
                            : LlmSummaryRenderer.SuppressSet.Default;
                    return RunAsync(
                        fromPattern: pr.GetValue(from)!,
                        full: viewValue == "full",
                        summary: viewValue == "summary",
                        effectsOnly: viewValue == "effects",
                        hazards: viewValue == "hazards",
                        async: pr.GetValue(async),
                        raw: pr.GetValue(raw),
                        files: pr.GetValue(files),
                        signatures: pr.GetValue(signatures),
                        plain: pr.GetValue(plain),
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        depth: pr.GetValue(depth),
                        only: CommonOptions.FilterSet(pr.GetValue(only)),
                        exclude: CommonOptions.FilterSet(pr.GetValue(exclude)),
                        noCache: pr.GetValue(noCache),
                        time: pr.GetValue(time),
                        format: formatValue,
                        llmIds: isLlmIdsFmt,
                        suppressSet: suppressSet,
                        output: output,
                        error: error,
                        workingDirectory: workingDirectory,
                        storeRef: pr.GetValue(store)
                    );
                }
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        string fromPattern,
        bool full,
        bool summary,
        bool effectsOnly,
        bool hazards,
        bool async,
        bool raw,
        bool files,
        bool signatures,
        bool plain,
        IReadOnlyList<string> extraRules,
        int? depth,
        HashSet<string> only,
        HashSet<string> exclude,
        bool noCache,
        bool time,
        string? format,
        bool llmIds,
        LlmSummaryRenderer.SuppressSet suppressSet,
        TextWriter output,
        TextWriter error,
        string workingDirectory,
        string? storeRef
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var llmFormat = string.Equals(format, "llm", StringComparison.OrdinalIgnoreCase);
        var maxDepth = CommonOptions.DepthOrUnbounded(depth);
        var mode = CommonOptions.Mode(async);

        // One merged load for the whole command; --raw zeroes the graph-shaping + render rules (the exact
        // unfiltered tree), else they're applied. Render rules are presentation-only — never affect reach.
        var rules = RuleSetLoader.Load(workingDirectory, extraRules);
        var shaped = raw ? rules with { Factory = [], Cut = [], Context = [] } : rules;
        var renderRules = raw ? FactRenderRules.Empty : rules.Render;

        await using var context = OpenReadContext(workingDirectory, storeRef);
        var timer = new PhaseTimer(time, error);

        // Query cache (best-effort, opt-out via --no-cache). A `rig tree` query recomputes the call-tree
        // forest (BuildTree) AND its effects (the ~3.8s dominant cost); both are a pure function of the
        // store + effective rules + traversal params. Cache the pair in a separate writable `.rig/cache.db`
        // (rig.db itself is opened read-only); a repeat query skips both and only re-loads the cheaper graph
        // to render. Auto-invalidates on reindex: the key embeds a store identity that index/graph change.
        var rigDir = StoreLayout.ResolveReadStoreDir(workingDirectory, storeRef);
        var storeKey = StoreKey(Path.Combine(rigDir, StoreLayout.DbFileName));
        var rulesHash = RulesFingerprint.Compute(workingDirectory, extraRules);
        using var cache = noCache ? null : QueryCache.Open(rigDirectory: rigDir, storeKey: storeKey);
        var cacheKey = cache is null
            ? null
            : TreeCacheKey(storeKey: storeKey, rulesHash: rulesHash, fromPattern: fromPattern, maxDepth: maxDepth, mode: mode, raw: raw);

        var cached = cacheKey is not null && cache!.Get(cacheKey) is { } blob ? TreeCacheCodec.Decode(blob) : null;
        // Render data the graph would otherwise be reloaded to produce, split by filter-dependence so the
        // filter-independent half isn't duplicated across --only/--exclude combos:
        //   - locations (method DocID -> file:line) are filter- AND hazard-independent → keyed by the forest
        //     key alone (`:loc`);
        //   - seam summaries are derived from the FILTERED effects → keyed by the forest key + the filter
        //     signature (`:seam:<sig>`), since filters are absent from the forest key.
        var locKey = cacheKey is null ? null : cacheKey + ":loc";
        var seamKey = cacheKey is null ? null : cacheKey + ":seam:" + EffectFilterSignature(only, exclude);
        var cachedLocations =
            cached is not null && locKey is not null && cache!.Get(locKey) is { } locBlob ? LocationsCodec.Decode(locBlob) : null;
        var cachedSeam =
            cached is not null && seamKey is not null && cache!.Get(seamKey) is { } seamBlob ? SeamCodec.Decode(seamBlob) : null;
        // A render with NO graph load needs the forest + BOTH render halves cached.
        var fullHit = cached is not null && cachedLocations is not null && cachedSeam is not null;
        timer.Lap($"cache lookup (forest={cached is not null}, render={fullHit})");

        FactGraphData? graph = null; // stays null on a full hit (forest + render data) — the graph is never loaded
        IReadOnlyList<TraceNode> roots;
        IReadOnlyList<DerivedEffect> effects;
        if (fullHit)
        {
            // FULL HIT: forest + effects + locations + seam all cached → render without touching the graph.
            roots = cached!.Forest;
            effects = cached.Effects;
            timer.Lap("forest + render-data hit (no graph load)");
        }
        else if (cached is not null)
        {
            // Forest hit but missing render data (a pre-cache entry, or first run under this filter): load the
            // shaped graph to render — locations/seam are written below so the NEXT query is a full hit.
            roots = cached.Forest;
            effects = cached.Effects;
            graph = await LoadShapedTraversalGraphAsync(
                context: context,
                pattern: fromPattern,
                direction: SqlReachability.Direction.Forward,
                shaped
            );
            if (!raw)
            {
                graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
            }

            timer.Lap("graph load + event marking (cache hit)");
        }
        else
        {
            var inputs = await LoadEffectReachInputsAsync(
                context: context,
                pattern: fromPattern,
                direction: SqlReachability.Direction.Forward,
                shaped
            );
            graph = inputs.Graph;
            timer.Lap("graph + invocations load");
            // Event subscriptions (`someEvent += Handler`) are deferred handlers, not synchronous calls —
            // mark them as handoffs so the sync tree doesn't expand the handler as if RegisterEvents ran it.
            if (!raw)
            {
                graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));
            }

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
                    effectRules: rules.Effects,
                    observationRules: rules.Observations,
                    invocations: inputs.Invocations,
                    baseEdges: BaseEdgeTuples(graph),
                    ctorRefs: inputs.CtorRefs,
                    throwRefs: inputs.ThrowRefs
                );
                if (cacheKey is not null)
                {
                    TryCache(() => cache!.Put(cacheKey, TreeCacheCodec.Encode(new TreeCachePayload(roots, effects))));
                }

                timer.Lap("derive effects + cache store");
            }
        }

        if (roots.Count == 0)
        {
            output.WriteLine($"No symbol matches '{fromPattern}'.");
            return 1;
        }

        // --hazards: surface the pattern HAZARDS (race_window / lazy_init_race / thread_local_context /
        // dual_write / n_plus_1 / unserializable_payload) on this entry point's tree — inline ⚠ marks + a
        // summary section. A hazard is a WHOLE-STORE, per-method fact (EP-independent), so we do NOT re-derive
        // anything per EP: we load the cached whole-store hazard-augmented effect set (shared with `derive`,
        // keyed by store+rules) and FILTER it to the tree's reachable methods. That filtered set REPLACES the
        // render effects for this run — so the field-fed shared_state effects (which a plain `tree` omits, not
        // threading field refs) render too, and a static-field-RMW-only method isn't pruned. Pure lookup +
        // filter: no graph, no per-EP derive — a warm `--hazards` is a cache hit like a plain `tree`. The
        // forest/effect/sidecar caches stay hazard-free (keyed without --hazards); this set lives in its own
        // store-keyed cache namespace, so a later plain `tree` is unaffected.
        IReadOnlyList<DeriveCommand.HazardFinding> hazardFindings = [];
        IReadOnlyDictionary<string, string>? hazardsByMethod = null;
        if (hazards)
        {
            var treeMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                CollectTreeMethods(root, treeMethods);
            }

            var hazardEffects = await LoadOrDeriveHazardEffectsAsync(
                context: context,
                rigDirectory: rigDir,
                storeKey: storeKey,
                rulesHash: rulesHash,
                rules: rules,
                useCache: !noCache
            );
            effects = hazardEffects.Where(e => e.EnclosingSymbolId is not null && treeMethods.Contains(e.EnclosingSymbolId)).ToList();
            hazardFindings = DeriveCommand.HazardFindings(effects).Where(f => treeMethods.Contains(f.Enclosing)).ToList();
            hazardsByMethod = hazardFindings
                .GroupBy(f => f.Enclosing, StringComparer.Ordinal)
                .ToDictionary(keySelector: g => g.Key, elementSelector: FormatHazardMark, comparer: StringComparer.Ordinal);
            timer.Lap("hazard lookup (cached whole-store, filtered to tree)");
        }

        // Deployment attribution (opt-in via deployments.json) + EP-site lookup, so tree nodes that are
        // themselves entry points get the ▶ kind + service chip. Null when unconfigured (default tree).
        // Locations (method DocID -> file:line): from the cache when present (even when a graph was loaded
        // for the seam — they're identical), else from the graph. One map serves the EP-chip site lookup
        // AND `--files` links.
        IReadOnlyDictionary<string, (string? File, int Line)> locations =
            cachedLocations
            ?? graph!
                .Methods.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (g.First().FilePath, g.First().Line), StringComparer.Ordinal);

        // --format tsv: the full reachable tree, one row per node in DFS pre-order (depth lets a consumer
        // rebuild the hierarchy). No deployment chrome / single-impl folding — raw structure for tooling.
        // Columns: depth, symbolId, edgeKind, handoffVia, fanout, effects (comma-joined provider:operation),
        // file, line. Emitted here so it pays for neither the deployment map nor the seam computation.
        if (tsv)
        {
            var tsvEffects = ApplyEffectFilters(effects, only, exclude)
                .Where(e => e.EnclosingSymbolId is not null)
                .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
                .ToDictionary(
                    keySelector: g => g.Key,
                    elementSelector: g => string.Join(',', g.Select(e => $"{e.Provider}:{e.Operation}")),
                    comparer: StringComparer.Ordinal
                );
            foreach (var root in roots)
            {
                EmitTsvNode(root, 0, tsvEffects, locations, output);
            }

            // --hazards: the per-hazard `hazard` rows (same column contract as `derive --format tsv`) after the
            // node rows, so a consumer reads the node tree and its findings from one stream.
            foreach (var h in hazardFindings)
            {
                output.WriteLine(DeriveCommand.HazardTsvRow(h));
            }

            timer.Total();
            return 0;
        }

        var deployments = await LoadDeploymentsAsync(context, workingDirectory);
        // EP context is built from `locations` (not the graph), so it works on the no-graph full-hit path.
        // The expensive, pattern-independent site->kind map is its own cache (LoadOrDeriveEpSiteKind).
        var epContext = deployments.IsEmpty
            ? null
            : new EpRenderContext(
                Deployments: deployments,
                SiteById: locations,
                EpSiteKind: await LoadOrDeriveEpSiteKindAsync(context, workingDirectory, extraRules, rules, !noCache)
            );
        timer.Lap("deployment map + entry-point derivation");

        effects = ApplyEffectFilters(effects, only, exclude); // --only / --exclude (e.g. --exclude throw)

        // --format llm / --format llm-ids: compact flat TSV for LLM consumption. Emitted before the
        // normal render path (skips the deployment map, seam, and box-drawing chrome — those are token
        // waste for a model). Projection determined by --view: paths (default) → EffectfulPaths; full →
        // Full; effects → EffectsFlat. llm-ids adds explicit surrogate-id linkage (8-column schema).
        if (llmFormat || llmIds)
        {
            // Raw provider:operation per occurrence, keyed by enclosing symbol — the LLM renderer
            // aggregates counts itself (no emoji, no resource names).
            var rawEffectsForLlm = effects
                .Where(e => e.EnclosingSymbolId is not null)
                .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
                .ToDictionary(
                    keySelector: g => g.Key,
                    elementSelector: g => g.Select(e => $"{e.Provider}:{e.Operation}").ToList(),
                    comparer: StringComparer.Ordinal
                );
            var projection =
                full ? LlmProjection.Full
                : effectsOnly ? LlmProjection.EffectsFlat
                : LlmProjection.EffectfulPaths;
            if (llmIds)
            {
                RenderWithIds(
                    roots: roots,
                    rawEffectsByMethod: rawEffectsForLlm,
                    projection: projection,
                    output: output,
                    suppress: suppressSet
                );
            }
            else
            {
                Render(roots: roots, rawEffectsByMethod: rawEffectsForLlm, projection: projection, output: output, suppress: suppressSet);
            }

            timer.Total();
            return 0;
        }

        var emoji = rules.EffectEmoji;
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
            {
                leafRows.Add((e.EnclosingSymbolId!, e.Line, FormatEffectLeaf(e, emoji)));
            }

            // Unresolved library calls: invocations to a referenced-assembly target that produced no effect
            // (no rule matched). Bounded to the rendered tree's methods; subtract the effect call-sites so a
            // call already shown as an effect leaf isn't doubled.
            var treeMethods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                CollectTreeMethods(root, treeMethods);
            }

            var effectSites = effects.Where(e => e.EnclosingSymbolId is not null).Select(e => (e.EnclosingSymbolId!, e.Line)).ToHashSet();
            // Library-call sites are a pure function of the forest's method set → cache under the forest key
            // (`:libcalls`), recomputed only when the forest changes, not on every --full run.
            var libCallsKey = cacheKey is null ? null : cacheKey + ":libcalls";
            var libCalls = libCallsKey is not null && cache!.Get(libCallsKey) is { } lcBlob ? LibCallsCodec.Decode(lcBlob) : null;
            if (libCalls is null)
            {
                var loaded = await Reads.LoadLibraryCallSitesAsync(context, treeMethods);
                libCalls = loaded;
                if (libCallsKey is not null)
                {
                    TryCache(() => cache!.Put(libCallsKey, LibCallsCodec.Encode(loaded)));
                }
            }
            foreach (
                var c in libCalls
                    .Where(c => c.Enclosing is not null && !effectSites.Contains((c.Enclosing!, c.Line)))
                    .DistinctBy(c => (c.Enclosing, c.Target, c.Line))
            )
            {
                leafRows.Add((c.Enclosing!, c.Line, FormatUnresolvedLeaf(target: c.Target, filePath: c.FilePath, line: c.Line)));
            }

            effectLeavesByMethod = leafRows
                .GroupBy(r => r.Method, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Line).Select(r => r.Body).ToList(), StringComparer.Ordinal);
        }

        if (summary)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                CollectTreeMethods(root, seen);
            }

            var hits = effects.Where(e => e.EnclosingSymbolId is not null && seen.Contains(e.EnclosingSymbolId)).ToList();
            output.WriteLine($"From: {fromPattern}");
            output.WriteLine($"Reachable methods: {seen.Count}");
            output.WriteLine($"Effects on reachable methods: {hits.Count}");
            foreach (var g in hits.GroupBy(h => (h.Provider, h.Operation)).OrderByDescending(g => g.Count()))
            {
                output.WriteLine($"{Indent.L1}{g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
            }

            DeriveCommand.WriteHazards(output, hazardFindings, AllHazardSites);
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
            {
                CollectEffectful(root, effectsByMethod, ordered, seen);
            }

            output.WriteLine($"From: {fromPattern}  ({ordered.Count} effectful method(s), source order)");
            foreach (var sym in ordered)
            {
                output.WriteLine($"{Indent.L1}{ShortName(sym)}\n{Indent.L3}{string.Join("  ", effectsByMethod[sym])}");
            }

            DeriveCommand.WriteHazards(output, hazardFindings, AllHazardSites);
            timer.Total();
            return 0;
        }

        // Seam effects: from the cache when present, else computed from the (filtered) effects + graph.
        // Under --hazards we still REUSE a cached seam: the seam is a collapsed-fan-out provider:op rollup,
        // and whether it includes the few field-fed shared_state effects is cosmetic — not worth a graph load
        // to recompute. (The WRITE below stays gated on !hazards so a cold --hazards run, which computes seam
        // from the augmented effects, never caches that augmented seam.)
        IReadOnlyDictionary<string, List<string>> seamEffects;
        if (cachedSeam is not null)
        {
            seamEffects = cachedSeam;
        }
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
                emojiFor: (p, o) => EmojiLookup.For(emoji, provider: p, operation: o)
            );
        }

        // `--files`: per-node definition location (relpath:line) for source links. Populate the render data
        // (best-effort) so the next warm query renders with NO graph load — only when a graph was actually
        // loaded (cold or render-miss) and caching is on. Locations are filter- AND hazard-independent, so
        // they cache under the forest key always; the seam is filter-dependent and NOT cached under --hazards
        // (the augmented effects would taint the rollup that a plain `tree` would later read).
        var locById = files ? locations : null;
        if (graph is not null && locKey is not null)
        {
            TryCache(() => cache!.Put(locKey, LocationsCodec.Encode(locations)));
        }

        if (graph is not null && seamKey is not null && !hazards)
        {
            TryCache(() => cache!.Put(seamKey, SeamCodec.Encode(seamEffects)));
        }

        // Print-order source-loc dedup: collapse a repeated trailing path (the --full call-site/leaf locs AND
        // the --files 📄 definition-loc) so the file name shows only when it changes down the tree. Mode-
        // agnostic — always on; it's a no-op when no loc is rendered (default mode). One writer per forest.
        var renderOut = new SourceLocDedupWriter(output);
        var rendered = 0;
        foreach (var root in roots)
        {
            if (!full && !SubtreeHasEffect(root, effectsByMethod))
            {
                continue;
            }

            rendered++;
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
                output: renderOut,
                files: files,
                locById: locById,
                signatures: signatures,
                plain: plain,
                cutRules: shaped.Cut,
                epContext: epContext,
                full: full,
                effectLeavesByMethod: effectLeavesByMethod,
                hazardsByMethod: hazardsByMethod
            );
        }

        // The default render is EFFECTFUL: branches with no downstream effect are pruned. When the symbol
        // matched (roots is non-empty — the Count==0 case returned above) but every root pruned away, the
        // user would otherwise see a blank screen + success exit. Say what happened and point at --full,
        // instead of leaving them unsure whether the symbol was wrong or the tool failed.
        if (rendered == 0)
        {
            output.WriteLine($"No effects reachable from '{fromPattern}'. Run with --view full for the structural call tree.");
        }

        // --hazards: the summary section under the tree (reuses the `derive` Hazards renderer). Empty-safe —
        // a no-op without --hazards (hazardFindings stays []). AllHazardSites = show every site (this is the
        // bounded one-EP drill-in, not the whole-store triage list `derive` caps).
        DeriveCommand.WriteHazards(output, hazardFindings, AllHazardSites);

        timer.Lap("seam effects + render");
        timer.Total();
        return 0;
    }

    // --hazards shows EVERY finding site for the one EP being drilled into (vs. `derive`'s capped whole-store
    // triage list). WriteHazards samples `limit / 8 + 1` per type, so a large limit prints all of them.
    private const int AllHazardSites = int.MaxValue;

    // The compact inline hazard marker for one method's findings (the --hazards node annotation): the distinct
    // hazard types the method carries, each tagged with its WORST (highest) confidence, type-sorted, with a
    // `×N` suffix when a type fires more than once on the method (e.g. two distinct race windows). Terse on
    // purpose — the full per-finding evidence is in the Hazards section + the tsv `hazard` rows.
    private static string FormatHazardMark(IEnumerable<DeriveCommand.HazardFinding> findings) =>
        "  ⚠ "
        + string.Join(
            ", ",
            findings
                .GroupBy(f => f.Type, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var worst = g.Select(f => f.Confidence).OrderBy(ConfidenceRank).First();
                    return g.Count() > 1 ? $"{g.Key}({worst})×{g.Count()}" : $"{g.Key}({worst})";
                })
        );

    // Confidence sort key: high < medium < low, so OrderBy(...).First() picks the WORST (highest-severity)
    // tier a method carries for a given hazard type. Unknown tiers sort last.
    private static int ConfidenceRank(string confidence) =>
        confidence switch
        {
            "high" => 0,
            "medium" => 1,
            "low" => 2,
            _ => 3,
        };

    // One DFS pre-order row per tree node for `--format tsv`: depth (rebuilds the hierarchy), full DocID,
    // the edge kind that reached it, the async-handoff dispatcher (if any), the dispatch fan-out degree,
    // its effects (comma-joined provider:operation, empty when none), and its declaration file:line.
    private static void EmitTsvNode(
        TraceNode node,
        int depth,
        IReadOnlyDictionary<string, string> effectsByMethod,
        IReadOnlyDictionary<string, (string? File, int Line)> locations,
        TextWriter output
    )
    {
        var (file, line) = locations.TryGetValue(node.SymbolId, out var loc) ? loc : (null, 0);
        var effects = effectsByMethod.GetValueOrDefault(key: node.SymbolId, defaultValue: "");
        output.WriteLine($"{depth}\t{node.SymbolId}\t{node.EdgeKind}\t{node.HandoffVia}\t{node.Fanout}\t{effects}\t{file}\t{line}");
        foreach (var child in node.Children)
        {
            EmitTsvNode(child, depth + 1, effectsByMethod, locations, output);
        }
    }
}
