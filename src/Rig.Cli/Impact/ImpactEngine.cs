using Rig.Analysis.Rules;
using Rig.Cli.Caching;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Caching.QueryCacheKeys;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Impact;

// The reusable store-vs-store DIFF ENGINE behind `rig impact` — the compute half split out of ImpactCommand
// so the command file is thin wiring + rendering. It adds no graph code: it runs `derive`'s
// DeriveEntryPoints/DeriveEffects on each store and diffs the per-EP forward-reach footprints. Shared by the
// CLI (ImpactCommand.RunAsync) and the web /api/impact endpoint (ImpactQueryService) so the two cannot diverge.
internal static class ImpactEngine
{
    // Resolve BOTH per-commit stores up front (sha / short-sha / store-id → store dir), load the rule set,
    // and open the query cache. ResolveReadStoreDir throws StoreRefNotFoundException for an unmatched ref —
    // CommandGuard lists what's indexed, so past this point both stores exist and are addressable. The HEAD
    // store dir hosts the result cache; the cache KEY folds in BOTH store identities so reindexing either
    // side misses. Render-only flags (--structural/--format/--limit) are absent — they re-present the SAME
    // diff and must not fragment it. F6: LoadedRulePaths are passed to ComputeFromPaths so the fingerprint
    // reuses the already-resolved paths instead of re-running the cascade merge.
    private static (RuleSet Rules, string BaseDbPath, QueryCache? Cache, string? CacheKey) ResolveStoresAndCache(
        WorkspaceLocation ws,
        string baseRef,
        string headRef,
        IReadOnlyList<string> extraRules,
        bool gate,
        bool noCache,
        FactPathFinder.TraversalMode mode
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory: ws.WorkingDirectory, extraRules: extraRules, loadedPaths: out var loadedRulePaths);

