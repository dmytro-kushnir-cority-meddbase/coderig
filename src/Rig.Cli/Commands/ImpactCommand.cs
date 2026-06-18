using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig impact [--base <ref>]` — git-diff-aware blast radius. Point it at a branch/diff and it reports what
// the change affects: (1) the CHANGED SET — every method declared in a changed `.cs` file (v1 is
// FILE-GRANULAR; line-range precision needs a method end-line fact, deferred); (2) the AFFECTED ENTRY
// POINTS that reverse-reach the changed set, grouped by deployed service (what redeploys / is at risk);
// (3) the EFFECTS in the forward reach of the changed set, with the risky observations surfaced + a
// one-line risk headline.
//
// This command is PURE ORCHESTRATION over the shipped engine — it adds no graph code. It is essentially
// `callers --entrypoints` seeded from a diff instead of one pattern (FactPathFinder.ReachedByAny is the
// multi-source twin of the ReachedBy that `callers` uses), plus `reaches`'s effect intersection done
// multi-source via ReachableFromAll, plus `derive`'s DeriveEffects/DeploymentMap exactly as they run there.
internal static class ImpactCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var @base = new Option<string?>("--base") { Description = "Git ref to diff against (default: main)." };
        var repo = new Option<string?>("--repo")
        {
            Description = "Source repo path to run `git diff` in (default: the indexed run's source repo).",
        };
        var baseStore = new Option<string?>("--base-store")
        {
            Description =
                "Base commit's .rig store (path or dir) for the entry-point diff; default: resolve --base to an indexed commit store.",
        };
        // --head-store is an alias for symmetry with --base-store: on the HEAD side there's no git-ref step
        // (the head is just "which indexed store is primary"), so one option serves both names. Pass a sha /
        // short-sha / store-id; default (omitted) = the LATEST-indexed store, as before.
        var head = new Option<string?>("--head", "--head-store")
        {
            Description =
                "Head (after) side of the diff: an indexed commit sha / store-id whose store is the PRIMARY "
                + "(alias --head-store, mirroring --base-store). Default (omitted): the LATEST-indexed store, as "
                + "before. Pass this to diff two explicit commits (`--head <shaA> --base <shaB>`) and avoid "
                + "depending on which commit was mined last.",
        };
        var async = CommonOptions.Async();
        var rules = CommonOptions.Rules();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var structural = new Option<bool>("--structural")
        {
            Description =
                "Also list every entry point whose reachable TREE changed — including the (usually large) set affected "
                + "only by a data-shape ripple (a record gaining a field changes every reaching EP's reach without "
                + "changing its behavior). Off by default: the default output lists EPs whose EFFECT set changed (the "
                + "behavioral signal) plus a one-line structural-only summary. This expands that summary to the full list.",
        };
        var reach = new Option<bool>("--reach")
        {
            Description =
                "Also show the SPECULATIVE blast radius: every entry point that reverse-reaches the change and every "
                + "effect in its forward reach. Off by default — in a large codebase this reach is pessimistic (a few "
                + "changed methods touch most of the graph); the default output is the PROVEN diff vs the base store.",
        };
        var cmd = new Command(
            name: "impact",
            description: "What a git diff changes: entry-point + effect diff vs the base commit (--reach adds the speculative blast radius)."
        )
        {
            @base,
            repo,
            baseStore,
            head,
            async,
            rules,
            format,
            limit,
            structural,
            reach,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        baseRef: pr.GetValue(@base) ?? "main",
                        repoOverride: pr.GetValue(repo),
                        baseStoreOverride: pr.GetValue(baseStore),
                        headOverride: pr.GetValue(head),
                        async: pr.GetValue(async),
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        format: pr.GetValue(format),
                        limit: pr.GetValue(limit),
                        structural: pr.GetValue(structural),
                        reach: pr.GetValue(reach),
                        output: output,
                        error: error,
                        workingDirectory: workingDirectory
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        string baseRef,
        string? repoOverride,
        string? baseStoreOverride,
        string? headOverride,
        bool async,
        IReadOnlyList<string> extraRules,
        string? format,
        int? limit,
        bool structural,
        bool reach,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var max = limit ?? int.MaxValue;
        var mode = CommonOptions.Mode(async); // --async => walk handoff edges (reverse + forward), else sync-cut
        // One rule load for the whole run — rules are working-dir-scoped, so the SAME set serves the branch
        // store and the base store; threaded into every helper below.
        var rules = RuleSet.Load(workingDirectory, extraRules);

        // The PRIMARY (head/after) store: an explicit --head sha/store-id, else the LATEST-indexed store
        // (the historical default). OpenReadContext resolves null => LATEST, a ref => that per-commit store
        // (throws StoreRefNotFoundException → CommandGuard lists what's indexed if --head doesn't match).
        await using var context = OpenReadContext(workingDirectory: workingDirectory, storeRef: headOverride);

        // The source repo to run `git diff` in: the indexed run's source repo (a SEPARATE tree from the
        // store/cwd — e.g. cwd is meddbase-analysis, source is meddbase-main-application), overridable via
        // --repo. SourceProjectPath is the `rig index --from` csproj; its repo top-level is the diff root.
        var runs = await Reads.ListRunsAsync(context);
        var primary = runs.OrderByDescending(r => r.SymbolCount).FirstOrDefault();
        var repoPath = repoOverride ?? RepoHintFromRun(primary);
        if (repoPath is null)
        {
            error.WriteLine("Could not determine the source repo to diff (no indexed run, or no source path). Pass --repo <path>.");
            return 1;
        }

        var repoRoot = GitTopLevel(repoPath);
        if (repoRoot is null)
        {
            error.WriteLine($"'{repoPath}' is not inside a git work tree (or git is unavailable). Pass --repo <path>.");
            return 1;
        }

        // The changed `.cs` files: committed-vs-base (base...HEAD) UNIONED with the working-tree changes
        // (staged + unstaged), so an in-progress branch is covered before commit. Paths are repo-root-
        // relative POSIX paths (git's default); resolved to absolute below to join the indexed FilePath.
        // CommittedRanges additionally carries each committed file's changed NEW-side line ranges (from
        // `git diff --unified=0`) so the blast radius can narrow from file- to symbol-granular below.
        var diff = ChangedCsFileDiff(repoRoot, baseRef, error);
        if (diff.Files.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine($"No changed .cs files between '{baseRef}' and the working tree of {repoRoot}.");
            }

            return 0;
        }

        // File -> declared method symbols. LoadDeadCodeMethodsAsync is every method symbol with its
        // absolute definition FilePath; LoadMethodEndLinesAsync adds each method's END line, so a method's
        // source extent [Line, EndLine] can be overlapped against the diff's changed line ranges. The
        // blast radius is now SYMBOL-GRANULAR where we can prove it (committed hunks confined to method
        // bodies), and falls back to FILE-GRANULAR otherwise — see SelectChangedMethods for the gates.
        // (EndLines is empty on a store indexed before the EndLine fact existed => fully file-granular.)
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        var endLineById = await Reads.LoadMethodEndLinesAsync(context);
        // Phase 2: the branch's per-symbol declaration body hashes (guarded — empty on a pre-fact store). Diffed
        // against the base's (loaded once in ComputeBaseSideAsync) to find in-place body edits the reach-set
        // diff misses. Loaded here so the branch context is read once.
        var branchBodyHashes = await Reads.LoadSymbolBodyHashesAsync(context);

        // Normalize both sides to compare paths: indexed FilePath is absolute with OS separators; the diff
        // files are repo-root-relative POSIX. Build the absolute form of each changed file, normalized.
        var changedAbs = diff
            .Files.Select(f => NormalizeRepoRelative(repoRoot: repoRoot, repoRelativePosix: f))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Precise line ranges are trusted ONLY for committed-and-clean files: the index reflects the source
        // on disk at index time, the committed `base...HEAD` new-side line numbers match HEAD, and the
        // canonical `impact` flow (CI: clean checkout of the PR tip, then index) makes those line up. A file
        // with WORKING-TREE edits is dropped from the precise set (its uncommitted lines have shifted vs the
        // indexed coordinates) and falls back to file-granular — pessimistic, never under-reports.
        var preciseRangesByFileNorm = new Dictionary<string, IReadOnlyList<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (relPath, ranges) in diff.CommittedRanges)
        {
            if (!diff.DirtyFiles.Contains(relPath))
            {
                preciseRangesByFileNorm[NormalizeRepoRelative(repoRoot: repoRoot, repoRelativePosix: relPath)] = ranges;
            }
        }

        var changedSet = SelectChangedMethods(methods, endLineById, changedAbs, preciseRangesByFileNorm);
        var changedMethods = changedSet.Methods;
        var changedIds = changedMethods.Select(m => m.SymbolId).ToHashSet(StringComparer.Ordinal);
        if (changedIds.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine(
                    $"{diff.Files.Count} changed .cs file(s), but none map to an indexed method symbol "
                        + "(file outside the indexed solution, or no methods declared). Nothing to trace."
                );
            }

            return 0;
        }

        // One whole-store graph drives BOTH directions (the changed set is scattered, so the bounded
        // single-pattern SQL subgraph the other commands use doesn't apply). Same load + ShapeGraph the
        // EF-fallback path of every traversal command uses, so impact walks the IDENTICAL shaped graph.
        var graph = await Reads.LoadFactGraphAsync(context, rules.Handoff);
        graph = FactPathFinder.ShapeGraph(graph, rules.Factory, rules.Cut, rules.Context);
        graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));

        var deployments = await LoadDeploymentsAsync(context, workingDirectory, error);

        // --- (2) Affected entry points: reverse-reach the changed set, intersect the rule-detected EP set.
        // ReachedByAny is the multi-source twin of `callers`' ReachedBy — the union of everything that can
        // reach ANY changed method. The EP intersection is the SAME (FilePath, Line) join `callers
        // --entrypoints` uses: a derived EP "touches" the change when its declaration site is reverse-
        // reachable from a changed method.
        var reachedBy = FactPathFinder.ReachedByAny(graph, changedIds, mode: mode);
        var reachableSites = methods.Where(m => reachedBy.ContainsKey(m.SymbolId)).Select(m => (m.FilePath, m.Line)).ToHashSet();

        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);
        var derivedEps = epSet.Derived;
        var promoted = epSet.PromotedOrigins;
        var affectedEps = EntryPointsAtSites(derived: derivedEps, promoted: promoted, sites: reachableSites);

        // --- (3) Effects in the forward reach: everything reachable FROM the changed set (multi-source,
        // exact ids — ReachableFromAll is the engine's multi-source forward traversal), intersected with
        // the whole-store derived effects by enclosing symbol. This is `reaches`' effect intersection,
        // done from a set of seeds instead of one pattern.
        var forward = FactPathFinder.ReachableFromAll(graph, changedIds, mode: mode);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs
        );
        var affectedEffects = effects.Where(e => e.EnclosingSymbolId is not null && forward.Contains(e.EnclosingSymbolId)).ToList();

        // Risky observations across the affected effects (the lenses the task names): n+1 / looped_effect,
        // lost-update/TOCTOU / read_before_commit, parallel_fanout, plus throw (an effect the change can
        // raise) and cross-service (an affected EP spans >1 service). These drive the risk headline.
        var observationCounts = affectedEffects
            .SelectMany(e => e.Observations ?? [])
            .GroupBy(o => o.Type, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var affectedServices = AffectedServices(affectedEps, deployments);

        // --- Two-store entry-point diff (step 3): EPs added/removed vs the base commit's store, paired on
        // (Kind, Route) — line/param-free, so formatting + signature edits don't churn the diff. Requires
        // the base commit to have its own indexed store. Null when no base store resolves (the blast-radius
        // output above still stands). See docs/design-impact-behavioral-diff.md §3.1-3.2.
        var branchEps = derivedEps.Concat(promoted).ToList();
        var baseDbPath = ResolveBaseDbPath(workingDirectory, baseStoreOverride, repoRoot, baseRef);
        var epDiff = baseDbPath is null ? null : await ComputeEpDiffAsync(baseDbPath, branchEps, rules);

        // --- (4) Behavioral delta (the actual two-store diff): the effects/observations reachable FROM the
        // changed methods, branch vs base. Seeding the base by Type.Method (param-free) so a signature-changed
        // method still matches its pre-change self; effect identity drops file/line/params, so formatting and
        // signature edits don't churn — only genuine behavior moves. This is "what did the change DO":
        // +effect = newly reachable (e.g. a new DB write), -effect = no longer reachable (e.g. the retired
        // object_store read), +observation = newly introduced risk (e.g. became an n+1). See §3.3.
        // --- (4) The two-store diff. The behavioral delta is the change-level effect/observation move (what
        // the change DOES). The AFFECTED ENTRY POINTS are computed STRUCTURALLY: per EP, diff its full
        // reachable symbol set branch vs base ("two trees, diffed") — an EP is affected iff WHAT IT REACHES
        // changed, regardless of whether an effect rule fired. This catches the obj→sql kind of migration the
        // effect-set diff collapses (same key, different symbols), and excludes false positives like a
        // file-granular sibling edit that doesn't change an EP's reach. The effect-level per-EP footprint diff
        // (now the primary view) rides along. The base store is loaded ONCE for all; the branch reuses the graph.
        BehavioralDelta? behavioral = null;
        IReadOnlyList<EpReachDelta> affectedEntryPoints = [];
        IReadOnlyList<EpFootprintDelta>? perEpDeltas = null;
        if (baseDbPath is not null)
        {
            var idBySite = MethodIdBySite(methods);
            // Phase 3: the branch's enclosing→field/property-access-targets lookup, built ONCE so ComputeReachSets
            // can union each reachable method's read/write targets as degenerate `R:` nodes at O(reach) cost.
            var branchRefTargets = RefTargetsByEnclosing(await Reads.LoadFieldAccessRefsAsync(context));
            var branchReachSets = ComputeReachSets(graph, branchEps, idBySite, mode, refsByEnclosing: branchRefTargets);
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

            var baseSide = await ComputeBaseSideAsync(
                baseDbPath: baseDbPath,
                branchEffects: affectedEffects,
                branchReach: forward.Count,
                changedMethods: changedMethods,
                rules: rules,
                mode: mode,
                // Footprints (per-EP effect-set deltas) are now the PRIMARY signal — the handful of EPs whose
                // BEHAVIOR changed, as opposed to the thousands whose reachable tree shifted only because a
                // record gained a field. Always computed; it's the same forward-reach pass ComputeReachSets
                // already runs, just unioning effect keys instead of symbol ids, so the extra cost is marginal.
                needFootprints: true
            );
            behavioral = baseSide.Delta;

            // Phase 2: the symbols whose declaration BODY changed base↔branch (differing/one-sided hash). An EP
            // whose reach intersects this set is affected IN-PLACE even when its structural reach-set diff is
            // empty. Both maps empty (pre-fact store on either side) => BodyChangedSymbols returns empty and the
            // signal degrades silently. branchBodyHashes is loaded once from the branch context above.
            var bodyChanged = BodyChangedSymbols(branchHashes: branchBodyHashes, baseHashes: baseSide.BodyHashes);
            affectedEntryPoints = DiffReachSets(
                branch: branchReachSets,
                baseStore: baseSide.ReachSets,
                epByKey: epByKey,
                bodyChanged: bodyChanged
            );

            var branchFootprints = ComputeFootprints(graph, branchEps, idBySite, EffectKeysByEnclosing(effects), mode);
            perEpDeltas = DiffFootprints(branch: branchFootprints, baseStore: baseSide.Footprints!, epByKey: epByKey);
        }

        var change = new ChangeSummary(
            Methods: changedMethods.Count,
            Files: diff.Files.Count,
            PreciseFiles: changedSet.PreciseFileCount,
            FileGranularFiles: changedSet.FileGranularFileCount
        );
        var impactDiff = new ImpactDiff(Ep: epDiff, Behavioral: behavioral, AffectedEps: affectedEntryPoints, PerEp: perEpDeltas);
        var blast = new BlastRadius(
            ReachedByCount: reachedBy.Count,
            AffectedEps: affectedEps,
            ForwardCount: forward.Count,
            Effects: affectedEffects,
            Observations: observationCounts,
            Services: affectedServices
        );

        // (FilePath, Line) -> method DocID, in-RAM (no store I/O) — lets the affected-EP card render each EP's
        // fully-qualified dotted name (FqnForCard), which round-trips into `rig tree`, instead of the path route.
        var fqnSites = MethodIdBySite(methods);

        if (tsv)
        {
            EmitTsv(output, changedMethods, impactDiff, blast, deployments, fqnSites, max);
            return 0;
        }

        WriteHeader(output, baseRef, repoRoot, mode, change, impactDiff);
        WriteEpDiffHuman(output, baseRef, baseDbPath, epDiff, StoreLayout.AvailableStoreIds(workingDirectory), max);
        WriteBehavioralDeltaHuman(output, baseRef, behavioral, max);
        // PRIMARY signal: the entry points whose reachable EFFECT set changed (the behavioral handful). Always
        // shown — this is the "what actually does something different" answer.
        WritePerEpHuman(output, baseRef, baseDbPath, perEpDeltas, fqnSites, max);
        // The structural reachable-tree diff is mostly data-shape ripple (a record field add lights up every
        // reaching EP). By default we DEMOTE it to a one-line, cause-classified breadcrumb so a no-net-new-effect
        // migration still can't hide; --structural expands it to the full per-EP list (the old default).
        if (structural)
        {
            WriteAffected(output, baseRef, impactDiff, deployments, fqnSites, max);
        }
        else
        {
            WriteStructuralBreadcrumb(output, baseRef, impactDiff, perEpDeltas);
        }

        // The SPECULATIVE blast radius — every entry point reverse-reachable from the change, every effect in
        // its forward reach — is opt-in (--reach). In a large codebase a handful of changed methods reverse-
        // reach most of the entry-point set, so it's pessimistic; the diff above is what actually CHANGED.
        if (reach)
        {
            WriteReach(output, blast, deployments, max);
        }
        else
        {
            output.WriteLine();
            output.WriteLine(
                $"Blast radius (speculative) hidden: {blast.AffectedEps.Count} entry point(s) reverse-reach the change, "
                    + $"{blast.Effects.Count} effect(s) in forward reach. Re-run with --reach to list them (or --format tsv)."
            );
        }
        return 0;
    }

    // A derived entry point at a source site, with its deployment requirements — the unit the impact output
    // lists and groups. Kind = action/http/page/…; Route = display route; Requires = deployment gates.
    internal sealed record EntryPointRef(string Kind, string Route, string FilePath, int Line, IReadOnlyList<string>? Requires);

    // The change set + how precisely each changed file mapped to methods (the header's "Changed:" line).
    internal sealed record ChangeSummary(int Methods, int Files, int PreciseFiles, int FileGranularFiles);

    // The PROVEN diff vs the base store: the entry-point set diff, the behavioral effect/observation delta,
    // the entry points that actually reach a changed effect, and the per-EP footprint deltas (computed by
    // default — the primary view). Ep and Behavioral are null when no base store resolved (nothing to show).
    internal sealed record ImpactDiff(
        EpDiff? Ep,
        BehavioralDelta? Behavioral,
        IReadOnlyList<EpReachDelta> AffectedEps,
        IReadOnlyList<EpFootprintDelta>? PerEp
    );

    // The SPECULATIVE blast radius (--reach): the reverse-reach entry points + the forward-reach effects of
    // the WHOLE changed set. Pessimistic in a large codebase, so it's opt-in.
    internal sealed record BlastRadius(
        int ReachedByCount,
        IReadOnlyList<EntryPointRef> AffectedEps,
        int ForwardCount,
        IReadOnlyList<DerivedEffect> Effects,
        IReadOnlyDictionary<string, int> Observations,
        IReadOnlyList<string> Services
    );

    // The db path of the base store to diff entry points against: an explicit --base-store (a db path or a
    // store dir), else the --base ref resolved to a commit sha and matched to an indexed per-commit store.
    // Null when none resolves (the EP diff is then skipped — the blast radius still renders).
    private static string? ResolveBaseDbPath(string workingDirectory, string? baseStoreOverride, string repoRoot, string baseRef)
    {
        if (baseStoreOverride is { Length: > 0 } explicitStore)
        {
            var path = Directory.Exists(explicitStore) ? Path.Combine(explicitStore, StoreLayout.DbFileName) : explicitStore;
            return File.Exists(path) ? path : null;
        }

        var baseSha = ResolveRefToSha(repoRoot: repoRoot, reference: baseRef) ?? baseRef;
        var dir = StoreLayout.ResolveStoreDirByRef(workingDirectory: workingDirectory, refOrId: baseSha);
        return dir is null ? null : Path.Combine(dir, StoreLayout.DbFileName);
    }

    // `git rev-parse --verify <ref>^{commit}` — peel a ref/tag to its commit sha, null when it doesn't resolve.
    private static string? ResolveRefToSha(string repoRoot, string reference)
    {
        var (ok, stdout, _) = RunGit(repoRoot, "rev-parse", "--verify", "--quiet", reference + "^{commit}");
        var sha = stdout.Trim();
        return ok && sha.Length > 0 ? sha : null;
    }

    internal sealed record EpDiff(IReadOnlyList<(string Kind, string Route)> Added, IReadOnlyList<(string Kind, string Route)> Removed);

    // Derive entry points on the base store and set-diff them against the branch's, keyed on (Kind, Route).
    // DeriveEntryPointsAsync derives straight from the passed context with rules loaded from the (shared)
    // working dir — no query cache — so running it on a second store is correct. Internal for testing.
    internal static async Task<EpDiff> ComputeEpDiffAsync(string baseDbPath, IReadOnlyList<DerivedEntryPoint> branchEps, RuleSet rules)
    {
        await using var baseContext = new Rig.Storage.Storage.RigDbContext(baseDbPath, readOnly: true);
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

    private static void WriteEpDiffHuman(
        TextWriter output,
        string baseRef,
        string? baseDbPath,
        EpDiff? diff,
        IReadOnlyList<string> availableStoreIds,
        int max
    )
    {
        output.WriteLine();
        if (diff is null || baseDbPath is null)
        {
            output.WriteLine($"Entry-point diff vs '{baseRef}': base store not indexed — skipped.");
            output.WriteLine(
                availableStoreIds.Count > 0
                    ? $"{Indent.L1}indexed commits: {string.Join(", ", availableStoreIds)}  (index the base commit, or pass --base-store <path>)"
                    : $"{Indent.L1}(no per-commit stores yet — index the base commit to enable the entry-point diff)"
            );
            return;
        }

        output.WriteLine($"Entry-point diff vs '{baseRef}': +{diff.Added.Count} added, -{diff.Removed.Count} removed");
        foreach (var (kind, route) in diff.Added.Take(max))
        {
            output.WriteLine($"{Indent.L1}+ {kind} {route}");
        }

        foreach (var (kind, route) in diff.Removed.Take(max))
        {
            output.WriteLine($"{Indent.L1}- {kind} {route}");
        }
    }

    // An effect identity that survives formatting AND signature edits: provider/op/resource + the enclosing
    // method WITHOUT its parameter list. So `SetCompanySettings(int,X)` and `SetCompanySettings(int,X,ITxn)`
    // collapse to the same site, and a reformatted body doesn't move anything — only a genuine change to
    // which effects are reachable shows up in the diff.
    internal sealed record BehavioralDelta(
        int ReachBranch,
        int ReachBase,
        IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> AddedEffects,
        IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> RemovedEffects,
        IReadOnlyList<(string Type, string Provider, string Operation, string Enclosing)> AddedObservations,
        IReadOnlyList<(string Type, string Provider, string Operation, string Enclosing)> RemovedObservations
    );

    // Strip a DocID's parameter list (and leading `M:`) to a param-free `Namespace.Type.Method` key.
    internal static string StripParams(string? docId)
    {
        if (string.IsNullOrEmpty(docId))
        {
            return "";
        }

        var body = docId.StartsWith("M:", StringComparison.Ordinal) ? docId[2..] : docId;
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        return paren >= 0 ? body[..paren] : body;
    }

    // The copy-pasteable label for an EP card: the method's fully-qualified dotted name (namespace.Type.Member),
    // resolved from the EP's (FilePath, Line) against the in-RAM method index (no extra store I/O — idBySite is
    // already built for reach computation). This is the exact suffix `rig tree <from>` matches on, so a card
    // label round-trips straight into a tree query. Falls back to the path-style Route when the site maps to no
    // indexed method symbol (synthesized/promoted handoff EPs, lambdas) — those keep their derived route.
    // Internal for testing — the route↔FQN resolution is the contract behind "the card always shows a dotted
    // name when the site resolves, else the route".
    internal static string FqnForCard(string route, string filePath, int line, Dictionary<(string, int), string> idBySite) =>
        !string.IsNullOrEmpty(filePath) && idBySite.TryGetValue((filePath, line), out var docId) ? StripParams(docId) : route;

    // The reachable-from-seeds effect set + reach size for a single store — the same load/shape/forward-reach/
    // derive the branch path runs inline, so both stores are measured identically.
    internal static async Task<(IReadOnlyList<DerivedEffect> Effects, int ReachCount)> ReachEffectsAsync(
        Rig.Storage.Storage.RigDbContext context,
        IReadOnlySet<string> seedIds,
        RuleSet rules,
        FactPathFinder.TraversalMode mode
    )
    {
        var graph = await Reads.LoadFactGraphAsync(context, rules.Handoff);
        graph = FactPathFinder.ShapeGraph(graph, rules.Factory, rules.Cut, rules.Context);
        graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));

        var forward = FactPathFinder.ReachableFromAll(graph, seedIds, mode: mode);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs
        );
        var reached = effects.Where(e => e.EnclosingSymbolId is not null && forward.Contains(e.EnclosingSymbolId)).ToList();
        return (reached, forward.Count);
    }

    internal static async Task<BehavioralDelta> ComputeBehavioralDeltaAsync(
        string baseDbPath,
        IReadOnlyList<DerivedEffect> branchEffects,
        int branchReach,
        IReadOnlyList<DeadCodeFinder.MethodMeta> changedMethods,
        RuleSet rules,
        FactPathFinder.TraversalMode mode
    )
    {
        await using var baseContext = new Rig.Storage.Storage.RigDbContext(baseDbPath, readOnly: true);

        // Seed the base by param-free method identity, so a signature-changed method seeds its PRE-change
        // self in the base (whose DocID differs) — else its downstream effects would falsely read as "added".
        var changedStems = changedMethods.Select(m => StripParams(m.SymbolId)).ToHashSet(StringComparer.Ordinal);
        var baseMethods = await Reads.LoadDeadCodeMethodsAsync(baseContext);
        var baseSeed = baseMethods
            .Where(m => changedStems.Contains(StripParams(m.SymbolId)))
            .Select(m => m.SymbolId)
            .ToHashSet(StringComparer.Ordinal);

        var (baseEffects, baseReach) = await ReachEffectsAsync(baseContext, baseSeed, rules, mode);
        return DiffBehavioral(branchEffects, baseEffects, branchReach, baseReach);
    }

    // The pure change-level set-diff: branch vs base effect/observation sets, keyed param-free so formatting
    // and signature edits don't churn. Shared by ComputeBehavioralDeltaAsync (loads base itself, used by the
    // tests + the non-`--per-ep` path) and ComputeBehavioralAndFootprintsAsync (loads base ONCE for both the
    // delta and per-EP footprints).
    private static BehavioralDelta DiffBehavioral(
        IReadOnlyList<DerivedEffect> branchEffects,
        IReadOnlyList<DerivedEffect> baseEffects,
        int branchReach,
        int baseReach
    )
    {
        (string, string, string, string) EffectKey(DerivedEffect e) =>
            (e.Provider, e.Operation, e.ResourceType, StripParams(e.EnclosingSymbolId));
        var branchEffectKeys = branchEffects.Select(EffectKey).ToHashSet();
        var baseEffectKeys = baseEffects.Select(EffectKey).ToHashSet();

        var addedEffects = branchEffectKeys.Where(k => !baseEffectKeys.Contains(k)).OrderBy(k => k).ToList();
        var removedEffects = baseEffectKeys.Where(k => !branchEffectKeys.Contains(k)).OrderBy(k => k).ToList();

        IEnumerable<(string Type, string Provider, string Operation, string Enclosing)> Observations(IEnumerable<DerivedEffect> effects) =>
            effects.SelectMany(e =>
                (e.Observations ?? []).Select(o => (o.Type, e.Provider, e.Operation, StripParams(e.EnclosingSymbolId)))
            );
        var branchObs = Observations(branchEffects).ToHashSet();
        var baseObs = Observations(baseEffects).ToHashSet();

        var addedObs = branchObs.Where(k => !baseObs.Contains(k)).OrderBy(k => k).ToList();
        var removedObs = baseObs.Where(k => !branchObs.Contains(k)).OrderBy(k => k).ToList();

        return new BehavioralDelta(
            ReachBranch: branchReach,
            ReachBase: baseReach,
            AddedEffects: addedEffects,
            RemovedEffects: removedEffects,
            AddedObservations: addedObs,
            RemovedObservations: removedObs
        );
    }

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
        IReadOnlyList<EpEffectAmplified> Amplified
    );

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
        var reached = FactPathFinder.ReachesInfoFromEachSeed(graph, seedIds, mode: mode);

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
        var reached = FactPathFinder.ReachesFromEachSeed(graph, seedIds, mode: mode);

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

    // Load the BASE store ONCE and produce, from that single load: the change-level behavioral delta, the base
    // per-EP REACHABLE SYMBOL SETS (for the structural affected-EP diff — always), and the base per-EP effect
    // footprints (only when --per-ep needs them). The branch side reuses the graph/effects RunAsync already
    // built, so the whole impact run is 2 store loads total.
    private static async Task<(
        BehavioralDelta Delta,
        Dictionary<(string Kind, string Route), HashSet<string>> ReachSets,
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>>? Footprints,
        IReadOnlyDictionary<string, string> BodyHashes
    )> ComputeBaseSideAsync(
        string baseDbPath,
        IReadOnlyList<DerivedEffect> branchEffects,
        int branchReach,
        IReadOnlyList<DeadCodeFinder.MethodMeta> changedMethods,
        RuleSet rules,
        FactPathFinder.TraversalMode mode,
        bool needFootprints
    )
    {
        await using var context = new Rig.Storage.Storage.RigDbContext(baseDbPath, readOnly: true);
        var graph = await Reads.LoadFactGraphAsync(context, rules.Handoff);
        graph = FactPathFinder.ShapeGraph(graph, rules.Factory, rules.Cut, rules.Context);
        graph = FactPathFinder.MarkEventSubscriptionHandoffs(graph, await Reads.EventSubscriptionSitesAsync(context));

        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);
        var baseEps = epSet.Derived.Concat(epSet.PromotedOrigins).ToList();
        var idBySite = MethodIdBySite(methods);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = DeriveEffects(
            rules.Effects,
            rules.Observations,
            invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs
        );

        // Behavioral delta: reach FROM the changed methods (seeded param-free so a signature-changed method
        // matches its pre-change self), intersect with base effects, diff against the branch.
        var changedStems = changedMethods.Select(m => StripParams(m.SymbolId)).ToHashSet(StringComparer.Ordinal);
        var baseSeed = methods
            .Where(m => changedStems.Contains(StripParams(m.SymbolId)))
            .Select(m => m.SymbolId)
            .ToHashSet(StringComparer.Ordinal);
        var forward = FactPathFinder.ReachableFromAll(graph, baseSeed, mode: mode);
        var baseReached = effects.Where(e => e.EnclosingSymbolId is not null && forward.Contains(e.EnclosingSymbolId)).ToList();
        var delta = DiffBehavioral(
            branchEffects: branchEffects,
            baseEffects: baseReached,
            branchReach: branchReach,
            baseReach: forward.Count
        );

        // Phase 3: union the base's field/property-access targets into its reach sets too, so the per-EP
        // structural diff compares like-for-like (degenerate `R:` nodes on BOTH sides). Built once per store.
        var baseRefTargets = RefTargetsByEnclosing(await Reads.LoadFieldAccessRefsAsync(context));
        var reachSets = ComputeReachSets(graph, baseEps, idBySite, mode, refsByEnclosing: baseRefTargets);
        var footprints = needFootprints ? ComputeFootprints(graph, baseEps, idBySite, EffectKeysByEnclosing(effects), mode) : null;

        // Phase 2: the base body-hash map (guarded — empty on a pre-fact store), so RunAsync can diff it
        // against the branch's WITHOUT a second base load.
        var bodyHashes = await Reads.LoadSymbolBodyHashesAsync(context);

        return (delta, reachSets, footprints, bodyHashes);
    }

    // Diff two stores' per-EP footprints: for every EP present in BOTH (paired on Kind+Route), the effects its
    // reach gained/lost (set membership) AND the effects that are AMPLIFIED — same key on both sides but now
    // produced MORE (higher reach multiplicity) or MOVED INTO A LOOP (Feature 1). Returns only EPs whose
    // footprint changed in EITHER way, busiest-delta first. EPs added/removed wholesale are the EP-diff
    // section's job, not this. Internal for unit-testing the pure diff (ImpactAmplificationTests).
    internal static IReadOnlyList<EpFootprintDelta> DiffFootprints(
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> branch,
        Dictionary<(string Kind, string Route), Dictionary<(string, string, string, string), EffectReach>> baseStore,
        // (Kind, Route) -> the EP's site, so each delta carries FilePath/Line for FQN rendering. An EP missing
        // here (shouldn't happen — branch footprints are keyed off the same EPs) falls back to empty site.
        IReadOnlyDictionary<(string Kind, string Route), EntryPointRef> epByKey
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

            if (added.Count > 0 || removed.Count > 0 || amplified.Count > 0)
            {
                var site = epByKey.GetValueOrDefault(key);
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
                            .ToList()
                    )
                );
            }
        }

        return deltas
            .OrderByDescending(d => d.Added.Count + d.Removed.Count + d.Amplified.Count)
            .ThenBy(d => d.Route, StringComparer.Ordinal)
            .ToList();
    }

    private static void EmitPerEpTsv(TextWriter output, IReadOnlyList<EpFootprintDelta>? deltas, Dictionary<(string, int), string> fqnSites)
    {
        if (deltas is null)
        {
            return;
        }

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
        }
    }

    // PRIMARY section: the entry points whose reachable EFFECT set changed — the behavioral signal. This is the
    // small, high-information set (a handful), as opposed to the structural reachable-tree diff which is mostly
    // data-shape ripple. Always shown (no longer gated behind --per-ep — footprints are computed by default).
    private static void WritePerEpHuman(
        TextWriter output,
        string baseRef,
        string? baseDbPath,
        IReadOnlyList<EpFootprintDelta>? deltas,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        output.WriteLine();
        if (baseDbPath is null)
        {
            output.WriteLine("Behavioral changes per entry point: skipped (no base store resolved — see the entry-point-diff note above).");
            return;
        }

        if (deltas is null || deltas.Count == 0)
        {
            output.WriteLine($"Behavioral changes per entry point vs '{baseRef}': none — no entry point's reachable-effect set changed.");
            return;
        }

        // The behavioral set = (effect-set changed) ∪ (amplified) — an EP whose set is stable but has an
        // amplified effect (now produced more / in a loop) is in `deltas` too (DiffFootprints lists it).
        output.WriteLine(
            $"Behavioral changes per entry point vs '{baseRef}' (reachable-effect set changed or effect amplified): {deltas.Count}"
        );
        foreach (var d in deltas.Take(max))
        {
            // Render the FQN (round-trips into `rig tree`), same as the structural list; falls back to the route.
            var label = FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
            var ampPart = d.Amplified.Count > 0 ? $", ~{d.Amplified.Count} amplified" : "";
            output.WriteLine(
                $"{Indent.L2}{d.Kind} {label}  (effects {d.BaseEffects}→{d.BranchEffects}, +{d.Added.Count}/-{d.Removed.Count}{ampPart})"
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
                output.WriteLine($"{Indent.L3}~ {a.Provider} {a.Operation}{Resource(a.Resource)}  ({AmplifyNote(a)})  ({a.Enclosing})  [review]");
            }
        }

        static string Resource(string resource) => string.IsNullOrEmpty(resource) ? "" : $" {resource}";
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

    private static void EmitBehavioralDeltaTsv(TextWriter output, BehavioralDelta? delta)
    {
        if (delta is null)
        {
            return;
        }

        foreach (var (provider, operation, resource, enclosing) in delta.AddedEffects)
        {
            output.WriteLine($"effect_added\t{provider}\t{operation}\t{resource}\t{enclosing}");
        }

        foreach (var (provider, operation, resource, enclosing) in delta.RemovedEffects)
        {
            output.WriteLine($"effect_removed\t{provider}\t{operation}\t{resource}\t{enclosing}");
        }

        foreach (var (type, provider, operation, enclosing) in delta.AddedObservations)
        {
            output.WriteLine($"obs_added\t{type}\t{provider}\t{operation}\t{enclosing}");
        }

        foreach (var (type, provider, operation, enclosing) in delta.RemovedObservations)
        {
            output.WriteLine($"obs_removed\t{type}\t{provider}\t{operation}\t{enclosing}");
        }
    }

    private static void WriteBehavioralDeltaHuman(TextWriter output, string baseRef, BehavioralDelta? delta, int max)
    {
        if (delta is null)
        {
            return; // no base store — already reported by the entry-point-diff section
        }

        var reachDelta = delta.ReachBranch - delta.ReachBase;
        var reachSign = reachDelta >= 0 ? "+" : "";
        output.WriteLine();
        output.WriteLine($"Behavioral delta vs '{baseRef}' (effects reachable from the changed methods):");
        output.WriteLine($"{Indent.L1}reach: {delta.ReachBranch} methods ({reachSign}{reachDelta} vs base)");
        output.WriteLine($"{Indent.L1}effects: +{delta.AddedEffects.Count} / -{delta.RemovedEffects.Count}");
        foreach (var (provider, operation, resource, enclosing) in delta.AddedEffects.Take(max))
        {
            output.WriteLine($"{Indent.L3}+ {provider} {operation}{Resource(resource)}  ({enclosing})");
        }

        foreach (var (provider, operation, resource, enclosing) in delta.RemovedEffects.Take(max))
        {
            output.WriteLine($"{Indent.L3}- {provider} {operation}{Resource(resource)}  ({enclosing})");
        }

        if (delta.AddedObservations.Count > 0 || delta.RemovedObservations.Count > 0)
        {
            output.WriteLine($"{Indent.L1}observations: +{delta.AddedObservations.Count} / -{delta.RemovedObservations.Count}");
            foreach (var (type, provider, operation, enclosing) in delta.AddedObservations.Take(max))
            {
                output.WriteLine($"{Indent.L3}+ {type} on {provider} {operation} ({enclosing})");
            }

            foreach (var (type, provider, operation, enclosing) in delta.RemovedObservations.Take(max))
            {
                output.WriteLine($"{Indent.L3}- {type} on {provider} {operation} ({enclosing})");
            }
        }

        static string Resource(string resource) => string.IsNullOrEmpty(resource) ? "" : $" {resource}";
    }

    // The hosts that LOAD at least one affected entry point (the redeploy/risk set), via the deployment map.
    private static IReadOnlyList<string> AffectedServices(IReadOnlyList<EntryPointRef> eps, DeploymentMap deployments)
    {
        if (deployments.IsEmpty)
        {
            return [];
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in eps)
        {
            foreach (var s in deployments.ActiveServices(loadedServices: deployments.ServicesForFile(e.FilePath), requires: e.Requires))
            {
                set.Add(s);
            }
        }

        return set.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    // Project the derived + promoted entry points whose declaration SITE (FilePath, Line) is in `sites` into
    // deduped EntryPointRefs, ordered by kind then route. The (FilePath, Line) join is the same one `callers
    // --entrypoints` uses: an EP "touches" a method set when its site is reachable from it.
    private static IReadOnlyList<EntryPointRef> EntryPointsAtSites(
        IReadOnlyList<DerivedEntryPoint> derived,
        IReadOnlyList<DerivedEntryPoint> promoted,
        IReadOnlySet<(string FilePath, int Line)> sites
    ) =>
        derived
            .Concat(promoted)
            .Where(e => sites.Contains((e.FilePath, e.Line)))
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => new EntryPointRef(
                Kind: g.Key.Kind,
                Route: g.Key.Route,
                FilePath: g.Key.FilePath,
                Line: g.Key.Line,
                Requires: g.First().Requires
            ))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

    // The header: the one-line PROVEN-diff takeaway, then the change provenance + size. No speculative
    // reach here — that's the opt-in --reach section (WriteReach).
    private static void WriteHeader(
        TextWriter output,
        string baseRef,
        string repoRoot,
        FactPathFinder.TraversalMode mode,
        ChangeSummary change,
        ImpactDiff diff
    )
    {
        var asyncNote = mode == FactPathFinder.TraversalMode.AsyncInclude ? "  (--async: handoffs included)" : "";

        output.WriteLine(DiffSummary(baseRef, change, diff));
        output.WriteLine();
        output.WriteLine($"Impact of {baseRef}...working-tree in {ShortenPath(repoRoot)}{asyncNote}");
    }

    // The one-line takeaway: the PROVEN change vs the base store — entry-point and effect/observation deltas
    // plus the count of entry points whose reachable tree structurally changed. A null base store (no diff
    // possible) reports the change size only.
    private static string DiffSummary(string baseRef, ChangeSummary change, ImpactDiff diff)
    {
        if (diff.Ep is null || diff.Behavioral is null)
        {
            return $"{change.Methods} changed method(s); base '{baseRef}' not indexed — no diff "
                + "(index the base commit, or pass --base-store).";
        }

        var behavioral = diff.Behavioral;
        var obs = behavioral.AddedObservations.Count + behavioral.RemovedObservations.Count;
        var obsPart =
            obs > 0
                ? $", {PlusMinus(added: behavioral.AddedObservations.Count, removed: behavioral.RemovedObservations.Count)} risk observation(s)"
                : "";
        // Lead with the BEHAVIORAL count (EPs whose effect set changed) — the high-signal number — then the
        // structural-tree count as the broader, mostly data-shape figure. The two answer different questions:
        // "how many EPs do something different" vs "how many EPs reach changed code at all".
        var behavioralEps = (diff.PerEp ?? []).Count;
        return $"Diff vs '{baseRef}': {PlusMinus(added: diff.Ep.Added.Count, removed: diff.Ep.Removed.Count)} entry point(s), "
            + $"{PlusMinus(added: behavioral.AddedEffects.Count, removed: behavioral.RemovedEffects.Count)} effect(s){obsPart}"
            + $"; {behavioralEps} entry point(s) with a changed behavior, {diff.AffectedEps.Count} with a changed reachable tree.";
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
        string baseRef,
        ImpactDiff diff,
        IReadOnlyList<EpFootprintDelta>? behavioralDeltas
    )
    {
        output.WriteLine();
        var behavioralKeys = (behavioralDeltas ?? []).Select(d => (d.Kind, d.Route)).ToHashSet();
        var structuralOnly = diff.AffectedEps.Where(d => !behavioralKeys.Contains((d.Kind, d.Route))).ToList();
        if (structuralOnly.Count == 0)
        {
            output.WriteLine($"Structural-only reachable-tree changes vs '{baseRef}': none.");
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
            $"Structural-only reachable-tree changes vs '{baseRef}' (no behavioral effect change): {structuralOnly.Count} entry point(s)"
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
    // effect-set diff collapses, and excludes false positives whose reach didn't actually move. Needs a base
    // store (it's a two-store diff).
    private static void WriteAffected(
        TextWriter output,
        string baseRef,
        ImpactDiff diff,
        DeploymentMap deployments,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        if (diff.Behavioral is null)
        {
            return; // no base store — the EP-diff section already explained it
        }

        output.WriteLine();
        output.WriteLine($"Affected entry points (reachable tree changed) vs '{baseRef}': {diff.AffectedEps.Count}");
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

    // Print a list of entry points grouped by kind (busiest first), each line with its deployment chip, then
    // the per-service rollup. Shared by the proven affected-EP list and the speculative --reach list.
    private static void WriteEntryPointGroups(TextWriter output, IReadOnlyList<EntryPointRef> eps, DeploymentMap deployments, int max)
    {
        foreach (var kindGroup in eps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(max / 4 + 1))
            {
                WriteEntryPointLine(output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
            }

            if (kindGroup.Count() > max / 4 + 1)
            {
                output.WriteLine($"{Indent.L3}… +{kindGroup.Count() - (max / 4 + 1)} more (raise --limit, or --format tsv for all)");
            }
        }

        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(eps.Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, output);
        }
        else if (eps.Count > 0)
        {
            output.WriteLine($"{Indent.L1}(no deployments.json — entry points listed without service attribution)");
        }
    }

    // The SPECULATIVE blast radius (opt-in --reach): every entry point that reverse-reaches the change,
    // grouped by deployed service, and every effect in the change's forward reach. Pessimistic by nature in
    // a large codebase — kept out of the default output, which is the proven diff.
    private static void WriteReach(TextWriter output, BlastRadius blast, DeploymentMap deployments, int max)
    {
        var svcNote = blast.Services.Count > 0 ? $"  [{blast.Services.Count} service(s): {string.Join(", ", blast.Services)}]" : "";

        // (2) Affected entry points, grouped by deployed service.
        output.WriteLine();
        output.WriteLine(
            $"Affected entry points (reverse-reach over {blast.ReachedByCount} caller method(s)): {blast.AffectedEps.Count}{svcNote}"
        );
        WriteEntryPointGroups(output, blast.AffectedEps, deployments, max);

        // (3) Effects in the forward reach.
        output.WriteLine();
        output.WriteLine($"Effects in the forward reach (over {blast.ForwardCount} reachable method(s)): {blast.Effects.Count}");
        foreach (var g in blast.Effects.GroupBy(e => (e.Provider, e.Operation)).OrderByDescending(g => g.Count()).Take(max))
        {
            output.WriteLine($"{Indent.L1}{g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
        }
        if (blast.Observations.Count > 0)
        {
            output.WriteLine($"{Indent.L1}risky observations:");
            foreach (var o in blast.Observations.OrderByDescending(o => o.Value))
            {
                output.WriteLine($"{Indent.L3}{o.Key}: {o.Value}");
            }
        }
    }

    private static void EmitTsv(
        TextWriter output,
        IReadOnlyList<DeadCodeFinder.MethodMeta> changedMethods,
        ImpactDiff diff,
        BlastRadius blast,
        DeploymentMap deployments,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        // One stream of typed rows for CI/tooling. First column is the row kind. The PROVEN diff
        // (changed/affected_ep/+ ep_*/effect_*/obs_* rows, emitted by the Emit*Tsv helpers) AND the
        // speculative blast radius (entrypoint/effect rows) are both included — tooling picks what it needs.
        //  changed      <symbolId>  <file>  <line>
        //  affected_ep  <kind>  <route>  <fqn>  <cause>  <file>  <line>  <+addedStems>  <-removedStems>  <~changedStems>  <inplace>   (proven; <route> is the path-style diff key, <fqn> the dotted name `rig tree` matches — equals <route> when unresolved; <cause> is behavioral|record-shape|ctor-sig|in-place|other — behavioral = effect set changed, the rest are structural-only; counts are DISTINCT param-free stems; inplace = reachable bodies changed)
        //  structural_summary  <total>  <behavioral>  <record-shape>  <ctor-sig>  <in-place>  <other>   (one row: the cause breakdown of the affected-EP set — behavioral counts the EPs whose effect set changed, the rest are structural-only)
        //  ep_reach_+   <kind>  <route>  <symbolId>                            (newly in the EP's reach — raw method DocID, or an `R:`-prefixed field/property-access target, Phase 3)
        //  ep_reach_-   <kind>  <route>  <symbolId>                            (dropped from the EP's reach — raw method DocID, or an `R:`-prefixed field/property-access target, Phase 3)
        //  ep_reach_~   <kind>  <route>  <stem>                                (a reachable method whose SIGNATURE changed — param-free stem)
        //  ep_reach_inplace  <kind>  <route>  <symbolId>                       (a reachable method whose BODY changed in place — raw DocID, Phase 2)
        //  ep_delta     <kind>  <route>  <fqn>  <branchEffects>  <baseEffects>  <+added>  <-removed>  <~amplified>   (one per EP whose reachable-effect footprint changed: set membership and/or amplification; counts are effect KEYS)
        //  ep_effect_added    <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>   (an effect KEY newly in the EP's footprint)
        //  ep_effect_removed  <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>   (an effect KEY dropped from the EP's footprint)
        //  ep_effect_amplified  <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>  <baseCount>  <branchCount>  <baseInLoop>  <branchInLoop>   (Feature 1: SAME key on both stores but produced MORE — branchCount>baseCount — and/or MOVED INTO A LOOP — branchInLoop && !baseInLoop. count = # distinct reachable effect-bearing producing nodes. A REVIEW flag, not a verdict: can't tell a hot-cache re-read from a real extra cold call.)
        //  entrypoint   <kind>  <route>  <file>  <line>  <requires>  <loaded>  <active>   (reverse-reach — speculative)
        //  effect       <provider>  <operation>  <resource>  <enclosing>  <file>  <line>  <observations>   (forward reach)
        foreach (var m in changedMethods.Take(max))
        {
            output.WriteLine($"changed\t{m.SymbolId}\t{m.FilePath}\t{m.Line}");
        }

        // Cause per EP: behavioral when its effect set changed (it's in PerEp), else the structural sub-cause.
        var behavioralKeys = (diff.PerEp ?? []).Select(d => (d.Kind, d.Route)).ToHashSet();
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
        var causeCounts = diff.AffectedEps.GroupBy(CauseTag, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
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

        foreach (var e in blast.AffectedEps.Take(max))
        {
            var loaded = deployments.ServicesForFile(e.FilePath);
            var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
            output.WriteLine(
                $"entrypoint\t{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}"
            );
        }

        foreach (var e in blast.Effects.Take(max))
        {
            var observations = string.Join(',', (e.Observations ?? []).Select(o => o.Type));
            output.WriteLine(
                $"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{observations}"
            );
        }

        EmitEpDiffTsv(output, diff.Ep);
        EmitBehavioralDeltaTsv(output, diff.Behavioral);
        EmitPerEpTsv(output, diff.PerEp, fqnSites);
    }

    // Normalize a path for cross-source comparison: forward slashes, no trailing separator. The indexed
    // FilePath and the diff-derived absolute path can disagree on separator (`\` vs `/`); both pass through
    // here so the set lookup is separator-agnostic (the HashSet is already case-insensitive for Windows).
    private static string Norm(string path) => path.Replace(oldChar: '\\', newChar: '/').TrimEnd('/');

    // A repo path hint from the indexed run: prefer the `--from` source project (SourceProjectPath), else
    // the solution path. We only need a path INSIDE the work tree — GitTopLevel walks up to the root.
    private static string? RepoHintFromRun(RunSummary? run)
    {
        if (run is null)
        {
            return null;
        }

        var hint = run.SourceProjectPath ?? run.SolutionPath;
        if (string.IsNullOrEmpty(hint))
        {
            return null;
        }

        // A file path -> its directory (git needs a directory or any path inside the work tree).
        return File.Exists(hint) ? Path.GetDirectoryName(hint) ?? hint : hint;
    }

    // `git -C <path> rev-parse --show-toplevel` — the work-tree root the diff paths are relative to. Null
    // when git is unavailable or the path is not in a work tree.
    private static string? GitTopLevel(string path)
    {
        var (ok, stdout, _) = RunGit(path, "rev-parse", "--show-toplevel");
        if (!ok)
        {
            return null;
        }

        var top = stdout.Trim();
        return string.IsNullOrEmpty(top) ? null : Path.GetFullPath(top);
    }

    // The changed-file set with the line-level detail the blast-radius gate needs.
    //  Files          — union of committed (base...HEAD) + working-tree (.cs) paths, repo-root-relative POSIX.
    //  CommittedRanges — each committed file's changed NEW-side line ranges (from `git diff --unified=0`).
    //  DirtyFiles      — files with working-tree changes; their committed ranges are NOT trusted for line
    //                    precision (uncommitted edits shift line numbers vs the indexed coordinates).
    internal sealed record CsDiff(
        IReadOnlyList<string> Files,
        IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>> CommittedRanges,
        IReadOnlySet<string> DirtyFiles
    );

    // `base...HEAD` (three-dot) diffs from the merge-base, so a stale base only shows what THIS branch added,
    // not unrelated upstream churn — the right "what did I change" set. The working-tree diff (`git diff
    // HEAD`) folds in staged + unstaged edits so an uncommitted WIP branch is still covered.
    private static CsDiff ChangedCsFileDiff(string repoRoot, string baseRef, TextWriter error)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var committedRanges = new Dictionary<string, IReadOnlyList<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);

        // --unified=0: zero context lines, so each hunk header's new-side range is exactly the changed lines.
        var (ok, committed, err) = RunGit(repoRoot, "diff", "--unified=0", $"{baseRef}...HEAD");
        if (!ok)
        {
            // A bad/absent base ref shouldn't abort — fall back to the working-tree diff alone and warn.
            error.WriteLine($"impact: `git diff {baseRef}...HEAD` failed ({err.Trim()}); using working-tree changes only.");
        }
        else
        {
            foreach (var (file, ranges) in ParseUnifiedDiff(committed))
            {
                files.Add(file);
                committedRanges[file] = ranges;
            }
        }

        var dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (okWt, worktree, _) = RunGit(repoRoot, "diff", "--name-only", "HEAD");
        if (okWt)
        {
            foreach (var line in worktree.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    files.Add(line);
                    dirty.Add(line);
                }
            }
        }

        return new CsDiff(files.ToList(), committedRanges, dirty);
    }

    // Parse `git diff --unified=0` output into per-`.cs`-file NEW-side changed line ranges. A hunk header is
    //   @@ -<oldStart>[,<oldLen>] +<newStart>[,<newLen>] @@
    // and with --unified=0 the new-side span [newStart, newStart+newLen-1] is exactly the added/modified
    // lines. A pure DELETION carries +newStart,0 (no new lines): it sits in the seam between new lines
    // newStart and newStart+1, so we record [newStart, newStart+1] (clamped ≥1) — enough for the overlap
    // test to attribute it to an enclosing method, or trip the structural fallback if it straddles a gap.
    internal static IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>> ParseUnifiedDiff(string diffOutput)
    {
        var result = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var isCs = false;

        foreach (var raw in diffOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            // `+++ b/<path>` names the new-side file for the hunks that follow. `/dev/null` => file deleted
            // wholesale: nothing in the new tree to attribute, so leave `current` null and skip its hunks.
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = line[4..].Trim();
                path = path.StartsWith("b/", StringComparison.Ordinal) ? path[2..] : path;
                current = path == "/dev/null" ? null : path;
                isCs = current is not null && current.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!isCs || current is null || !line.StartsWith("@@", StringComparison.Ordinal))
            {
                continue;
            }

            if (TryParseHunkNewSide(line, start: out var start, len: out var len))
            {
                var range = len == 0 ? (Math.Max(val1: 1, val2: start), Math.Max(val1: 1, val2: start) + 1) : (start, start + len - 1);
                if (!result.TryGetValue(current, out var list))
                {
                    result[current] = list = [];
                }

                list.Add(range);
            }
        }

        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<(int, int)>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // Extract the new-side (start, len) from a `@@ -a,b +c,d @@` header. len defaults to 1 when omitted
    // (a single-line hunk: `+c`). Returns false for a header we can't parse (left untrusted, not crashed).
    private static bool TryParseHunkNewSide(string hunkHeader, out int start, out int len)
    {
        start = 0;
        len = 0;
        var plus = hunkHeader.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return false;
        }

        var end = plus + 1;
        while (end < hunkHeader.Length && (char.IsDigit(hunkHeader[end]) || hunkHeader[end] == ','))
        {
            end++;
        }

        var token = hunkHeader[(plus + 1)..end];
        var comma = token.IndexOf(',');
        if (comma < 0)
        {
            len = 1;
            return int.TryParse(token, CultureInfo.InvariantCulture, out start);
        }

        return int.TryParse(token[..comma], CultureInfo.InvariantCulture, out start)
            && int.TryParse(token[(comma + 1)..], CultureInfo.InvariantCulture, out len);
    }

    // Resolve a repo-root-relative POSIX diff path to the normalized absolute form used to join the indexed
    // FilePath (absolute, OS separators). Norm makes the comparison separator-agnostic.
    private static string NormalizeRepoRelative(string repoRoot, string repoRelativePosix) =>
        Norm(Path.GetFullPath(Path.Combine(repoRoot, repoRelativePosix.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar))));

    // The changed method set + how it was derived: PreciseFileCount files were narrowed to the methods whose
    // source extent overlaps a changed line range; FileGranularFileCount files were taken whole.
    internal sealed record ChangedSet(IReadOnlyList<DeadCodeFinder.MethodMeta> Methods, int PreciseFileCount, int FileGranularFileCount);

    // Map the changed files to changed methods, narrowing to symbol granularity where we can PROVE it and
    // falling back to file granularity (every method in the file) everywhere else. A file goes symbol-
    // granular only when ALL of these hold; otherwise it is taken whole — pessimistic, never under-reports:
    //   * we have trusted committed line ranges for it (committed + clean; see preciseRangesByFileNorm),
    //   * every indexed method in it has a known span [Line, EndLine] (EndLine > 0 — a pre-EndLine store
    //     yields none, so the file stays whole), and
    //   * every changed range overlaps SOME method span (a range hitting none is an edit outside any method
    //     body — a field/attribute/using/type-declaration change, or a deletion straddling a method gap —
    //     which can affect any member of the file, so we conservatively take the whole file).
    // changedFilesNorm and the keys of preciseRangesByFileNorm are normalized-absolute (NormalizeRepoRelative);
    // method FilePaths are normalized here so the join is separator/case-agnostic.
    internal static ChangedSet SelectChangedMethods(
        IReadOnlyList<DeadCodeFinder.MethodMeta> methods,
        IReadOnlyDictionary<string, int> endLineById,
        IReadOnlySet<string> changedFilesNorm,
        IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>> preciseRangesByFileNorm
    )
    {
        var byFile = methods
            .Where(m => !string.IsNullOrEmpty(m.FilePath))
            .GroupBy(m => Norm(m.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var selected = new List<DeadCodeFinder.MethodMeta>();
        var preciseFiles = 0;
        var fileGranularFiles = 0;
        foreach (var file in changedFilesNorm)
        {
            if (!byFile.TryGetValue(file, out var fileMethods) || fileMethods.Count == 0)
            {
                continue; // no indexed method declared in this changed file — nothing to seed
            }

            if (
                TryPreciseSelect(
                    fileMethods: fileMethods,
                    endLineById: endLineById,
                    preciseRangesByFileNorm: preciseRangesByFileNorm,
                    fileNorm: file,
                    narrowed: out var narrowed
                )
            )
            {
                selected.AddRange(narrowed);
                preciseFiles++;
            }
            else
            {
                selected.AddRange(fileMethods);
                fileGranularFiles++;
            }
        }

        return new ChangedSet(selected, PreciseFileCount: preciseFiles, FileGranularFileCount: fileGranularFiles);
    }

    // True (with `narrowed` = the overlapping methods) when this file qualifies for symbol-granular
    // selection; false when any gate fails (caller then takes the whole file). See SelectChangedMethods.
    private static bool TryPreciseSelect(
        List<DeadCodeFinder.MethodMeta> fileMethods,
        IReadOnlyDictionary<string, int> endLineById,
        IReadOnlyDictionary<string, IReadOnlyList<(int Start, int End)>> preciseRangesByFileNorm,
        string fileNorm,
        out List<DeadCodeFinder.MethodMeta> narrowed
    )
    {
        narrowed = [];
        if (!preciseRangesByFileNorm.TryGetValue(fileNorm, out var ranges))
        {
            return false; // no trusted committed ranges (dirty file, or only working-tree changes) → whole file
        }

        var spans = new List<(DeadCodeFinder.MethodMeta Method, int Start, int End)>(fileMethods.Count);
        foreach (var m in fileMethods)
        {
            if (m.Line <= 0 || !endLineById.TryGetValue(m.SymbolId, out var end) || end <= 0)
            {
                return false; // an unknown span could hide a changed range → can't prove precision → whole file
            }

            spans.Add((m, m.Line, Math.Max(val1: end, val2: m.Line)));
        }

        // Structural guard: a changed range overlapping no method span is an out-of-method edit → whole file.
        foreach (var r in ranges)
        {
            if (!spans.Any(sp => Overlaps(aStart: sp.Start, aEnd: sp.End, bStart: r.Start, bEnd: r.End)))
            {
                return false;
            }
        }

        narrowed = spans
            .Where(sp => ranges.Any(r => Overlaps(aStart: sp.Start, aEnd: sp.End, bStart: r.Start, bEnd: r.End)))
            .Select(sp => sp.Method)
            .ToList();
        return true;
    }

    // Inclusive 1-D interval overlap.
    private static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd) => aStart <= bEnd && bStart <= aEnd;

    private static (bool Ok, string StdOut, string StdErr) RunGit(string workingDir, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return (false, "", "could not start git");
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return (proc.ExitCode == 0, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
    }
}
