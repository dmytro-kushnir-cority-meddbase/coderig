using System.CommandLine;
using System.Diagnostics;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
using Rig.Cli.Rules;
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
        var async = CommonOptions.Async();
        var rules = CommonOptions.Rules();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var cmd = new Command(name: "impact", description: "Blast radius of a git diff: affected entry points, services, effects.")
        {
            @base,
            repo,
            baseStore,
            async,
            rules,
            format,
            limit,
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
                        async: pr.GetValue(async),
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        format: pr.GetValue(format),
                        limit: pr.GetValue(limit),
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
        bool async,
        IReadOnlyList<string> extraRules,
        string? format,
        int? limit,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var max = limit ?? int.MaxValue;
        var mode = CommonOptions.Mode(async); // --async => walk handoff edges (reverse + forward), else sync-cut
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);

        await using var context = OpenReadContext(workingDirectory);

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
        var changedFiles = ChangedCsFiles(repoRoot, baseRef, error);
        if (changedFiles.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine($"No changed .cs files between '{baseRef}' and the working tree of {repoRoot}.");
            }

            return 0;
        }

        // File -> declared method symbols. LoadDeadCodeMethodsAsync is every method symbol with its
        // absolute definition FilePath — the file->symbol map the task calls for, no new query needed.
        // v1 is FILE-GRANULAR: EVERY method declared in a changed file is treated as changed (cheap,
        // facts-only). Line-range precision (map a +hunk line to its enclosing method) needs a method
        // END-line fact => a FactExtractor change + re-index; deferred (noted in the report).
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);

        // Normalize both sides to compare paths: indexed FilePath is absolute with OS separators; the diff
        // files are repo-root-relative POSIX. Build the absolute form of each changed file, normalized.
        var changedAbs = changedFiles
            .Select(f =>
                Norm(Path.GetFullPath(Path.Combine(path1: repoRoot, path2: f.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar))))
            )
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changedMethods = methods.Where(m => !string.IsNullOrEmpty(m.FilePath) && changedAbs.Contains(Norm(m.FilePath))).ToList();
        var changedIds = changedMethods.Select(m => m.SymbolId).ToHashSet(StringComparer.Ordinal);
        if (changedIds.Count == 0)
        {
            if (!tsv)
            {
                output.WriteLine(
                    $"{changedFiles.Count} changed .cs file(s), but none map to an indexed method symbol "
                        + "(file outside the indexed solution, or no methods declared). Nothing to trace."
                );
            }

            return 0;
        }

        // One whole-store graph drives BOTH directions (the changed set is scattered, so the bounded
        // single-pattern SQL subgraph the other commands use doesn't apply). Same load + ShapeGraph the
        // EF-fallback path of every traversal command uses, so impact walks the IDENTICAL shaped graph.
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules);
        var shaping = ShapingRuleSet.Load(workingDirectory, extraRules, raw: false);
        graph = FactPathFinder.ShapeGraph(graph, shaping.Factory, shaping.Cut, shaping.Context);
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
        var epSet = await DeriveEntryPointsAsync(context, epData, workingDirectory, extraRules, handoffRules);
        var derivedEps = epSet.Derived;
        var promoted = epSet.PromotedOrigins;
        var affectedEps = derivedEps
            .Concat(promoted)
            .Where(e => reachableSites.Contains((e.FilePath, e.Line)))
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line, g.First().Requires))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        // --- (3) Effects in the forward reach: everything reachable FROM the changed set (multi-source,
        // exact ids — ReachableFromAll is the engine's multi-source forward traversal), intersected with
        // the whole-store derived effects by enclosing symbol. This is `reaches`' effect intersection,
        // done from a set of seeds instead of one pattern.
        var forward = FactPathFinder.ReachableFromAll(graph, changedIds, mode: mode);
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = DeriveEffects(
            workingDirectory,
            extraRules,
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
        var throwCount = affectedEffects.Count(e => string.Equals(e.Provider, "throw", StringComparison.OrdinalIgnoreCase));
        var affectedServices = AffectedServices(affectedEps, deployments);

        // --- Two-store entry-point diff (step 3): EPs added/removed vs the base commit's store, paired on
        // (Kind, Route) — line/param-free, so formatting + signature edits don't churn the diff. Requires
        // the base commit to have its own indexed store. Null when no base store resolves (the blast-radius
        // output above still stands). See docs/design-impact-behavioral-diff.md §3.1-3.2.
        var branchEps = derivedEps.Concat(promoted).ToList();
        var baseDbPath = ResolveBaseDbPath(workingDirectory, baseStoreOverride, repoRoot, baseRef);
        var epDiff = baseDbPath is null
            ? null
            : await ComputeEpDiffAsync(baseDbPath, branchEps, workingDirectory, extraRules, handoffRules);

        if (tsv)
        {
            EmitTsv(output, changedMethods, affectedEps, deployments, affectedEffects, max);
            EmitEpDiffTsv(output, epDiff);
            return 0;
        }

        WriteHuman(
            output,
            baseRef,
            repoRoot,
            mode,
            changedFiles.Count,
            changedMethods,
            reachedBy.Count,
            affectedEps,
            deployments,
            forward.Count,
            affectedEffects,
            observationCounts,
            throwCount,
            affectedServices,
            max
        );
        WriteEpDiffHuman(output, baseRef, baseDbPath, epDiff, StoreLayout.AvailableStoreIds(workingDirectory), max);
        return 0;
    }

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

        var baseSha = ResolveRefToSha(repoRoot, baseRef) ?? baseRef;
        var dir = StoreLayout.ResolveStoreDirByRef(workingDirectory, baseSha);
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
    internal static async Task<EpDiff> ComputeEpDiffAsync(
        string baseDbPath,
        IReadOnlyList<DerivedEntryPoint> branchEps,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        await using var baseContext = new Rig.Storage.Storage.RigDbContext(baseDbPath, readOnly: true);
        var baseEpData = await Reads.LoadFactEntryPointDataAsync(baseContext);
        var baseSet = await DeriveEntryPointsAsync(baseContext, baseEpData, workingDirectory, extraRules, handoffRules);
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
        return new EpDiff(added, removed);
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

    // The hosts that LOAD at least one affected entry point (the redeploy/risk set), via the deployment map.
    private static IReadOnlyList<string> AffectedServices(
        IReadOnlyList<(string Kind, string Route, string FilePath, int Line, IReadOnlyList<string>? Requires)> eps,
        DeploymentMap deployments
    )
    {
        if (deployments.IsEmpty)
        {
            return [];
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in eps)
        {
            foreach (var s in deployments.ActiveServices(deployments.ServicesForFile(e.FilePath), e.Requires))
            {
                set.Add(s);
            }
        }

        return set.OrderBy(s => s, StringComparer.Ordinal).ToList();
    }

    private static void WriteHuman(
        TextWriter output,
        string baseRef,
        string repoRoot,
        FactPathFinder.TraversalMode mode,
        int changedFileCount,
        IReadOnlyList<DeadCodeFinder.MethodMeta> changedMethods,
        int reachedByCount,
        IReadOnlyList<(string Kind, string Route, string FilePath, int Line, IReadOnlyList<string>? Requires)> affectedEps,
        DeploymentMap deployments,
        int forwardCount,
        IReadOnlyList<DerivedEffect> affectedEffects,
        IReadOnlyDictionary<string, int> observationCounts,
        int throwCount,
        IReadOnlyList<string> affectedServices,
        int max
    )
    {
        var asyncNote = mode == FactPathFinder.TraversalMode.AsyncInclude ? "  (--async: handoffs included)" : "";

        // The risk headline — the one-line takeaway, before the detail.
        output.WriteLine(RiskHeadline(affectedEps.Count, affectedServices, affectedEffects.Count, observationCounts, throwCount));
        output.WriteLine();

        output.WriteLine($"Impact of {baseRef}...working-tree in {ShortenPath(repoRoot)}{asyncNote}");
        output.WriteLine($"  Changed: {changedMethods.Count} method(s) across {changedFileCount} file(s) (file-granular)");

        // (2) Affected entry points, grouped by deployed service.
        output.WriteLine();
        output.WriteLine($"Affected entry points (reverse-reach over {reachedByCount} caller method(s)): {affectedEps.Count}");
        foreach (var kindGroup in affectedEps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
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
            WriteServiceSummary(affectedEps.Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, output);
        }
        else if (affectedEps.Count > 0)
        {
            output.WriteLine($"{Indent.L1}(no deployments.json — entry points listed without service attribution)");
        }

        // (3) Effects in the forward reach.
        output.WriteLine();
        output.WriteLine($"Effects in the forward reach (over {forwardCount} reachable method(s)): {affectedEffects.Count}");
        foreach (var g in affectedEffects.GroupBy(e => (e.Provider, e.Operation)).OrderByDescending(g => g.Count()).Take(max))
        {
            output.WriteLine($"{Indent.L1}{g.Count(), 4}  {g.Key.Provider} {g.Key.Operation}");
        }
        if (observationCounts.Count > 0)
        {
            output.WriteLine($"{Indent.L1}risky observations:");
            foreach (var o in observationCounts.OrderByDescending(o => o.Value))
            {
                output.WriteLine($"{Indent.L3}{o.Key}: {o.Value}");
            }
        }
    }

    // The one-line takeaway. Orders by severity: nothing reachable → safe; else lead with EPs/services,
    // then the highest-signal risk observation present.
    private static string RiskHeadline(
        int epCount,
        IReadOnlyList<string> services,
        int effectCount,
        IReadOnlyDictionary<string, int> observationCounts,
        int throwCount
    )
    {
        if (epCount == 0 && effectCount == 0)
        {
            return "RISK: low — the change reaches no entry points and no effects (isolated / dead-ish code).";
        }

        var risks = new List<string>();
        if (observationCounts.TryGetValue("looped_effect", out var loop) && loop > 0)
        {
            risks.Add($"{loop} looped effect(s) (n+1)");
        }

        if (observationCounts.TryGetValue("read_before_commit", out var rbc) && rbc > 0)
        {
            risks.Add($"{rbc} read-before-commit (lost-update/TOCTOU)");
        }

        if (observationCounts.TryGetValue("parallel_fanout", out var pf) && pf > 0)
        {
            risks.Add($"{pf} parallel fan-out");
        }

        if (throwCount > 0)
        {
            risks.Add($"{throwCount} throw(s)");
        }

        var svcPart = services.Count > 0 ? $" across {services.Count} service(s) [{string.Join(", ", services)}]" : "";
        var riskPart = risks.Count > 0 ? $"; risk signals: {string.Join(", ", risks)}" : "";
        var level = risks.Count > 0 || services.Count > 1 ? "HIGH" : (epCount > 0 ? "MEDIUM" : "LOW");
        return $"RISK: {level} — change reaches {epCount} entry point(s){svcPart}, {effectCount} effect(s){riskPart}.";
    }

    private static void EmitTsv(
        TextWriter output,
        IReadOnlyList<DeadCodeFinder.MethodMeta> changedMethods,
        IReadOnlyList<(string Kind, string Route, string FilePath, int Line, IReadOnlyList<string>? Requires)> affectedEps,
        DeploymentMap deployments,
        IReadOnlyList<DerivedEffect> affectedEffects,
        int max
    )
    {
        // One stream of typed rows for CI/tooling. First column is the row kind.
        //  changed     <symbolId>  <file>  <line>
        //  entrypoint  <kind>  <route>  <file>  <line>  <requires>  <loadedServices>  <activeServices>
        //  effect      <provider>  <operation>  <resource>  <enclosing>  <file>  <line>  <observations>
        foreach (var m in changedMethods.Take(max))
        {
            output.WriteLine($"changed\t{m.SymbolId}\t{m.FilePath}\t{m.Line}");
        }

        foreach (var e in affectedEps.Take(max))
        {
            var loaded = deployments.ServicesForFile(e.FilePath);
            var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
            output.WriteLine(
                $"entrypoint\t{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}"
            );
        }

        foreach (var e in affectedEffects.Take(max))
        {
            var observations = string.Join(',', (e.Observations ?? []).Select(o => o.Type));
            output.WriteLine(
                $"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{observations}"
            );
        }
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

    // The union of changed `.cs` files: committed (base...HEAD) + working tree (tracked changes vs HEAD).
    // `base...HEAD` (three-dot) diffs from the merge-base, so a stale base only shows what THIS branch
    // added, not unrelated upstream churn — the right "what did I change" set. The working-tree diff
    // (`git diff HEAD`) folds in staged + unstaged edits so an uncommitted WIP branch is covered.
    private static IReadOnlyList<string> ChangedCsFiles(string repoRoot, string baseRef, TextWriter error)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var (ok, committed, err) = RunGit(repoRoot, "diff", "--name-only", $"{baseRef}...HEAD");
        if (!ok)
        {
            // A bad/absent base ref shouldn't abort — fall back to the working-tree diff alone and warn.
            error.WriteLine($"impact: `git diff {baseRef}...HEAD` failed ({err.Trim()}); using working-tree changes only.");
        }
        else
        {
            AddCsLines(files, committed);
        }

        var (okWt, worktree, _) = RunGit(repoRoot, "diff", "--name-only", "HEAD");
        if (okWt)
        {
            AddCsLines(files, worktree);
        }

        return files.ToList();

        static void AddCsLines(HashSet<string> set, string output)
        {
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    set.Add(line);
                }
            }
        }
    }

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
