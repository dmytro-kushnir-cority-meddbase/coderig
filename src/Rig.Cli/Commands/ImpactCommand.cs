using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.Caching;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Caching.QueryCacheKeys;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig impact --base <store> --head <store>` — a PURE two-store derived-facts diff. Both sides are REQUIRED
// indexed per-commit stores (a sha / short-sha / store-id matching an indexed `.rig/<id>/` store — NOT a git
// ref, NOT a working tree). It reports, per entry point, what the change DID, derived entirely from the two
// immutable stores:
//   (1) the ENTRY-POINT SET diff — EPs added/removed vs the base, paired on (Kind, Route);
//   (2) the BEHAVIORAL per-EP diff — entry points whose reachable EFFECT set changed (the high-signal handful);
//   (3) the STRUCTURAL per-EP diff — entry points whose reachable TREE changed (demoted to a cause-classified
//       one-liner by default; --structural expands it).
//
// There is NO git diff and NO speculative blast radius: the old git-working-tree seed (changed methods →
// reverse/forward reach) fed only the now-removed behavioral-delta section and the removed --reach blast
// radius. Every signal here is store-vs-store, so the output is the PROVEN diff between two indexed commits.
//
// This command is PURE ORCHESTRATION over the shipped engine — it adds no graph code. It runs `derive`'s
// DeriveEntryPoints/DeriveEffects on each store and diffs the per-EP forward-reach footprints.
internal static class ImpactCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        // Both sides are INDEXED STORE REFS (sha / short-sha / store-id matching a per-commit `.rig/<id>/`
        // store), resolved the same way every read command's --store is. --base-store / --head-store are
        // aliases for symmetry; they take the same store-ref form (the historical --base-store path/dir form
        // is gone — all four names resolve through ResolveReadStoreDir).
        var @base = new Option<string?>("--base", "--base-store")
        {
            Description = "BASE (before) side: an indexed commit store ref (sha / short-sha / store-id). Required.",
        };
        var head = new Option<string?>("--head", "--head-store")
        {
            Description = "HEAD (after) side: an indexed commit store ref (sha / short-sha / store-id). Required.",
        };
        var async = CommonOptions.Async();
        var includeDelivery = CommonOptions.IncludeDelivery();
        var rules = CommonOptions.Rules();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var noCache = CommonOptions.NoCache();
        var noGate = CommonOptions.NoGate();
        var structural = new Option<bool>("--structural")
        {
            Description =
                "Also list every entry point whose reachable TREE changed — including the (usually large) set affected "
                + "only by a data-shape ripple (a record gaining a field changes every reaching EP's reach without "
                + "changing its behavior). Off by default: the default output lists EPs whose EFFECT set changed (the "
                + "behavioral signal) plus a one-line structural-only summary. This expands that summary to the full list.",
        };
        // CI guardrail for behavior-preserving MRs (refactors / framework migrations): exit non-zero if ANY
        // entry point's reachable EFFECT set changed (the per-EP behavioral delta — the same count the header
        // reports as "N with a changed behavior"). Structural-only reachable-tree ripple does NOT trip it (a
        // data-shape change with no new/lost effect is exactly what a refactor is allowed to do). The diff is
        // formatting/rename-immune, so this gates on behavior, not text.
        var expectNoEffectChange = new Option<bool>("--expect-no-effect-change")
        {
            Description = "CI gate: exit 1 if any entry point's reachable effect set changed (for behavior-preserving MRs).",
        };
        var cmd = new Command(
            name: "impact",
            description: "Two-store diff: the entry-point + per-EP effect/reach changes between two indexed commits (--base <store> --head <store>)."
        )
        {
            @base,
            head,
            async,
            includeDelivery,
            rules,
            format,
            limit,
            noCache,
            noGate,
            structural,
            expectNoEffectChange,
        };
        // Both stores are mandatory — impact is a pure two-store diff, there is no working-tree/git fallback
        // and no LATEST default. Error clearly (before opening anything) if either ref is missing.
        cmd.Validators.Add(result =>
        {
            var hasBase = !string.IsNullOrWhiteSpace(result.GetValue(@base));
            var hasHead = !string.IsNullOrWhiteSpace(result.GetValue(head));
            if (!hasBase || !hasHead)
            {
                result.AddError(
                    "rig impact requires both --base <store> and --head <store> (indexed commit store refs: sha / short-sha / store-id)."
                );
            }
        });
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        new Options(
                            BaseRef: pr.GetValue(@base)!,
                            HeadRef: pr.GetValue(head)!,
                            Async: pr.GetValue(async),
                            IncludeDelivery: pr.GetValue(includeDelivery),
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Format: pr.GetValue(format),
                            Limit: pr.GetValue(limit),
                            NoCache: pr.GetValue(noCache),
                            Gate: !pr.GetValue(noGate),
                            Structural: pr.GetValue(structural),
                            ExpectNoEffectChange: pr.GetValue(expectNoEffectChange)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, null))
                    )
            )
        );
        return cmd;
    }

    // Bound option values for `rig impact`. Raw user inputs kept as parsed strings/values; derived locals
    // (tsv, max, mode) live at the top of RunAsync so cross-flag derivation stays in one place.
    private sealed record Options(
        string BaseRef,
        string HeadRef,
        bool Async,
        bool IncludeDelivery,
        IReadOnlyList<string> ExtraRules,
        string? Format,
        int? Limit,
        bool NoCache,
        bool Gate,
        bool Structural,
        bool ExpectNoEffectChange
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var max = opts.Limit ?? int.MaxValue;
        var mode = CommonOptions.Mode(async: opts.Async, includeDelivery: opts.IncludeDelivery); // --async => walk sound handoffs (delivery fan-out excluded unless --include-delivery)

        // The HEAD store: opened once here so the deployment-map read (render chrome) shares it with DiffAsync
        // (which also reads it for provenance + the cold derivation). Opening issues no query.
        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation with { StoreRef = opts.HeadRef });

        // F4: load the DeploymentMap ONCE (render-only — the --structural chips). Not part of the diff, so it
        // stays here rather than in DiffAsync (which the web endpoint also calls, without deployment chrome).
        var deployments = await LoadDeploymentsAsync(context, io.WorkspaceLocation.WorkingDirectory, io.TextOutput.Error);

        // The store-vs-store diff (warm-cached or freshly derived) — the SAME artifact the web /api/impact
        // returns, so `rig impact` and the web view cannot diverge.
        var art = await DiffAsync(
            headContext: context,
            ws: io.WorkspaceLocation,
            baseRef: opts.BaseRef,
            headRef: opts.HeadRef,
            mode: mode,
            gate: opts.Gate,
            noCache: opts.NoCache,
            extraRules: opts.ExtraRules
        );

        RenderImpact(
            output: io.TextOutput.Output,
            impactDiff: art.Diff,
            baseProv: art.BaseProvenance,
            headProv: art.HeadProvenance,
            mode: mode,
            deployments: deployments,
            fqnSites: art.FqnSites,
            tsv: tsv,
            structural: opts.Structural,
            max: max
        );
        return ExpectNoEffectChangeExit(opts.ExpectNoEffectChange, EffectChangedEpCount(art.Diff), io.TextOutput.Error);
    }

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

    // The `--expect-no-effect-change` CI gate. Behavioral change = an entry point present in BOTH commits whose
    // reachable EFFECT set changed (impactDiff.PerEp — the header's "N with a changed behavior"). Structural-only
    // reachable-tree ripple is NOT a behavioral change and never trips the gate. The verdict goes to STDERR so a
    // `--format tsv` run's stdout stays machine-clean; the exit code is the CI signal (1 = changed, 0 = clean).
    internal static int ExpectNoEffectChangeExit(bool expect, int behavioralEpCount, TextWriter error)
    {
        if (!expect)
        {
            return 0;
        }

        if (behavioralEpCount > 0)
        {
            error.WriteLine(
                $"--expect-no-effect-change FAILED: {behavioralEpCount} entry point(s) changed behavior (reachable effect set) — see the per-EP section. exit 1."
            );
            return 1;
        }

        error.WriteLine("--expect-no-effect-change OK: no entry point's effect set changed.");
        return 0;
    }

    // The count of EPs whose reachable EFFECT SET changed (added / removed / amplified) — the FR-4
    // behavioral count, and the headline "changed behavior" number. PerEp ALSO contains EPs whose only
    // delta is a HAZARD gain/loss (race_window / n+1 / …) so they surface in the per-EP section; those must
    // NOT count here. --expect-no-effect-change is a deterministic effect-set gate — gating CI on a
    // (often heuristic) hazard belongs to a separate opt-in (e.g. a future --expect-no-hazard-gain), not
    // this flag. So a behavior-preserving refactor that merely trips a hazard heuristic stays green here.
    internal static int EffectChangedEpCount(ImpactDiff diff) =>
        diff.PerEp.Count(d => d.Added.Count > 0 || d.Removed.Count > 0 || d.Amplified.Count > 0);

    // The render of a computed impact diff — shared by the cold (just-computed) and warm (cache-replayed)
    // paths so a hit is BYTE-IDENTICAL to a recompute. A pure function of the diff + provenance + deployments
    // + the FQN site map + the presentation flags: tsv emits the typed rows, else the human sections. FqnForCard
    // only ever looks up the diff's own sites, so the warm path's site SUBSET serves it exactly as the full map.
    private static void RenderImpact(
        TextWriter output,
        ImpactDiff impactDiff,
        StoreProvenance baseProv,
        StoreProvenance headProv,
        FactPathFinder.TraversalMode mode,
        DeploymentMap deployments,
        Dictionary<(string, int), string> fqnSites,
        bool tsv,
        bool structural,
        int max
    )
    {
        if (tsv)
        {
            EmitTsv(output, impactDiff, fqnSites, max);
            return;
        }

        WriteHeader(output, baseProv, headProv, mode, impactDiff);
        WriteEpDiffHuman(output, baseProv, impactDiff.Ep, max);
        // PRIMARY signal: the entry points whose reachable EFFECT set changed (the behavioral handful). Always
        // shown — this is the "what actually does something different" answer.
        WritePerEpHuman(output, baseProv, impactDiff.PerEp, fqnSites, max);
        // The structural reachable-tree diff is mostly data-shape ripple (a record field add lights up every
        // reaching EP). By default we DEMOTE it to a one-line, cause-classified breadcrumb so a no-net-new-effect
        // migration still can't hide; --structural expands it to the full per-EP list.
        if (structural)
        {
            WriteAffected(output, baseProv, impactDiff, deployments, fqnSites, max);
        }
        else
        {
            WriteStructuralBreadcrumb(output, baseProv, impactDiff, impactDiff.PerEp);
        }
    }

    // The source-control provenance of a store, condensed for the header: a short commit (12-char sha) +
    // branch when the store carries them, else a fallback label (the store-ref the user passed). Label is
    // ALWAYS non-empty so the header can name which side it is even on a pre-stamping store.
    internal sealed record StoreProvenance(string? Branch, string? ShortCommit, string Fallback)
    {
        // The header label for this side: "<branch> (<short>)" when both are known, "<branch>" / "(<short>)"
        // when only one is, else the fallback store-ref. The diff-summary uses the same short form.
        public string Label =>
            (Branch, ShortCommit) switch
            {
                ({ Length: > 0 } b, { Length: > 0 } c) => $"{b} ({c})",
                ({ Length: > 0 } b, _) => b,
                (_, { Length: > 0 } c) => $"({c})",
                _ => Fallback,
            };

        // The compact label the diff-summary line leads with: the short commit when known, else the branch,
        // else the fallback store-ref.
        public string ShortLabel =>
            ShortCommit is { Length: > 0 } c ? c
            : Branch is { Length: > 0 } b ? b
            : Fallback;
    }

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

    // A derived entry point at a source site, with its deployment requirements — the unit the impact output
    // lists and groups. Kind = action/http/page/…; Route = display route; Requires = deployment gates.
    internal sealed record EntryPointRef(string Kind, string Route, string FilePath, int Line, IReadOnlyList<string>? Requires);

    // The PROVEN store-vs-store diff: the entry-point set diff, the entry points whose reachable EFFECT set
    // changed (PerEp — the behavioral signal), and the entry points whose reachable TREE changed (AffectedEps —
    // structural). All three are derived purely from the two indexed stores.
    internal sealed record ImpactDiff(EpDiff? Ep, IReadOnlyList<EpReachDelta> AffectedEps, IReadOnlyList<EpFootprintDelta> PerEp);

    internal sealed record EpDiff(IReadOnlyList<(string Kind, string Route)> Added, IReadOnlyList<(string Kind, string Route)> Removed);

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

    private static void EmitEpDiffTsv(TextWriter output, EpDiff? diff)
    {
        if (diff is null)
        {
            return;
        }

        foreach (var (kind, route) in diff.Added)
        {
            output.WriteLine($"ep_added\t{kind}\t{route}");
        }

        foreach (var (kind, route) in diff.Removed)
        {
            output.WriteLine($"ep_removed\t{kind}\t{route}");
        }
    }

    private static void WriteEpDiffHuman(TextWriter output, StoreProvenance baseProv, EpDiff? diff, int max)
    {
        output.WriteLine();
        if (diff is null)
        {
            return;
        }

        output.WriteLine($"Entry-point diff vs '{baseProv.ShortLabel}': +{diff.Added.Count} added, -{diff.Removed.Count} removed");
        foreach (var (kind, route) in diff.Added.Take(max))
        {
            output.WriteLine($"{Indent.L1}+ {kind} {route}");
        }

        foreach (var (kind, route) in diff.Removed.Take(max))
        {
            output.WriteLine($"{Indent.L1}- {kind} {route}");
        }
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

    // The reach MULTIPLICITY + loop context of one effect key from one EP (Feature 1). Count is the number
    // of distinct reachable effect-bearing enclosing nodes that produce this key (a derivable proxy for "how
    // many times" — a higher count means the effect is produced from more reachable sites). InLoop is true
    // when ANY of those producing nodes is reached under an enclosing loop somewhere on its call path (the
    // BFS-shortest-path NearestLoopKind, the same loop context the tree's 🔁 uses). This is the per-key
    // cardinality + loop flag the bare reachable-SET dedup throws away.
    internal sealed record EffectReach(int Count, bool InLoop);

    // One effect that is AMPLIFIED on an EP (Feature 1): its key is present on BOTH stores (so the set-diff
    // says "unchanged"), but it is now produced MORE (BranchCount > BaseCount) and/or has MOVED INTO A LOOP
    // (BranchInLoop && !BaseInLoop). A FLAG FOR REVIEW, not a verdict — the static signal can't distinguish a
    // harmless hot-cache 2nd read from a real extra cold DB call, so the rendering says "review".
    internal sealed record EpEffectAmplified(
        string Provider,
        string Operation,
        string Resource,
        string Enclosing,
        int BaseCount,
        int BranchCount,
        bool BaseInLoop,
        bool BranchInLoop
    );

    // One entry point whose reachable-effect FOOTPRINT differs between the two stores, with the per-EP
    // added/removed effects (set membership) AND the amplified effects (Feature 1 — same key, produced more
    // or now in a loop). EffectKey = (provider, operation, resource, param-free enclosing method). An EP is
    // listed when Added/Removed OR Amplified is non-empty (the behavioral set = set-changed ∪ amplified).
    internal sealed record EpFootprintDelta(
        string Kind,
        string Route,
        // The EP's source site — carried so the card can render the FQN (FqnForCard), same as the structural
        // list. Empty/0 when the EP's site is unknown (then the card shows the route).
        string FilePath,
        int Line,
        int BranchEffects,
        int BaseEffects,
        IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> Added,
        IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> Removed,
        IReadOnlyList<EpEffectAmplified> Amplified,
        // FR-1e: true when the BRANCH reach still carries a `shared_state` effect (a mutation of an
        // inherently-shared cell — ConcurrentDictionary/Atom/ImmutableInterlocked/static-field-write). This
        // is the bit the Added/Removed lists can't carry: a shared mutation present on BOTH sides is absent
        // from the set-diff, yet it's exactly what makes a lock/guard ADD or REMOVE on this EP race-relevant.
        // Defaults false so existing constructions/tests are unaffected.
        bool SharedMutationOnPath = false,
        // HAZARD DELTA: the hazard findings (race_window / lazy_init_race / n_plus_1 / unserializable_payload —
        // see HazardKinds) this EP's reach GAINED (HazardsAdded — head-only) or LOST (HazardsRemoved —
        // base-only) between the two stores. A hazard finding is keyed on (Type, Cell, param-free Enclosing)
        // and carries its confidence tier. This is the hazard mirror of the effect Added/Removed lists — a
        // refactor that opened a race_window on a path, or a fix that closed one. Defaulted empty so existing
        // constructions/tests (and OLD cache blobs) are unaffected.
        IReadOnlyList<HazardFinding>? HazardsAdded = null,
        IReadOnlyList<HazardFinding>? HazardsRemoved = null
    )
    {
        // Normalized non-null views so callers (ordering, rendering, the cache codec) never NRE on the
        // defaulted-null hazard lists (a delta with no hazard change, or an OLD cache blob / effect-only test).
        public IReadOnlyList<HazardFinding> HazardsAddedOrEmpty => HazardsAdded ?? [];
        public IReadOnlyList<HazardFinding> HazardsRemovedOrEmpty => HazardsRemoved ?? [];
    }

    // One hazard finding on an EP's reach: a hazard observation (race_window / lazy_init_race / n_plus_1 /
    // unserializable_payload) on a reachable effect. Keyed on (Type, Cell, param-free Enclosing) so it diffs
    // line/signature-insensitively, exactly as the effect key does — Cell is the observation's Context (the
    // shared cell for race_window, the loop identifier for n_plus_1, …), Enclosing the param-free producing
    // method. Confidence is the disclosed tier (high/medium/low); it rides along for rendering but is DELIBERATELY
    // EXCLUDED from equality/hash (overridden below) so the diff identity is exactly (Type, Cell, Enclosing) — a
    // confidence change on the SAME finding is not a gain/loss, and the set-union/Distinct dedup keys on identity.
    internal sealed record HazardFinding(string Type, string Cell, string Enclosing, string Confidence)
    {
        public bool Equals(HazardFinding? other) =>
            other is not null
            && string.Equals(Type, other.Type, StringComparison.Ordinal)
            && string.Equals(Cell, other.Cell, StringComparison.Ordinal)
            && string.Equals(Enclosing, other.Enclosing, StringComparison.Ordinal);

        public override int GetHashCode() => HashCode.Combine(Type, Cell, Enclosing);
    }

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

    // One entry point whose REACHABLE SYMBOL SET (its full forward-reach tree, structural — not effects)
    // differs between the two stores. This is the line-number-insensitive "two trees, diffed" signal: an EP
    // is affected iff what it reaches changed — a new/removed/renamed method anywhere in its reach (incl. the
    // obj→sql kind of migration the effect-set diff collapses).
    //
    // The symbol moves are bucketed by PARAM-FREE STEM (StripParams), so a signature/overload change reads as
    // ONE change, not an add+remove pair: AddedStems = stems only in the added set, RemovedStems = stems only
    // in the removed set, ChangedStems = stems present on BOTH sides (a signature change — e.g. a ctor whose
    // params moved). Added/Removed keep the RAW first-party method DocIDs that belong to a genuinely added /
    // removed stem (NOT the signature-changed ones) so tooling (tsv) still has the exact ids; ChangedStems
    // carries the param-free stems for the `~` rows. DistinctStemDelta is the dedup'd magnitude (added ∪
    // removed ∪ changed stems) that ranks the list (Task 2) — a 30-overload swap counts as 1, not 30.
    //
    // InPlace (Phase 2) is the orthogonal IN-PLACE signal: reachable method DocIDs whose declaration BODY hash
    // differs base↔branch even though they stayed in the reach set (a changed constant/literal — no call-
    // structure move). An EP can be affected by ONLY this (empty stem buckets, non-empty InPlace) — e.g. a
    // reachable method's body changed but nothing was added/removed/re-signed. InPlaceCount is the full count;
    // InPlace carries a few sample DocIDs for display.
    internal sealed record EpReachDelta(
        string Kind,
        string Route,
        string FilePath,
        int Line,
        IReadOnlyList<string>? Requires,
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> AddedStems,
        IReadOnlyList<string> RemovedStems,
        IReadOnlyList<string> ChangedStems,
        int DistinctStemDelta,
        int InPlaceCount = 0,
        IReadOnlyList<string>? InPlace = null
    );

    // The stem-bucketed partition of one EP's added/removed reachable DocID sets: a symbol present in BOTH
    // sets under the same param-free stem is a SIGNATURE CHANGE (Changed), not an add+remove. Added/Removed
    // keep only the raw DocIDs whose stem is genuinely one-sided; AddedStems/RemovedStems/ChangedStems are the
    // param-free stems for display + counting. Pure + internal so the bucketing is unit-testable in isolation.
    internal sealed record StemBuckets(
        IReadOnlyList<string> Added,
        IReadOnlyList<string> Removed,
        IReadOnlyList<string> AddedStems,
        IReadOnlyList<string> RemovedStems,
        IReadOnlyList<string> ChangedStems
    );

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
    private const string RefNodePrefix = "R:";

    // Display label for a reach node: a `R:`-prefixed degenerate field/property-access node (Phase 3) renders
    // as its short member name tagged `(field/prop access)`; an ordinary method DocID renders via ShortName.
    private static string ReachNodeLabel(string node) =>
        node.StartsWith(RefNodePrefix, StringComparison.Ordinal)
            ? $"{ShortName(node[RefNodePrefix.Length..])} (field/prop access)"
            : ShortName(node);

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

    private static void EmitPerEpTsv(TextWriter output, IReadOnlyList<EpFootprintDelta> deltas, Dictionary<(string, int), string> fqnSites)
    {
        foreach (var d in deltas)
        {
            var fqn = FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
            output.WriteLine(
                $"ep_delta\t{d.Kind}\t{d.Route}\t{fqn}\t{d.BranchEffects}\t{d.BaseEffects}\t+{d.Added.Count}\t-{d.Removed.Count}\t~{d.Amplified.Count}"
            );
            foreach (var (provider, operation, resource, enclosing) in d.Added)
            {
                output.WriteLine($"ep_effect_added\t{d.Kind}\t{d.Route}\t{provider}\t{operation}\t{resource}\t{enclosing}");
            }

            foreach (var (provider, operation, resource, enclosing) in d.Removed)
            {
                output.WriteLine($"ep_effect_removed\t{d.Kind}\t{d.Route}\t{provider}\t{operation}\t{resource}\t{enclosing}");
            }

            foreach (var a in d.Amplified)
            {
                output.WriteLine(
                    $"ep_effect_amplified\t{d.Kind}\t{d.Route}\t{a.Provider}\t{a.Operation}\t{a.Resource}\t{a.Enclosing}\t{a.BaseCount}\t{a.BranchCount}\t{a.BaseInLoop}\t{a.BranchInLoop}"
                );
            }

            // FR-1e: a guard (lock/async_lock) added/removed on a path that still mutates shared state.
            //  ep_guard_delta  <kind>  <route>  <+guards comma-joined>  <-guards comma-joined>
            if (HasGuardDeltaOnSharedMutation(d))
            {
                var (gAdded, gRemoved) = GuardEffectDelta(d);
                output.WriteLine($"ep_guard_delta\t{d.Kind}\t{d.Route}\t{string.Join(',', gAdded)}\t{string.Join(',', gRemoved)}");
            }

            // HAZARD DELTA: one row per hazard finding GAINED / LOST on this EP's reach.
            //  ep_hazard_added / ep_hazard_removed  <kind>  <route>  <type>  <confidence>  <cell>  <enclosing>
            foreach (var h in d.HazardsAddedOrEmpty)
            {
                output.WriteLine($"ep_hazard_added\t{d.Kind}\t{d.Route}\t{h.Type}\t{h.Confidence}\t{h.Cell}\t{h.Enclosing}");
            }

            foreach (var h in d.HazardsRemovedOrEmpty)
            {
                output.WriteLine($"ep_hazard_removed\t{d.Kind}\t{d.Route}\t{h.Type}\t{h.Confidence}\t{h.Cell}\t{h.Enclosing}");
            }
        }
    }

    // PRIMARY section: the entry points whose reachable EFFECT set changed — the behavioral signal. This is the
    // small, high-information set (a handful), as opposed to the structural reachable-tree diff which is mostly
    // data-shape ripple.
    private static void WritePerEpHuman(
        TextWriter output,
        StoreProvenance baseProv,
        IReadOnlyList<EpFootprintDelta> deltas,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        output.WriteLine();
        if (deltas.Count == 0)
        {
            output.WriteLine(
                $"Behavioral changes per entry point vs '{baseProv.ShortLabel}': none — no entry point's reachable-effect set changed."
            );
            return;
        }

        // The behavioral set = (effect-set changed) ∪ (amplified) — an EP whose set is stable but has an
        // amplified effect (now produced more / in a loop) is in `deltas` too (DiffFootprints lists it).
        output.WriteLine(
            $"Behavioral changes per entry point vs '{baseProv.ShortLabel}' (reachable-effect set changed or effect amplified): {deltas.Count}"
        );
        foreach (var d in deltas.Take(max))
        {
            // Render the FQN (round-trips into `rig tree`), same as the structural list; falls back to the route.
            var label = FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
            var ampPart = d.Amplified.Count > 0 ? $", ~{d.Amplified.Count} amplified" : "";
            var hazAdded = d.HazardsAddedOrEmpty;
            var hazRemoved = d.HazardsRemovedOrEmpty;
            var hazPart = hazAdded.Count > 0 || hazRemoved.Count > 0 ? $", hazards +{hazAdded.Count}/-{hazRemoved.Count}" : "";
            output.WriteLine(
                $"{Indent.L2}{d.Kind} {label}  (effects {d.BaseEffects}→{d.BranchEffects}, +{d.Added.Count}/-{d.Removed.Count}{ampPart}{hazPart})"
            );
            foreach (var (provider, operation, resource, enclosing) in d.Added.Take(max))
            {
                output.WriteLine($"{Indent.L3}+ {provider} {operation}{Resource(resource)}  ({enclosing})");
            }

            foreach (var (provider, operation, resource, enclosing) in d.Removed.Take(max))
            {
                output.WriteLine($"{Indent.L3}- {provider} {operation}{Resource(resource)}  ({enclosing})");
            }

            // Amplified effects: same key on both sides, but produced MORE or now in a loop. Marked `~` and
            // worded for REVIEW (not "regression") — the static signal can't tell a hot-cache re-read from a
            // real extra cold call. Note: a harmless ×1->×2 will show; that's the chosen tradeoff.
            foreach (var a in d.Amplified.Take(max))
            {
                output.WriteLine(
                    $"{Indent.L3}~ {a.Provider} {a.Operation}{Resource(a.Resource)}  ({AmplifyNote(a)})  ({a.Enclosing})  [review]"
                );
            }

            // FR-1e: a lock/guard was added or removed on a path that STILL mutates shared state — the
            // concurrency protection around an inherently-shared cell changed. High-signal: this is the
            // exact shape of the lock-guarded-class race (a guard lost, or a fix that adds one). Flagged for
            // review, not a verdict.
            if (HasGuardDeltaOnSharedMutation(d))
            {
                var (gAdded, gRemoved) = GuardEffectDelta(d);
                var moves = new List<string>();
                moves.AddRange(gAdded.Select(g => $"+{g}"));
                moves.AddRange(gRemoved.Select(g => $"-{g}"));
                output.WriteLine(
                    $"{Indent.L3}⚠ guard delta on a shared-mutation path: {string.Join(" ", moves)}  (shared_state mutation still reachable)  [review]"
                );
            }

            // HAZARD DELTA: the hazard findings (race_window / lazy_init_race / n_plus_1 /
            // unserializable_payload) this EP's reach GAINED (+) or LOST (-) — a refactor that opened a race on
            // this path, or a fix that closed one. Shown with the confidence tier + the cell/context, mirroring
            // the effect +/- lines. Flagged for review, not a verdict.
            foreach (var h in hazAdded.Take(max))
            {
                output.WriteLine($"{Indent.L3}+ hazard {h.Type} ({h.Confidence}){Cell(h.Cell)}  ({h.Enclosing})  [review]");
            }

            foreach (var h in hazRemoved.Take(max))
            {
                output.WriteLine($"{Indent.L3}- hazard {h.Type} ({h.Confidence}){Cell(h.Cell)}  ({h.Enclosing})");
            }
        }

        static string Resource(string resource) => string.IsNullOrEmpty(resource) ? "" : $" {resource}";
        static string Cell(string cell) => string.IsNullOrEmpty(cell) ? "" : $" {cell}";
    }

    // The amplification annotation: the count move (×base -> ×branch) and/or a loop-entry note, both when
    // both fired. Worded as an observation, not a verdict — it pairs with the `[review]` tag in the line.
    private static string AmplifyNote(EpEffectAmplified a)
    {
        var parts = new List<string>();
        if (a.BranchCount > a.BaseCount)
        {
            parts.Add($"×{a.BaseCount} -> ×{a.BranchCount}");
        }

        if (a.BranchInLoop && !a.BaseInLoop)
        {
            parts.Add("now in loop");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "amplified";
    }

    // The header: the one-line PROVEN-diff takeaway, then which two commits/branches were compared. Both sides
    // are indexed commits — there is no working tree.
    private static void WriteHeader(
        TextWriter output,
        StoreProvenance baseProv,
        StoreProvenance headProv,
        FactPathFinder.TraversalMode mode,
        ImpactDiff diff
    )
    {
        var asyncNote = mode switch
        {
            FactPathFinder.TraversalMode.AsyncExact => "  (--async: handoffs included; delivery fan-out excluded)",
            FactPathFinder.TraversalMode.AsyncInclude => "  (--async --include-delivery: delivery fan-out included)",
            _ => "",
        };

        output.WriteLine(DiffSummary(baseProv, diff));
        output.WriteLine();
        output.WriteLine($"Impact: {baseProv.Label}  ->  {headProv.Label}{asyncNote}");
    }

    // The one-line takeaway: the PROVEN change vs the base store — entry points added/removed, entry points
    // whose behavior (reachable-effect set) changed, and entry points whose reachable tree changed.
    private static string DiffSummary(StoreProvenance baseProv, ImpactDiff diff)
    {
        // The "changed behavior" headline counts EFFECT-set changes only; hazard-only EPs are reported by
        // hazardNote below (PerEp includes them, but they aren't an effect-set change — no double-count).
        var behavioralEps = EffectChangedEpCount(diff);
        var added = diff.Ep?.Added.Count ?? 0;
        var removed = diff.Ep?.Removed.Count ?? 0;
        // FR-1e: count the EPs whose guard (lock/atomic) around a still-reachable shared mutation changed.
        // Only appended when non-zero so the common (no-guard-change) summary line stays unchanged.
        var guardEps = diff.PerEp.Count(HasGuardDeltaOnSharedMutation);
        var guardNote = guardEps > 0 ? $" ⚠ {guardEps} with a guard delta on a shared-mutation path." : "";
        // Hazard delta: count the EPs that GAINED or LOST a hazard finding (race_window / n+1 / …). Appended
        // only when non-zero so the common (no-hazard-change) summary line stays unchanged.
        var hazardEps = diff.PerEp.Count(d => d.HazardsAddedOrEmpty.Count > 0 || d.HazardsRemovedOrEmpty.Count > 0);
        var hazardNote = hazardEps > 0 ? $" ⚠ {hazardEps} with a hazard delta." : "";
        return $"Diff vs '{baseProv.ShortLabel}': {PlusMinus(added: added, removed: removed)} entry point(s)"
            + $"; {behavioralEps} entry point(s) with a changed behavior, {diff.AffectedEps.Count} with a changed reachable tree.{guardNote}{hazardNote}";
    }

    private static string PlusMinus(int added, int removed) => $"+{added}/-{removed}";

    // Why an entry point's reachable TREE changed, when its EFFECT set did NOT — the cause buckets for the
    // structural-only breadcrumb. RecordShape: the moved members are DOMINATED by data-shape changes — a record
    // gained/lost a field, so every reaching EP sees the new field accessors + the ctor signature move. This is
    // the dominant noise, and it dominates even when a handful of real methods moved alongside (e.g. a deleted
    // settings type's deserializer), because the CAUSE is still the one data-shape change. CtorSig: the move is
    // purely constructor signatures (the data-shape change seen only at the ctor). InPlace: a reachable body
    // changed with no structural move. Other: real method-level reach churn is a MEANINGFUL fraction — these are
    // the genuine migration/refactor sites a reviewer should look at (a migration can move reach with no net-new
    // effect kind, so it lands here, not in the noise).
    internal enum StructuralCause
    {
        RecordShape,
        CtorSig,
        InPlace,
        Other,
    }

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

    // The DEMOTED structural view (default): one line stating how many EPs have a changed reachable tree but NO
    // behavioral (effect-set) change, broken down by cause so a data-shape ripple reads as exactly that — and a
    // no-net-new-effect migration still surfaces as a non-zero `other` count that can't hide. `--structural`
    // expands this to the full per-EP list (WriteAffected). EPs whose effect set DID change are already shown by
    // WritePerEpHuman, so they're excluded here (the two sections partition the affected set, no double-count).
    private static void WriteStructuralBreadcrumb(
        TextWriter output,
        StoreProvenance baseProv,
        ImpactDiff diff,
        IReadOnlyList<EpFootprintDelta> behavioralDeltas
    )
    {
        output.WriteLine();
        var behavioralKeys = behavioralDeltas.Select(d => (d.Kind, d.Route)).ToHashSet();
        var structuralOnly = diff.AffectedEps.Where(d => !behavioralKeys.Contains((d.Kind, d.Route))).ToList();
        if (structuralOnly.Count == 0)
        {
            output.WriteLine($"Structural-only reachable-tree changes vs '{baseProv.ShortLabel}': none.");
            return;
        }

        var byCause = structuralOnly.GroupBy(ClassifyStructuralCause).ToDictionary(g => g.Key, g => g.Count());
        int N(StructuralCause c) => byCause.GetValueOrDefault(c);
        var parts = new List<string>();
        if (N(StructuralCause.RecordShape) > 0)
        {
            parts.Add($"{N(StructuralCause.RecordShape)} record-shape (reach a changed field/property)");
        }

        if (N(StructuralCause.CtorSig) > 0)
        {
            parts.Add($"{N(StructuralCause.CtorSig)} ctor-signature");
        }

        if (N(StructuralCause.InPlace) > 0)
        {
            parts.Add($"{N(StructuralCause.InPlace)} in-place body change");
        }

        if (N(StructuralCause.Other) > 0)
        {
            parts.Add($"{N(StructuralCause.Other)} other method-level churn");
        }

        output.WriteLine(
            $"Structural-only reachable-tree changes vs '{baseProv.ShortLabel}' (no behavioral effect change): {structuralOnly.Count} entry point(s)"
        );
        output.WriteLine($"{Indent.L1}{string.Join(", ", parts)}");
        // The `other` bucket is the one that can hide a real migration (method churn with no NET-new effect kind),
        // so call it out explicitly when present — that's the line a reviewer should not skip.
        if (N(StructuralCause.Other) > 0)
        {
            output.WriteLine(
                $"{Indent.L1}↳ {N(StructuralCause.Other)} are method-level churn — review these (a migration can change reach without a net-new effect kind)."
            );
        }

        output.WriteLine($"{Indent.L1}--structural to list them all (or --format tsv).");
    }

    // The affected entry points, computed STRUCTURALLY: each EP whose full reachable symbol set differs
    // base↔branch ("two trees, diffed"), grouped by kind with deployment chips and the per-EP +added/-removed
    // reachable methods. Independent of effect classification — catches the obj→sql kind of migration the
    // effect-set diff collapses, and excludes false positives whose reach didn't actually move.
    private static void WriteAffected(
        TextWriter output,
        StoreProvenance baseProv,
        ImpactDiff diff,
        DeploymentMap deployments,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        output.WriteLine();
        output.WriteLine($"Affected entry points (reachable tree changed) vs '{baseProv.ShortLabel}': {diff.AffectedEps.Count}");
        if (diff.AffectedEps.Count == 0)
        {
            output.WriteLine($"{Indent.L1}none — no entry point's reachable structure changed.");
            return;
        }

        foreach (var kindGroup in diff.AffectedEps.GroupBy(d => d.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var d in kindGroup.Take(max / 4 + 1))
            {
                // +added/-removed/~changed counted by DISTINCT STEM, so a 30-overload ctor swap reads as ~1,
                // not +30/-30. The `~` lines are signature changes (same stem on both sides); the +/- lines
                // are genuine reach gains/losses, labelled by ShortName (added/removed raw DocIDs, deduped to
                // their stem for display so an overload set doesn't print N near-identical lines).
                // The in-place suffix (Phase 2) flags an EP affected by a reachable method's BODY change with no
                // structural reach move — so an EP with empty +/-/~ but a changed constant still reads as why.
                var inPlaceNote = d.InPlaceCount > 0 ? $", in-place: {d.InPlaceCount} reached method body(ies) changed" : "";
                // Label the card with the FQN (round-trips into `rig tree`); fall back to the path route when the
                // EP site maps to no indexed method symbol. The diff still keys on (Kind, Route) internally.
                var label = FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
                var route = $"{label}  (+{d.AddedStems.Count}/-{d.RemovedStems.Count}/~{d.ChangedStems.Count} reachable{inPlaceNote})";
                WriteEntryPointLine(output, deployments, route: route, filePath: d.FilePath, line: d.Line, requires: d.Requires);
                foreach (var s in d.AddedStems.Take(3))
                {
                    output.WriteLine($"{Indent.L3}+ {ReachNodeLabel(s)}");
                }

                foreach (var s in d.RemovedStems.Take(3))
                {
                    output.WriteLine($"{Indent.L3}- {ReachNodeLabel(s)}");
                }

                foreach (var s in d.ChangedStems.Take(3))
                {
                    output.WriteLine($"{Indent.L3}~ {ShortName(s)} (signature changed)");
                }

                foreach (var s in (d.InPlace ?? []).Take(3))
                {
                    output.WriteLine($"{Indent.L3}≈ {ShortName(s)} (body changed in place)");
                }
            }

            if (kindGroup.Count() > max / 4 + 1)
            {
                output.WriteLine($"{Indent.L3}… +{kindGroup.Count() - (max / 4 + 1)} more (raise --limit, or --format tsv for all)");
            }
        }
    }

    private static void EmitTsv(TextWriter output, ImpactDiff diff, Dictionary<(string, int), string> fqnSites, int max)
    {
        // One stream of typed rows for CI/tooling. First column is the row kind. Every row here is the
        // STORE-vs-STORE derived-facts diff: the EP set diff + the per-EP footprint/reach diff between the two
        // indexed commits. There is NO git working-tree diff and NO speculative reverse-reach blast radius — the
        // old `changed` / `effect_added` / `effect_removed` / `obs_*` rows and the `entrypoint` / `effect`
        // (reverse-reach) rows are gone; read the per-EP rows (ep_delta / ep_effect_*) for the same effects,
        // attributed and correct.
        //  affected_ep  <kind>  <route>  <fqn>  <cause>  <file>  <line>  <+addedStems>  <-removedStems>  <~changedStems>  <inplace>   (proven; <route> is the path-style diff key, <fqn> the dotted name `rig tree` matches — equals <route> when unresolved; <cause> is behavioral|record-shape|ctor-sig|in-place|other — behavioral = effect set changed, the rest are structural-only; counts are DISTINCT param-free stems; inplace = reachable bodies changed)
        //  structural_summary  <total>  <behavioral>  <record-shape>  <ctor-sig>  <in-place>  <other>   (one row: the cause breakdown of the affected-EP set — behavioral counts the EPs whose effect set changed, the rest are structural-only)
        //  ep_reach_+   <kind>  <route>  <symbolId>                            (newly in the EP's reach — raw method DocID, or an `R:`-prefixed field/property-access target, Phase 3)
        //  ep_reach_-   <kind>  <route>  <symbolId>                            (dropped from the EP's reach — raw method DocID, or an `R:`-prefixed field/property-access target, Phase 3)
        //  ep_reach_~   <kind>  <route>  <stem>                                (a reachable method whose SIGNATURE changed — param-free stem)
        //  ep_reach_inplace  <kind>  <route>  <symbolId>                       (a reachable method whose BODY changed in place — raw DocID, Phase 2)
        //  ep_added     <kind>  <route>                                        (an entry point present only on the HEAD store)
        //  ep_removed   <kind>  <route>                                        (an entry point present only on the BASE store)
        //  ep_delta     <kind>  <route>  <fqn>  <branchEffects>  <baseEffects>  <+added>  <-removed>  <~amplified>   (one per EP whose reachable-effect footprint changed: set membership and/or amplification; counts are effect KEYS)
        //  ep_effect_added    <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>   (an effect KEY newly in the EP's footprint)
        //  ep_effect_removed  <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>   (an effect KEY dropped from the EP's footprint)
        //  ep_effect_amplified  <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>  <baseCount>  <branchCount>  <baseInLoop>  <branchInLoop>   (Feature 1: SAME key on both stores but produced MORE — branchCount>baseCount — and/or MOVED INTO A LOOP — branchInLoop && !baseInLoop. count = # distinct reachable effect-bearing producing nodes. A REVIEW flag, not a verdict: can't tell a hot-cache re-read from a real extra cold call.)
        //  ep_guard_delta  <kind>  <route>  <+guards>  <-guards>   (FR-1e: a lock/async_lock acquire/release ADDED (+, comma-joined provider:operation) or REMOVED (-) on a path whose branch reach STILL carries a shared_state mutation — the concurrency guard around an inherently-shared cell changed. A REVIEW flag covering both the lost-guard race and the guard-adding fix.)
        //  ep_hazard_added    <kind>  <route>  <type>  <confidence>  <cell>  <enclosing>   (a hazard finding — race_window / lazy_init_race / n_plus_1 / unserializable_payload, see HazardKinds — newly present on the EP's reach: a refactor opened it. cell = the observation Context, enclosing = the param-free producing method. A REVIEW flag, not a verdict.)
        //  ep_hazard_removed  <kind>  <route>  <type>  <confidence>  <cell>  <enclosing>   (a hazard finding that DROPPED from the EP's reach base->head: a fix closed it. Same columns as ep_hazard_added.)

        // Cause per EP: behavioral when its effect set changed (it's in PerEp), else the structural sub-cause.
        var behavioralKeys = diff.PerEp.Select(d => (d.Kind, d.Route)).ToHashSet();
        string CauseTag(EpReachDelta e) =>
            behavioralKeys.Contains((e.Kind, e.Route))
                ? "behavioral"
                : ClassifyStructuralCause(e) switch
                {
                    StructuralCause.RecordShape => "record-shape",
                    StructuralCause.CtorSig => "ctor-sig",
                    StructuralCause.InPlace => "in-place",
                    _ => "other",
                };

        // structural_summary: the cause breakdown of the WHOLE affected set (not capped by --limit) so tooling
        // gets the true totals even when the per-EP rows below are truncated.
        var causeCounts = diff
            .AffectedEps.GroupBy(CauseTag, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int CC(string k) => causeCounts.GetValueOrDefault(k);
        output.WriteLine(
            $"structural_summary\t{diff.AffectedEps.Count}\t{CC("behavioral")}\t{CC("record-shape")}\t{CC("ctor-sig")}\t{CC("in-place")}\t{CC("other")}"
        );

        foreach (var e in diff.AffectedEps.Take(max))
        {
            var fqn = FqnForCard(route: e.Route, filePath: e.FilePath, line: e.Line, idBySite: fqnSites);
            output.WriteLine(
                $"affected_ep\t{e.Kind}\t{e.Route}\t{fqn}\t{CauseTag(e)}\t{e.FilePath}\t{e.Line}\t+{e.AddedStems.Count}\t-{e.RemovedStems.Count}\t~{e.ChangedStems.Count}\t{e.InPlaceCount}"
            );
            foreach (var s in e.Added)
            {
                output.WriteLine($"ep_reach_+\t{e.Kind}\t{e.Route}\t{s}");
            }

            foreach (var s in e.Removed)
            {
                output.WriteLine($"ep_reach_-\t{e.Kind}\t{e.Route}\t{s}");
            }

            foreach (var s in e.ChangedStems)
            {
                output.WriteLine($"ep_reach_~\t{e.Kind}\t{e.Route}\t{s}");
            }

            foreach (var s in e.InPlace ?? [])
            {
                output.WriteLine($"ep_reach_inplace\t{e.Kind}\t{e.Route}\t{s}");
            }
        }

        EmitEpDiffTsv(output, diff.Ep);
        EmitPerEpTsv(output, diff.PerEp, fqnSites);
    }
}