        var headDir = StoreLayout.ResolveReadStoreDir(ws with { StoreRef = headRef });
        var baseDir = StoreLayout.ResolveReadStoreDir(ws with { StoreRef = baseRef });
        var baseDbPath = Path.Combine(baseDir, StoreLayout.DbFileName);
        var headStoreKey = StoreKey(Path.Combine(headDir, StoreLayout.DbFileName));
        var baseStoreKey = StoreKey(baseDbPath);
        var cache = noCache ? null : QueryCache.Open(rigDirectory: headDir, storeKey: headStoreKey);
        // Fold the shared_state:read write-pairing gate state into the rule-fingerprint slot so the gated and
        // ungated (--no-gate) diffs never share a cache entry. BOTH carry an explicit token (not the bare
        // rulesHash) so a diff cached by a PRE-gate binary can never be served as a gated result (a one-time
        // recompute on upgrade; correctness over a warm-cache hit).
        var rulesHash = RulesFingerprint.ComputeFromPaths(loadedRulePaths); // F6: reuse paths Load resolved.
        var keyRulesHash = gate ? $"{rulesHash}|gate" : $"{rulesHash}|nogate";
        var cacheKey = cache is null
            ? null
            : ImpactCacheKey(baseStoreKey: baseStoreKey, headStoreKey: headStoreKey, rulesHash: keyRulesHash, mode: mode);
        return (rules, baseDbPath, cache, cacheKey);
    }

    // The reusable store-vs-store DIFF, shared by `rig impact` (RunAsync) and the web /api/impact endpoint (via
    // ImpactQueryService). Produces the full ImpactCacheArtifact (diff + both provenances + the diff-site FQN
    // map) — everything a renderer needs — with NO rendering/deployment concerns. Warm path returns the cached
    // artifact without loading either graph; cold path loads + derives + caches. The caller owns `headContext`
    // (opened at StoreRef=headRef) so it can also read deployments off it without a second open.
    internal static async Task<ImpactCacheArtifact> DiffAsync(
        RigDbContext headContext,
        WorkspaceLocation ws,
        string baseRef,
        string headRef,
        FactPathFinder.TraversalMode mode,
        bool gate,
        bool noCache,
        IReadOnlyList<string> extraRules,
        // Optional coarse progress callback (phase name, ms since previous phase) — awaited between the
        // top-level phases so a caller (the SSE endpoint) can stream live progress on a cold diff. Null (the
        // CLI) makes this a no-op; the diff RESULT is unchanged either way.
        Func<string, long, Task>? onPhase = null
    )
    {
        var (rules, baseDbPath, cacheRaw, cacheKey) = ResolveStoresAndCache(ws, baseRef, headRef, extraRules, gate, noCache, mode);
        using var cache = cacheRaw;

        // WARM PATH: a fully-materialized diff + provenance + per-EP FQN subset → return WITHOUT loading the
        // base store or shaping/walking either graph.
        if (cacheKey is not null && cache!.Get(cacheKey) is { } cachedBlob && ImpactCacheCodec.Decode(cachedBlob) is { } art)
        {
            if (onPhase is not null)
            {
                await onPhase("cache hit", 0);
            }

            return art;
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        async Task Tick(string name)
        {
            if (onPhase is not null)
            {
                await onPhase(name, sw.ElapsedMilliseconds);
            }

            sw.Restart();
        }

        var headProv = await ReadProvenanceAsync(headContext, headRef);
        var baseProv = await ResolveBaseProvenanceAsync(baseDbPath: baseDbPath, baseRef: baseRef);
        await Tick("provenance");
        var headData = await LoadHeadSideDataAsync(headContext, rules, gate: gate);
        await Tick("head: load graph + derive effects");
        var branchSide = await ComputeBranchSideAsync(baseDbPath: baseDbPath, rules: rules, mode: mode, headData: headData);
        await Tick("head: reach sets + footprints + hazards");
        var impactDiff = await AssembleImpactDiffAsync(
            baseDbPath: baseDbPath,
            rules: rules,
            mode: mode,
            headData: headData,
            branchSide: branchSide,
            gate: gate
        );
        await Tick("base: load + derive + diff");
        var fqnSites = branchSide.IdBySite;
        TrySaveDiffToCache(cache, cacheKey, impactDiff, baseProv, headProv, fqnSites);
        return new ImpactCacheArtifact(Diff: impactDiff, BaseProvenance: baseProv, HeadProvenance: headProv, FqnSites: fqnSites);
    }

    // The HEAD (branch) store data needed by the per-EP computations: the shaped graph, the entry points,
    // the derived effects, the method-id-by-site index, the body hashes, and the field-access ref targets.
    // Loaded once and threaded into ComputeBranchSideAsync and AssembleImpactDiffAsync so no second open is
    // needed for the branch store. Hazard delta: F8 combined single-kind scan mirrors `derive` / the base side.
    private sealed record HeadSideData(
        FactGraphData Graph,
        IReadOnlyList<DerivedEntryPoint> DerivedEps,
        IReadOnlyList<DerivedEntryPoint> PromotedEps,
        IReadOnlyList<DerivedEffect> Effects,
        Dictionary<(string, int), string> IdBySite,
        IReadOnlyDictionary<string, string> BodyHashes,
        IReadOnlyDictionary<string, IReadOnlyList<string>> RefTargets
    );

    private static async Task<HeadSideData> LoadHeadSideDataAsync(RigDbContext context, RuleSet rules, bool gate = true)
    {
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        // The branch's per-symbol declaration body hashes (guarded — empty on a pre-fact store). Diffed against
        // the base's (loaded once in ComputeBaseSideAsync) to find in-place body edits the reach-set diff misses.
        var branchBodyHashes = await Reads.LoadSymbolBodyHashesAsync(context);
        // Fully shaped graph: handoff-classified load → ShapeGraph → MarkEventSubscriptionHandoffs →
        // AddDeliveryEdges. Impact walks the delivery-edge-bearing graph, so per-EP --async reach includes
        // event/actor delivery paths.
        var graph = await Reads.LoadShapedGraphAsync(context: context, rules: rules);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        // Hazard delta: impact loads the static-field read/write refs and runs the hazard post-pass on BOTH
        // stores (mirroring `derive`), so the derived effects carry hazard observations (race_window /
        // lazy_init_race; n_plus_1 / unserializable_payload ride along via the observation rules). Scoped to
        // impact — tree/reaches are untouched.
        // F8: one combined scan (RefKind in {read,write}) instead of two back-to-back single-kind queries.
        var (staticFieldWriteRefs, staticFieldReadRefs) = await Reads.LoadStaticFieldAccessRefsByKindAsync(context);
        var threadStaticCells = await Reads.LoadThreadStaticFieldIdsAsync(context);
        var volatileCells = await Reads.LoadVolatileFieldIdsAsync(context);
        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs,
            staticFieldWriteRefs: staticFieldWriteRefs,
            staticFieldReadRefs: staticFieldReadRefs,
            deriveHazards: true,
            threadStaticCells: threadStaticCells,
            volatileCells: volatileCells,
            gate: gate
        );
        // The branch's enclosing→field/property-access-targets lookup, built ONCE so ComputeReachSets can union
        // each reachable method's read/write targets as degenerate `R:` nodes at O(reach) cost.
        var refTargets = RefTargetsByEnclosing(await Reads.LoadFieldAccessRefsAsync(context));
        return new HeadSideData(
            Graph: graph,
            DerivedEps: epSet.Derived,
            PromotedEps: epSet.PromotedOrigins,
            Effects: effects,
            IdBySite: MethodIdBySite(methods),
            BodyHashes: branchBodyHashes,
            RefTargets: refTargets
        );
    }

    // The branch-side per-EP outputs computed over the already-loaded HEAD data: the entry-point set diff
    // vs the base, the branch reach sets, the (Kind, Route) → EP-ref site map, the effect footprints, and
    // the hazard sets. Bundled so AssembleImpactDiffAsync can consume them without re-opening the HEAD store.
    private sealed record BranchSideData(
        EpDiff EpDiff,
        Dictionary<(string Kind, string Route), HashSet<string>> ReachSets,
        Dictionary<(string Kind, string Route), EntryPointRef> EpByKey,
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> Footprints,
        Dictionary<(string Kind, string Route), HashSet<HazardFinding>> Hazards,
        Dictionary<(string, int), string> IdBySite
    );

    private static async Task<BranchSideData> ComputeBranchSideAsync(
        string baseDbPath,
        RuleSet rules,
        FactPathFinder.TraversalMode mode,
        HeadSideData headData
    )
    {
        // --- Two-store entry-point diff: EPs added/removed vs the base store, paired on (Kind, Route) —
        // line/param-free, so formatting + signature edits don't churn the diff.
        var branchEps = headData.DerivedEps.Concat(headData.PromotedEps).ToList();
        var epDiff = await ComputeEpDiffAsync(baseDbPath, branchEps, rules);

        // --- Per-EP store-vs-store diff. The AFFECTED ENTRY POINTS are computed STRUCTURALLY: per EP, diff
        // its full reachable symbol set branch vs base ("two trees, diffed") — an EP is affected iff WHAT IT
        // REACHES changed, regardless of whether an effect rule fired. This catches the obj→sql kind of
        // migration the effect-set diff collapses (same key, different symbols), and excludes false positives.
        var branchReachSets = ComputeReachSets(headData.Graph, branchEps, headData.IdBySite, mode, refsByEnclosing: headData.RefTargets);
        var epByKey = branchEps
            .GroupBy(e => (e.Kind, e.Route))
            .ToDictionary(
                g => g.Key,
                g => new EntryPointRef(
                    Kind: g.Key.Kind,
                    Route: g.Key.Route,
                    FilePath: g.First().FilePath,
                    Line: g.First().Line,
                    Requires: g.First().Requires
                )
            );
        var branchFootprints = ComputeFootprints(
            headData.Graph,
            branchEps,
            headData.IdBySite,
            EffectKeysByEnclosing(headData.Effects),
            mode
        );
        // The branch's per-EP reachable-hazard set (hazard mirror of the footprint), diffed against the base's
        // in DiffFootprints so each per-EP delta carries the hazards gained/lost.
        var branchHazards = ComputeHazardSets(headData.Graph, branchEps, headData.IdBySite, HazardsByEnclosing(headData.Effects), mode);
        return new BranchSideData(
            EpDiff: epDiff,
            ReachSets: branchReachSets,
            EpByKey: epByKey,
            Footprints: branchFootprints,
            Hazards: branchHazards,
            IdBySite: headData.IdBySite
        );
    }

    // Load the base store ONCE (via ComputeBaseSideAsync), diff the branch reach sets and footprints against
    // it, and assemble the three signals (EP-set diff, structural affected EPs, per-EP behavioral deltas) into
    // one ImpactDiff. The branch store is represented by headData + branchSide — no second HEAD open needed.
    private static async Task<ImpactDiff> AssembleImpactDiffAsync(
        string baseDbPath,
        RuleSet rules,
        FactPathFinder.TraversalMode mode,
        HeadSideData headData,
        BranchSideData branchSide,
        bool gate = true
    )
    {
        var baseSide = await ComputeBaseSideAsync(baseDbPath: baseDbPath, rules: rules, mode: mode, gate: gate);

        // The symbols whose declaration BODY changed base↔branch (differing/one-sided hash). An EP whose reach
        // intersects this set is affected IN-PLACE even when its structural reach-set diff is empty. Both maps
        // empty (pre-fact store on either side) => BodyChangedSymbols returns empty and the signal degrades
        // silently. branchBodyHashes is loaded once from the branch context above (headData.BodyHashes).
        var bodyChanged = BodyChangedSymbols(branchHashes: headData.BodyHashes, baseHashes: baseSide.BodyHashes);
        var affectedEntryPoints = DiffReachSets(
            branch: branchSide.ReachSets,
            baseStore: baseSide.ReachSets,
            epByKey: branchSide.EpByKey,
            bodyChanged: bodyChanged
        );
        var perEpDeltas = DiffFootprints(
            branch: branchSide.Footprints,
            baseStore: baseSide.Footprints,
            epByKey: branchSide.EpByKey,
            branchHazards: branchSide.Hazards,
            baseHazards: baseSide.Hazards
        );
        return new ImpactDiff(Ep: branchSide.EpDiff, AffectedEps: affectedEntryPoints, PerEp: perEpDeltas);
    }

    // Write the proven diff + both sides' provenance + the diff-site FQN subset to the cache (best-effort).
    // Stored UNTRUNCATED (--limit is a render concern), so every --limit value renders correctly from one blob.
    // No-ops when caching is disabled (cacheKey is null).
    private static void TrySaveDiffToCache(
        QueryCache? cache,
        string? cacheKey,
        ImpactDiff impactDiff,
        StoreProvenance baseProv,
        StoreProvenance headProv,
        Dictionary<(string, int), string> fqnSites
    )
    {
        if (cacheKey is null)
        {
            return;
        }

        TryCache(() =>
            cache!.Put(
                key: cacheKey,
                payload: ImpactCacheCodec.Encode(diff: impactDiff, baseProvenance: baseProv, headProvenance: headProv, idBySite: fqnSites)
            )
        );
    }

    // The count of EPs whose reachable EFFECT SET changed (added / removed / amplified) — the FR-4
    // behavioral count, and the headline "changed behavior" number. PerEp ALSO contains EPs whose only
    // delta is a HAZARD gain/loss (race_window / n+1 / …) so they surface in the per-EP section; those must
    // NOT count here. --expect-no-effect-change is a deterministic effect-set gate — gating CI on a
    // (often heuristic) hazard belongs to a separate opt-in (e.g. a future --expect-no-hazard-gain), not
    // this flag. So a behavior-preserving refactor that merely trips a hazard heuristic stays green here.
    internal static int EffectChangedEpCount(ImpactDiff diff) =>
        diff.PerEp.Count(d => d.Added.Count > 0 || d.Removed.Count > 0 || d.Amplified.Count > 0);

    // Read a store's provenance from its own run row (the run with the most symbols — the primary index).
    // Short sha = first 12 chars, matching `rig runs`. Fallback is the store-ref the user passed.
    private static async Task<StoreProvenance> ReadProvenanceAsync(RigDbContext context, string storeRef)
    {
        var runs = await Reads.ListRunsAsync(context);
        var primary = runs.OrderByDescending(r => r.SymbolCount).FirstOrDefault();
        var commit = primary?.SourceCommit;
        var shortSha = commit is { Length: > 0 } ? (commit.Length >= 12 ? commit[..12] : commit) : null;
        return new StoreProvenance(Branch: primary?.SourceBranch, ShortCommit: shortSha, Fallback: storeRef);
    }

    // The base store's provenance — opened read-only for just its run row.
    private static async Task<StoreProvenance> ResolveBaseProvenanceAsync(string baseDbPath, string baseRef)
    {
        await using var baseContext = new RigDbContext(baseDbPath, readOnly: true);
        return await ReadProvenanceAsync(baseContext, baseRef);
    }

    // Derive entry points on the base store and set-diff them against the branch's, keyed on (Kind, Route).
    // DeriveEntryPointsAsync derives straight from the passed context with rules loaded from the (shared)
    // working dir — no query cache — so running it on a second store is correct. Internal for testing.
    internal static async Task<EpDiff> ComputeEpDiffAsync(string baseDbPath, IReadOnlyList<DerivedEntryPoint> branchEps, RuleSet rules)
    {
        await using var baseContext = new RigDbContext(baseDbPath, readOnly: true);
        var baseEpData = await Reads.LoadFactEntryPointDataAsync(baseContext);
        var baseSet = await DeriveEntryPointsAsync(baseContext, baseEpData, rules);
        var baseEps = baseSet.Derived.Concat(baseSet.PromotedOrigins).ToList();

        var branchKeys = branchEps.Select(e => (e.Kind, e.Route)).ToHashSet();
        var baseKeys = baseEps.Select(e => (e.Kind, e.Route)).ToHashSet();

        var added = branchKeys
            .Where(k => !baseKeys.Contains(k))
            .OrderBy(k => k.Kind, StringComparer.Ordinal)
            .ThenBy(k => k.Route, StringComparer.Ordinal)
            .ToList();
        var removed = baseKeys
            .Where(k => !branchKeys.Contains(k))
            .OrderBy(k => k.Kind, StringComparer.Ordinal)
            .ThenBy(k => k.Route, StringComparer.Ordinal)
            .ToList();
        return new EpDiff(Added: added, Removed: removed);
    }

    // Strip a DocID's parameter list (and leading `M:`) to a param-free `Namespace.Type.Method` key.
    // Delegates to the shared SymbolNameFormatter.FqnFromDocId so the impact EP card and the derive/
    // entrypoints/callers EP listings render the identical FQN form from one implementation.
    internal static string StripParams(string? docId) => FqnFromDocId(docId);

    // The copy-pasteable label for an EP card: the method's fully-qualified dotted name (namespace.Type.Member),
    // resolved from the EP's (FilePath, Line) against the in-RAM method index (no extra store I/O — idBySite is
    // already built for reach computation). This is the exact suffix `rig tree <from>` matches on, so a card
    // label round-trips straight into a tree query. Falls back to the path-style Route when the site maps to no
    // indexed method symbol (synthesized/promoted handoff EPs, lambdas) — those keep their derived route.
    // Internal for testing — the route↔FQN resolution is the contract behind "the card always shows a dotted
    // name when the site resolves, else the route".
    internal static string FqnForCard(string route, string filePath, int line, Dictionary<(string, int), string> idBySite) =>
        !string.IsNullOrEmpty(filePath) && idBySite.TryGetValue((filePath, line), out var docId) ? StripParams(docId) : route;

    // The providers that count as a concurrency GUARD (a lock/atomic acquired or released on a path). Used
    // by the FR-1e guard-delta callout: a guard added/removed on a path that still mutates shared state.
    private static readonly HashSet<string> GuardProviders = new(StringComparer.Ordinal) { "lock", "async_lock" };

    // FR-1e — the guard delta on a shared-mutation path. From one EP's footprint delta, the lock/async_lock
    // effects it GAINED (Added) and LOST (Removed), as "provider:operation" labels. Pure + derivable from the
    // already-computed Added/Removed sets (the guard effects ARE effects), so it needs nothing the diff didn't
    // already carry. Empty lists when no guard moved. Internal for unit-testing the classification.
    internal static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) GuardEffectDelta(EpFootprintDelta d)
    {
        static List<string> Guards(IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> keys) =>
            keys.Where(k => GuardProviders.Contains(k.Provider))
                .Select(k => $"{k.Provider}:{k.Operation}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToList();

        return (Guards(d.Added), Guards(d.Removed));
    }

    // FR-1e fires for an EP when a guard (lock/async_lock) was added OR removed on its path AND the branch
    // path STILL carries a shared_state mutation — i.e. the concurrency protection around an inherently-shared
    // mutation changed. Both the lost-guard case (mutation now unguarded) and the gained-guard case (a fix) are
    // flagged for review; the static signal asserts the delta, not a verdict on correctness.
    internal static bool HasGuardDeltaOnSharedMutation(EpFootprintDelta d)
    {
        if (!d.SharedMutationOnPath)
        {
            return false;
        }

        var (added, removed) = GuardEffectDelta(d);
        return added.Count > 0 || removed.Count > 0;
    }

    // enclosing-method-id -> the distinct effect keys (provider, op, resource, param-free enclosing) declared
    // there, so a per-EP footprint is assembled by unioning the effects of every reachable enclosing node.
    private static Dictionary<string, List<(string, string, string, string)>> EffectKeysByEnclosing(IReadOnlyList<DerivedEffect> effects) =>
        effects
            .Where(e => e.EnclosingSymbolId is not null)
            .GroupBy(e => e.EnclosingSymbolId!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(e => (e.Provider, e.Operation, e.ResourceType, StripParams(e.EnclosingSymbolId))).Distinct().ToList(),
                StringComparer.Ordinal
            );

    // enclosing-method-id -> the distinct HAZARD findings (Type, Cell, param-free enclosing, Confidence)
    // declared there, so a per-EP hazard set is assembled by unioning the hazards of every reachable enclosing
    // node — exactly as EffectKeysByEnclosing does for effects. A hazard finding is an EffectObservationInfo
    // whose Type is in HazardKinds (race_window / lazy_init_race / n_plus_1 / unserializable_payload), found on
    // an effect's Observations. Cell = the observation's Context (the shared cell / loop identifier / payload
    // type — the same field the cli renders); Confidence rides along (not part of the diff identity, see the
    // record). An effect with no hazard observation contributes nothing. Distinct so two effects in one method
    // bearing the same finding count once.
    private static Dictionary<string, List<HazardFinding>> HazardsByEnclosing(IReadOnlyList<DerivedEffect> effects) =>
        effects
            .Where(e => e.EnclosingSymbolId is not null && e.Observations is not null)
            .SelectMany(e =>
                e.Observations!.Where(o => HazardKinds.IsHazard(o.Type))
                    .Select(o =>
                        (
                            Enclosing: e.EnclosingSymbolId!,
                            Finding: new HazardFinding(
                                Type: o.Type,
                                Cell: o.Context,
                                Enclosing: StripParams(e.EnclosingSymbolId),
                                Confidence: o.Confidence
                            )
                        )
                    )
            )
            .GroupBy(x => x.Enclosing, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(x => x.Finding).Distinct().ToList(), StringComparer.Ordinal);

    // The reachable-HAZARD set of each entry point, keyed on (Kind, Route), over an ALREADY-LOADED graph +
    // hazards-by-enclosing — the hazard mirror of ComputeFootprints. Forward-reaches every EP and unions the
    // hazard findings of each reachable enclosing node. Same seed/depth/mode contract as ComputeFootprints so
    // the two are computed over the identical reach. An EP whose reach bears no hazard maps to an empty set.
    private static Dictionary<(string Kind, string Route), HashSet<HazardFinding>> ComputeHazardSets(
        FactGraphData graph,
        IReadOnlyList<DerivedEntryPoint> eps,
        Dictionary<(string, int), string> idBySite,
        Dictionary<string, List<HazardFinding>> hazardsByEnclosing,
        FactPathFinder.TraversalMode mode
    )
    {
        var distinct = eps.GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line)).Select(g => g.Key).ToList();
        var seedIds = distinct.Select(e => idBySite.TryGetValue((e.FilePath, e.Line), out var id) ? id : "").ToList();
        var reached = FactPathFinder.ReachesFromEachSeed(graph, seedIds, maxDepth: int.MaxValue, mode: mode);

        var sets = new Dictionary<(string, string), HashSet<HazardFinding>>();
        for (var i = 0; i < distinct.Count; i++)
        {
            var key = (distinct[i].Kind, distinct[i].Route);
            if (!sets.TryGetValue(key, out var set))
            {
                sets[key] = set = new HashSet<HazardFinding>();
            }

            foreach (var node in reached[i])
            {
                if (hazardsByEnclosing.TryGetValue(node, out var findings))
                {
                    set.UnionWith(findings);
                }
            }
        }

        return sets;
    }

    // (FilePath, Line) -> the method declared there, so an EP (which carries a declaration site, not an id)
    // can seed a forward reach from its method node.
    private static Dictionary<(string, int), string> MethodIdBySite(IReadOnlyList<DeadCodeFinder.MethodMeta> methods)
    {
        var idBySite = new Dictionary<(string, int), string>();
        foreach (var m in methods)
        {
            if (!string.IsNullOrEmpty(m.FilePath))
            {
                idBySite[(m.FilePath, m.Line)] = m.SymbolId;
            }
        }

        return idBySite;
    }

    // The reachable-effect footprint of each entry point, keyed on (Kind, Route), over an ALREADY-LOADED graph
    // + effects (no store I/O — the caller loaded the store once). Forward-reaches every EP in parallel
    // (ReachesInfoFromEachSeed: one shared index, all cores). An EP whose (FilePath, Line) maps to no method
    // node seeds empty; duplicate (Kind, Route) sites union their footprints.
    //
    // Feature 1 (amplification): the inner value is no longer a bare effect-key SET but a per-key EffectReach
    // carrying CARDINALITY + a LOOP flag. Count = number of distinct reachable effect-bearing enclosing nodes
    // that produce the key (a derivable multiplicity — produced from more sites ⇒ higher count). InLoop ORs
    // the NearestLoopKind of each producing node over the EP's forward reach (the same BFS loop context the
    // tree's 🔁 marker uses; available identically on both stores). The set-diff over the key SET is recovered
    // by DiffFootprints reading the dictionary keys, so Added/Removed semantics are unchanged.
    private static Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> ComputeFootprints(
        FactGraphData graph,
        IReadOnlyList<DerivedEntryPoint> eps,
        Dictionary<(string, int), string> idBySite,
        Dictionary<string, List<(string, string, string, string)>> effectsByEnclosing,
        FactPathFinder.TraversalMode mode
    )
    {
        var distinct = eps.GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line)).Select(g => g.Key).ToList();
        var seedIds = distinct.Select(e => idBySite.TryGetValue((e.FilePath, e.Line), out var id) ? id : "").ToList();
        // Unbounded depth (matching `reaches`/`tree`): the default maxDepth=20 truncates effects whose shortest
        // reach is deeper than 20 hops, which made impact emit spurious per-EP +/- deltas when a change merely
        // shifted an effect's shortest depth across the 20 boundary. maxNodes + cycle dedup still bound/terminate.
        var reached = FactPathFinder.ReachesInfoFromEachSeed(graph, seedIds, maxDepth: int.MaxValue, mode: mode);

        var footprints = new Dictionary<(string, string), Dictionary<(string, string, string, string), EffectReach>>();
        for (var i = 0; i < distinct.Count; i++)
        {
            var key = (distinct[i].Kind, distinct[i].Route);
            if (!footprints.TryGetValue(key, out var perKey))
            {
                footprints[key] = perKey = new Dictionary<(string, string, string, string), EffectReach>();
            }

            // Walk the EP's reachable nodes; for each effect-bearing one, accumulate its effect keys' count
            // (one per distinct producing node) and OR-in whether that node is reached under a loop.
            foreach (var (node, info) in reached[i])
            {
                if (!effectsByEnclosing.TryGetValue(node, out var keys))
                {
                    continue;
                }

                var nodeInLoop = info.NearestLoopKind is not null;
                foreach (var ek in keys)
                {
                    var prev = perKey.TryGetValue(ek, out var r) ? r : new EffectReach(Count: 0, InLoop: false);
                    perKey[ek] = new EffectReach(Count: prev.Count + 1, InLoop: prev.InLoop || nodeInLoop);
                }
            }
        }

        return footprints;
    }

    // Phase 3: the degenerate field/property-access nodes contributed by a set of reachable methods. For each
    // reachable enclosing method, union in its first-party read/write reference TARGETS, `R:`-prefixed. Pure +
    // internal so the union step is unit-testable WITHOUT a store. The caller passes a PREBUILT
    // enclosing→targets lookup (built once per store), so this is O(reachable) lookups, not a per-EP ref scan.
    internal static IReadOnlyCollection<string> RefTargetsFor(
        IReadOnlySet<string> reachableMethods,
        IReadOnlyDictionary<string, IReadOnlyList<string>> refsByEnclosing
    )
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        foreach (var method in reachableMethods)
        {
            if (refsByEnclosing.TryGetValue(method, out var targets))
            {
                foreach (var t in targets)
                {
                    union.Add(RefNodePrefix + t);
                }
            }
        }

        return union;
    }

    // Per-EP REACHABLE SYMBOL SET over an already-loaded graph (no store I/O). Mirrors ComputeFootprints but
    // collects the raw reachable method DocIDs instead of mapping them to effect keys — so a structural diff
    // sees every reachable-set change, not just effect-classified ones. Duplicate (Kind, Route) sites union.
    // refsByEnclosing (Phase 3, optional) unions each reachable method's first-party field/property read/write
    // TARGETS into the set as `R:`-prefixed degenerate leaf nodes, so a changed access surfaces in the diff.
    private static Dictionary<(string Kind, string Route), HashSet<string>> ComputeReachSets(
        FactGraphData graph,
        IReadOnlyList<DerivedEntryPoint> eps,
        Dictionary<(string, int), string> idBySite,
        FactPathFinder.TraversalMode mode,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? refsByEnclosing = null
    )
    {
        var distinct = eps.GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line)).Select(g => g.Key).ToList();
        var seedIds = distinct.Select(e => idBySite.TryGetValue((e.FilePath, e.Line), out var id) ? id : "").ToList();
        // Unbounded depth (matching `reaches`/`tree`): the default maxDepth=20 truncates effects whose shortest
        // reach is deeper than 20 hops, which made impact emit spurious per-EP +/- deltas when a change merely
        // shifted an effect's shortest depth across the 20 boundary. maxNodes + cycle dedup still bound/terminate.
        var reached = FactPathFinder.ReachesFromEachSeed(graph, seedIds, maxDepth: int.MaxValue, mode: mode);

        var sets = new Dictionary<(string, string), HashSet<string>>();
        for (var i = 0; i < distinct.Count; i++)
        {
            var key = (distinct[i].Kind, distinct[i].Route);
            if (!sets.TryGetValue(key, out var set))
            {
                sets[key] = set = new HashSet<string>(StringComparer.Ordinal);
            }

            set.UnionWith(reached[i]);
            if (refsByEnclosing is not null)
            {
                set.UnionWith(RefTargetsFor(reached[i], refsByEnclosing));
            }
        }

        return sets;
    }

    // Build the enclosing-method → first-party field/property read/write TARGET-DocIDs lookup ONCE per store
    // (Phase 3). Looked up per reachable method in ComputeReachSets, so the per-EP cost stays O(reach); without
    // this prebuild it would be an O(EPs × all-refs) re-scan. Distinct targets per enclosing method.
    private static Dictionary<string, IReadOnlyList<string>> RefTargetsByEnclosing(IReadOnlyList<SymbolRef> fieldAccessRefs) =>
        fieldAccessRefs
            .Where(r => r.Enclosing is not null)
            .GroupBy(r => r.Enclosing!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.Target).Distinct(StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal
            );

    // Diff two stores' per-EP reachable symbol sets: for every EP present in BOTH (paired on Kind+Route), the
    // methods its reach gained/lost. Returns only EPs whose reach changed, busiest-delta first. EPs added/
    // removed wholesale are the entry-point-diff section's job. epByKey supplies the EP's site for rendering.
    internal static IReadOnlyList<EpReachDelta> DiffReachSets(
        Dictionary<(string Kind, string Route), HashSet<string>> branch,
        Dictionary<(string Kind, string Route), HashSet<string>> baseStore,
        Dictionary<(string Kind, string Route), EntryPointRef> epByKey,
        IReadOnlySet<string>? bodyChanged = null
    )
    {
        bodyChanged ??= new HashSet<string>(StringComparer.Ordinal);
        var deltas = new List<EpReachDelta>();
        foreach (var (key, branchSet) in branch)
        {
            if (!baseStore.TryGetValue(key, out var baseSet))
            {
                continue;
            }

            var added = branchSet.Where(s => !baseSet.Contains(s)).ToList();
            var removed = baseSet.Where(s => !branchSet.Contains(s)).ToList();

            // Phase 2 (in-place): reachable methods PRESENT IN BOTH stores whose body hash differs — a changed
            // constant/literal the structural set-diff can't see (the method stayed in the reach). Intersect
            // the body-changed set with the SHARED reach (branch ∩ base) so a genuinely added/removed method is
            // attributed by the structural diff above, not double-counted here.
            var inPlace =
                bodyChanged.Count == 0
                    ? []
                    : branchSet.Where(s => baseSet.Contains(s) && bodyChanged.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList();

            // An EP is affected if its reach STRUCTURE changed (added/removed) OR a reachable body changed in
            // place. With none of those, it's untouched.
            if (added.Count == 0 && removed.Count == 0 && inPlace.Count == 0)
            {
                continue;
            }

            // Collapse signature/overload churn: bucket by param-free stem so a ctor whose params moved reads
            // as ONE `~` change, not an add+remove pair. The magnitude that RANKS the list is the count of
            // DISTINCT meaningful stems (added ∪ removed ∪ changed) PLUS the in-place body-changed methods, so
            // a 30-overload swap counts as 1 (Task 2) and a pure in-place edit still has a non-zero magnitude.
            var b = BucketStems(added, removed);
            var distinctStemDelta = b.AddedStems.Count + b.RemovedStems.Count + b.ChangedStems.Count + inPlace.Count;
            var ep = epByKey.TryGetValue(key, out var r)
                ? r
                : new EntryPointRef(Kind: key.Kind, Route: key.Route, FilePath: "", Line: 0, Requires: null);
            deltas.Add(
                new EpReachDelta(
                    Kind: key.Kind,
                    Route: key.Route,
                    FilePath: ep.FilePath,
                    Line: ep.Line,
                    Requires: ep.Requires,
                    Added: b.Added,
                    Removed: b.Removed,
                    AddedStems: b.AddedStems,
                    RemovedStems: b.RemovedStems,
                    ChangedStems: b.ChangedStems,
                    DistinctStemDelta: distinctStemDelta,
                    InPlaceCount: inPlace.Count,
                    InPlace: inPlace
                )
            );
        }

        // Stable order: by distinct meaningful (stem) delta desc, then Kind, then Route (Task 2).
        return deltas
            .OrderByDescending(d => d.DistinctStemDelta)
            .ThenBy(d => d.Kind, StringComparer.Ordinal)
            .ThenBy(d => d.Route, StringComparer.Ordinal)
            .ToList();
    }

    // The set of symbol DocIDs whose declaration BODY changed base↔branch: a DocID whose hash differs between
    // the two hash maps, OR is present on exactly one side (added/removed declarations also count). Empty when
    // either store lacks the BodyHash fact (pre-fact store) — the in-place signal degrades silently then.
    internal static IReadOnlySet<string> BodyChangedSymbols(
        IReadOnlyDictionary<string, string> branchHashes,
        IReadOnlyDictionary<string, string> baseHashes
    )
    {
        // Either side empty => the fact is absent on at least one store; no reliable signal, skip silently.
        if (branchHashes.Count == 0 || baseHashes.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, hash) in branchHashes)
        {
            if (!baseHashes.TryGetValue(id, out var baseHash) || !string.Equals(hash, baseHash, StringComparison.Ordinal))
            {
                changed.Add(id);
            }
        }

        foreach (var id in baseHashes.Keys)
        {
            if (!branchHashes.ContainsKey(id))
            {
                changed.Add(id);
            }
        }

        return changed;
    }

    // Load the BASE store ONCE and produce, from that single load: the base per-EP REACHABLE SYMBOL SETS (for
    // the structural affected-EP diff), the base per-EP effect FOOTPRINTS (for the behavioral per-EP diff), and
    // the base body-hash map (for the in-place signal). The branch side reuses the graph/effects RunAsync
    // already built, so the whole impact run is 2 store loads total.
    private static async Task<(
        Dictionary<(string Kind, string Route), HashSet<string>> ReachSets,
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> Footprints,
        Dictionary<(string Kind, string Route), HashSet<HazardFinding>> Hazards,
        IReadOnlyDictionary<string, string> BodyHashes
    )> ComputeBaseSideAsync(string baseDbPath, RuleSet rules, FactPathFinder.TraversalMode mode, bool gate = true)
    {
        await using var context = new RigDbContext(baseDbPath, readOnly: true);
        var graph = await Reads.LoadShapedGraphAsync(context: context, rules: rules);

        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);
        var baseEps = epSet.Derived.Concat(epSet.PromotedOrigins).ToList();
        var idBySite = MethodIdBySite(methods);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        // Hazard delta: derive hazards on the base side too (mirror RunAsync / DeriveCommand) so the base
        // per-EP hazard set is computed over hazard-bearing effects and the diff compares like-for-like.
        // F8: one combined scan instead of two back-to-back single-kind queries (mirrors the HEAD side).
        var (staticFieldWriteRefs, staticFieldReadRefs) = await Reads.LoadStaticFieldAccessRefsByKindAsync(context);
        var threadStaticCells = await Reads.LoadThreadStaticFieldIdsAsync(context);
        var volatileCells = await Reads.LoadVolatileFieldIdsAsync(context);
        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs,
            staticFieldWriteRefs: staticFieldWriteRefs,
            staticFieldReadRefs: staticFieldReadRefs,
            deriveHazards: true,
            threadStaticCells: threadStaticCells,
            volatileCells: volatileCells,
            gate: gate
        );

        // Phase 3: union the base's field/property-access targets into its reach sets too, so the per-EP
        // structural diff compares like-for-like (degenerate `R:` nodes on BOTH sides). Built once per store.
        var baseRefTargets = RefTargetsByEnclosing(await Reads.LoadFieldAccessRefsAsync(context));
        var reachSets = ComputeReachSets(graph, baseEps, idBySite, mode, refsByEnclosing: baseRefTargets);
        var footprints = ComputeFootprints(graph, baseEps, idBySite, EffectKeysByEnclosing(effects), mode);
        var hazards = ComputeHazardSets(graph, baseEps, idBySite, HazardsByEnclosing(effects), mode);

        // Phase 2: the base body-hash map (guarded — empty on a pre-fact store), so RunAsync can diff it
        // against the branch's WITHOUT a second base load.
        var bodyHashes = await Reads.LoadSymbolBodyHashesAsync(context);

        return (reachSets, footprints, hazards, bodyHashes);
    }

    // Diff two stores' per-EP footprints: for every EP present in BOTH (paired on Kind+Route), the effects its
    // reach gained/lost (set membership) AND the effects that are AMPLIFIED — same key on both sides but now
    // produced MORE (higher reach multiplicity) or MOVED INTO A LOOP (Feature 1). Returns only EPs whose
    // footprint changed in EITHER way, busiest-delta first. EPs added/removed wholesale are the EP-diff
    // section's job, not this. Internal for unit-testing the pure diff (ImpactAmplificationTests).
    //
    // Hazard delta (additive): branchHazards/baseHazards (optional — null for the existing effect-only callers
    // and tests) are the per-EP reachable-hazard SETS. For each EP present in BOTH stores, the set-diff yields
    // HazardsAdded (head-only) and HazardsRemoved (base-only). An EP whose ONLY change is a hazard delta —
    // empty effect Added/Removed/Amplified — still surfaces in the result (so a pure hazard gain isn't missed).
    internal static IReadOnlyList<EpFootprintDelta> DiffFootprints(
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> branch,
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> baseStore,
        // (Kind, Route) -> the EP's site, so each delta carries FilePath/Line for FQN rendering. An EP missing
        // here (shouldn't happen — branch footprints are keyed off the same EPs) falls back to empty site.
        IReadOnlyDictionary<(string Kind, string Route), EntryPointRef> epByKey,
        Dictionary<(string Kind, string Route), HashSet<HazardFinding>>? branchHazards = null,
        Dictionary<(string Kind, string Route), HashSet<HazardFinding>>? baseHazards = null
    )
    {
        var deltas = new List<EpFootprintDelta>();
        foreach (var (key, branchReach) in branch)
        {
            if (!baseStore.TryGetValue(key, out var baseReach))
            {
                continue;
            }

            var added = branchReach.Keys.Where(k => !baseReach.ContainsKey(k)).OrderBy(k => k).ToList();
            var removed = baseReach.Keys.Where(k => !branchReach.ContainsKey(k)).OrderBy(k => k).ToList();

            // Amplification is a THIRD category over the INTERSECTION: a key present on BOTH sides whose
            // branch reach is produced MORE (BranchCount > BaseCount) and/or has newly entered a loop
            // (BranchInLoop && !BaseInLoop). A count DECREASE or LEAVING a loop is not flagged.
            var amplified = new List<EpEffectAmplified>();
            foreach (var (ek, br) in branchReach)
            {
                if (!baseReach.TryGetValue(ek, out var ba))
                {
                    continue; // added key — handled by the set-diff above, not amplification
                }

                var countUp = br.Count > ba.Count;
                var loopEntry = br.InLoop && !ba.InLoop;
                if (countUp || loopEntry)
                {
                    amplified.Add(
                        new EpEffectAmplified(
                            Provider: ek.Item1,
                            Operation: ek.Item2,
                            Resource: ek.Item3,
                            Enclosing: ek.Item4,
                            BaseCount: ba.Count,
                            BranchCount: br.Count,
                            BaseInLoop: ba.InLoop,
                            BranchInLoop: br.InLoop
                        )
                    );
                }
            }

            // Hazard delta (additive): the set-diff of this EP's reachable hazard findings, head-only = added,
            // base-only = removed. Empty when no hazard maps were supplied (the effect-only callers/tests) or
            // when the EP's hazard set is unchanged.
            var (hazardsAdded, hazardsRemoved) = DiffHazards(key, branchHazards, baseHazards);

            // An EP is listed when its effect footprint changed (set membership or amplification) OR a hazard
            // was gained/lost — so a PURE hazard gain (no effect-set change) still surfaces in PerEp.
            if (added.Count > 0 || removed.Count > 0 || amplified.Count > 0 || hazardsAdded.Count > 0 || hazardsRemoved.Count > 0)
            {
                var site = epByKey.GetValueOrDefault(key);
                // FR-1e: does the branch path still mutate shared state? (provider == shared_state — an
                // inherently-concurrent cell). Carried on the delta because an unchanged mutation is absent
                // from Added/Removed yet is what makes a co-occurring lock/guard delta race-relevant.
                var sharedMutationOnPath = branchReach.Keys.Any(k => string.Equals(k.Item1, "shared_state", StringComparison.Ordinal));
                deltas.Add(
                    new EpFootprintDelta(
                        Kind: key.Item1,
                        Route: key.Item2,
                        FilePath: site?.FilePath ?? "",
                        Line: site?.Line ?? 0,
                        BranchEffects: branchReach.Count,
                        BaseEffects: baseReach.Count,
                        Added: added,
                        Removed: removed,
                        Amplified: amplified
                            .OrderBy(a => a.Provider, StringComparer.Ordinal)
                            .ThenBy(a => a.Operation, StringComparer.Ordinal)
                            .ThenBy(a => a.Resource, StringComparer.Ordinal)
                            .ThenBy(a => a.Enclosing, StringComparer.Ordinal)
                            .ToList(),
                        SharedMutationOnPath: sharedMutationOnPath,
                        HazardsAdded: hazardsAdded,
                        HazardsRemoved: hazardsRemoved
                    )
                );
            }
        }

        return deltas
            .OrderByDescending(d =>
                d.Added.Count + d.Removed.Count + d.Amplified.Count + d.HazardsAddedOrEmpty.Count + d.HazardsRemovedOrEmpty.Count
            )
            .ThenBy(d => d.Route, StringComparer.Ordinal)
            .ToList();
    }

    // The per-EP hazard set-diff: head-only findings = added, base-only = removed, both ordered stably. Returns
    // empty lists when no hazard maps were supplied or the EP is absent on either side (so the diff degrades
    // silently on a pre-hazard store / the effect-only callers). Pure + ordered so the cache round-trip and the
    // unit tests see a deterministic list.
    private static (IReadOnlyList<HazardFinding> Added, IReadOnlyList<HazardFinding> Removed) DiffHazards(
        (string Kind, string Route) key,
        Dictionary<(string Kind, string Route), HashSet<HazardFinding>>? branchHazards,
        Dictionary<(string Kind, string Route), HashSet<HazardFinding>>? baseHazards
    )
    {
        if (branchHazards is null || baseHazards is null)
        {
            return ([], []);
        }

        var branchSet = branchHazards.GetValueOrDefault(key) ?? [];
        var baseSet = baseHazards.GetValueOrDefault(key) ?? [];
        if (branchSet.Count == 0 && baseSet.Count == 0)
        {
            return ([], []);
        }

        static List<HazardFinding> Order(IEnumerable<HazardFinding> hs) =>
            hs.OrderBy(h => h.Type, StringComparer.Ordinal)
                .ThenBy(h => h.Cell, StringComparer.Ordinal)
                .ThenBy(h => h.Enclosing, StringComparer.Ordinal)
                .ThenBy(h => h.Confidence, StringComparer.Ordinal)
                .ToList();

        var added = Order(branchSet.Where(h => !baseSet.Contains(h)));
        var removed = Order(baseSet.Where(h => !branchSet.Contains(h)));
        return (added, removed);
    }

    // Partition an EP's added/removed reachable DocIDs by param-free stem (StripParams). A stem present on
    // BOTH sides is a signature change (ChangedStems) and its raw ids drop out of Added/Removed — collapsing
    // the `- #ctor(old)` / `+ #ctor(new)` churn to one `~` line. A stem on one side only stays a genuine
    // add/remove; its raw ids are preserved (ordered) for tsv tooling. All three stem lists are ordered.
    internal static StemBuckets BucketStems(IReadOnlyList<string> added, IReadOnlyList<string> removed)
    {
        var addedStems = added.Select(StripParams).ToHashSet(StringComparer.Ordinal);
        var removedStems = removed.Select(StripParams).ToHashSet(StringComparer.Ordinal);
        var changedStems = addedStems.Where(removedStems.Contains).ToHashSet(StringComparer.Ordinal);

        var rawAdded = added.Where(id => !changedStems.Contains(StripParams(id))).OrderBy(s => s, StringComparer.Ordinal).ToList();
        var rawRemoved = removed.Where(id => !changedStems.Contains(StripParams(id))).OrderBy(s => s, StringComparer.Ordinal).ToList();
        return new StemBuckets(
            Added: rawAdded,
            Removed: rawRemoved,
            AddedStems: addedStems.Where(s => !changedStems.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            RemovedStems: removedStems.Where(s => !changedStems.Contains(s)).OrderBy(s => s, StringComparer.Ordinal).ToList(),
            ChangedStems: changedStems.OrderBy(s => s, StringComparer.Ordinal).ToList()
        );
    }

    // The `R:` prefix marks a DEGENERATE reach node: a field/property access TARGET (Phase 3), not a callable
    // method node. It keeps these distinct from method DocIDs in the reach set so the structural diff sees a
    // changed access, and StripParams leaves it intact (no `(`), so it reads as its own stem.
    internal const string RefNodePrefix = "R:";

    // Data-shape dominance threshold: an EP whose moved+changed members are at least this fraction data-shape
    // (fields/properties/accessors/ctors) is RecordShape — the few non-data-shape moves are incidental to the
    // same record change. Below it, real method churn is significant enough to warrant review (Other). 0.8 keeps
    // pure field-ripple and field-ripple-plus-a-moved-type in RecordShape while routing genuine refactors to
    // Other; validated against the live MR (see the migration's Master workflow EPs landing in Other).
    private const double DataShapeDominance = 0.8;

    // Classify ONE structural-only EP delta (effect set unchanged) into a cause bucket — pure + internal so the
    // bucketing is unit-testable without a store. "Data-shape" = a field/property-access node (`R:` prefix), a
    // property accessor (`.get_`/`.set_`), or a constructor (`.#ctor`) — all three are how a record's field
    // add/remove shows up in the reach graph. Classification is PROPORTIONAL (not all-or-nothing): record-shape
    // when data-shape dominates the moved+changed set, so a migration's field ripple isn't mislabeled "other"
    // just because one real method moved alongside it.
    internal static StructuralCause ClassifyStructuralCause(EpReachDelta d)
    {
        static bool IsDataShape(string stem) =>
            stem.StartsWith(RefNodePrefix, StringComparison.Ordinal)
            || stem.Contains(".get_", StringComparison.Ordinal)
            || stem.Contains(".set_", StringComparison.Ordinal)
            || stem.EndsWith(".#ctor", StringComparison.Ordinal);
        static bool IsCtor(string stem) => stem.EndsWith(".#ctor", StringComparison.Ordinal);

        // Every member that moved or changed signature — the population we classify over.
        var members = d.AddedStems.Concat(d.RemovedStems).Concat(d.ChangedStems).ToList();
        if (members.Count == 0)
        {
            // No structural move at all — the EP is affected only by an in-place body change (Phase 2).
            return d.InPlaceCount > 0 ? StructuralCause.InPlace : StructuralCause.Other;
        }

        // Purely constructor signatures (no fields/methods moved) — a record's ctor params changed and nothing
        // else. Called out separately from the field-add case since there's no new accessor, just a re-signing.
        if (members.All(IsCtor))
        {
            return StructuralCause.CtorSig;
        }

        var dataShape = members.Count(IsDataShape);
        return dataShape >= members.Count * DataShapeDominance ? StructuralCause.RecordShape : StructuralCause.Other;
    }
}
