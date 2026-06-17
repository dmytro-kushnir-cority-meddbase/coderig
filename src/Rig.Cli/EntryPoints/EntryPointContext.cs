using Rig.Analysis.Rules;
using Rig.Cli.Deployments;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Caching.QueryCacheKeys;

namespace Rig.Cli.EntryPoints;

// Everything the query commands need about entry points + deployment attribution: deriving the rule-
// detected EP set (page/action/class-inheritance + promoted async-handoff origins), the pattern-
// independent site->kind map (tiered: materialized table → query cache → live derive), the per-tree
// EP render context, and the deployments.json load. The single home for the EP-derivation block that
// derive / callers --entrypoints / the EP-site map each copy-pasted.
internal static class EntryPointContext
{
    // deployments.json resolved against the store's primary (max-symbol) solution — the opt-in deployment
    // map every command loads the same way. Empty (no-op) when unconfigured. `log` surfaces config
    // problems (only `derive` passes one today).
    internal static async Task<DeploymentMap> LoadDeploymentsAsync(RigDbContext context, string workingDirectory, TextWriter? log = null) =>
        await DeploymentMap.LoadAsync(
            workingDirectory: workingDirectory,
            solutionPath: await PrimaryDeploymentSolutionPathAsync(context),
            log: log
        );

    // The solution to resolve deployments.json against: the run with the MOST symbols — the primary/root
    // solution (e.g. MedDBase.slnx at the monorepo root), NOT ListRunsAsync().FirstOrDefault() (which is
    // newest-first). In a multi-solution `--merge` store the newest run is whatever sub-solution was
    // merged last, sitting in a subdirectory; deployments.json host paths are relative to the root
    // solution's directory, so resolving against a sub-solution makes every host "not found". The
    // max-symbol run is the real root in practice. Null when the store has no runs.
    internal static async Task<string?> PrimaryDeploymentSolutionPathAsync(RigDbContext context) =>
        (await Reads.ListRunsAsync(context)).OrderByDescending(r => r.SymbolCount).FirstOrDefault()?.SolutionPath;

    // The rule-detected entry-point set (page/action/class-inheritance) plus the classified async-handoff
    // origins, derived from facts under the effective rules — the SAME set `rig derive` reports. Returns
    // the three pieces callers need: the L1 derived EPs, the classified handoffs, and the promoted origins
    // (deduped against the L1 set). epData is passed in so the caller can share its (heavy) load with the
    // effect deriver instead of re-querying it.
    internal static async Task<(
        IReadOnlyList<DerivedEntryPoint> Derived,
        IReadOnlyList<HandoffEntryPoint> ClassifiedHandoffs,
        IReadOnlyList<DerivedEntryPoint> PromotedOrigins
    )> DeriveEntryPointsAsync(
        RigDbContext context,
        FactEntryPointDeriver.FactEntryPointData epData,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var derived = FactEntryPointDeriver.Derive(epData, epRules, classRules);
        var classifiedHandoffs = (await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue, handoffRules))
            .Where(h => h.Dispatcher is not null)
            .ToList();
        var promoted = PromoteHandoffOrigins(classifiedHandoffs, derived);
        return (derived, classifiedHandoffs, promoted);
    }

    // Phase-3 origin promotion: a CLASSIFIED handoff target becomes a first-class DerivedEntryPoint —
    // kind from the matching dispatcher (background|timer|actor|event), route = the target's FQN
    // (same shape as the L1 class-inheritance route), registration site as file/line. Deduped against
    // the L1-rule EPs by route, so a `Process()` override that is BOTH an L1 EP and a handoff target
    // is not double-counted. Deduped among handoffs by route too (one origin per callback).
    internal static IReadOnlyList<DerivedEntryPoint> PromoteHandoffOrigins(
        IReadOnlyList<HandoffEntryPoint> classifiedHandoffs,
        IReadOnlyList<DerivedEntryPoint> existingEntryPoints
    )
    {
        var existingRoutes = new HashSet<string>(existingEntryPoints.Select(e => e.Route), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<DerivedEntryPoint>();
        foreach (var h in classifiedHandoffs)
        {
            var route = HandoffTargetRoute(h.Target);
            if (route is null || existingRoutes.Contains(route) || !seen.Add(route))
            {
                continue;
            }

            var kind = h.Kind ?? "background";
            var method = kind.ToUpperInvariant();
            result.Add(
                new DerivedEntryPoint(
                    Kind: kind,
                    Method: method,
                    Route: route,
                    DisplayName: $"{kind} {method} {route}",
                    FilePath: h.FilePath,
                    Line: h.Line,
                    Requires: h.Requires
                )
            );
        }
        return result;
    }

    // "M:Ns.Type.Method(args)" -> "Ns.Type.Method" (strip M:, params, generic arity) — the same route
    // shape FactEntryPointDeriver builds for class-inheritance EPs, so dedup-by-route lines up.
    internal static string? HandoffTargetRoute(string targetDocId)
    {
        if (!targetDocId.StartsWith("M:", StringComparison.Ordinal))
        {
            return null;
        }

        var body = targetDocId.Substring(2);
        var paren = body.IndexOf('(');
        if (paren >= 0)
        {
            body = body.Substring(startIndex: 0, length: paren);
        }

        var sb = new System.Text.StringBuilder(body.Length);
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '`')
            {
                i++;
                while (i < body.Length && char.IsDigit(body[i]))
                {
                    i++;
                }

                i--;
                continue;
            }
            sb.Append(body[i]);
        }
        return sb.ToString();
    }

    // Builds the EP-render context for a tree: the SymbolId->site map (from the loaded graph) and the
    // site->kind map (from the SAME derived entry-point set `derive` emits, incl. promoted handoff
    // origins). Returns null when deployments are unconfigured, so the default tree pays no cost.
    internal static async Task<EpRenderContext?> BuildEpContextAsync(
        RigDbContext context,
        FactGraphData graph,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules,
        DeploymentMap deployments,
        bool useCache = true
    )
    {
        if (deployments.IsEmpty)
        {
            return null;
        }

        // The site->kind map is the expensive, PATTERN-INDEPENDENT half — derive-or-cache it once per
        // (store + rules). The symbol->site map below is cheap and rebuilt fresh from THIS query's graph.
        var epSiteKind = await LoadOrDeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules, useCache);

        var siteById = graph
            .Methods.GroupBy(m => m.SymbolId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => ((string?)g.First().FilePath, g.First().Line), StringComparer.Ordinal);

        return new EpRenderContext(deployments, siteById, epSiteKind);
    }

    // Load the whole-store entry-point site map: (file,line) -> (kind, capability requirements), covering
    // both rule-detected EPs and promoted handoff origins. A pure function of the store + effective rules
    // (NO traversal pattern). Three tiers, fastest first:
    //   1. The entry_point_sites table `rig graph` materialized — INDEX data, read via raw ADO (no EF, no
    //      whole-store load, no derive). Used whenever the effective rules match what graph was built with,
    //      regardless of --no-cache (it's index data, like call_edges), so it serves the common path.
    //   2. The .rig/cache.db query cache — for --rules queries (rule-hash mismatch on the table) when
    //      caching is on; derives once then memoizes.
    //   3. A live derive — --no-cache with a rule mismatch, or no materialized table yet.
    internal static async Task<
        IReadOnlyDictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>
    > LoadOrDeriveEpSiteKindAsync(
        RigDbContext context,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules,
        bool useCache
    )
    {
        var rulesHash = RulesFingerprint.Compute(workingDirectory, extraRules);

        // Tier 1: the materialized index table (built at `rig graph` under the default rules).
        if (await EntryPointSiteStore.LoadAsync(context, rulesHash) is { } materialized)
        {
            return materialized;
        }

        if (!useCache)
        {
            return await DeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules);
        }

        // Tier 2: query cache (handles --rules, which the table doesn't cover).
        var rigDir = CommandLine.StoreLayout.ResolveStoreDir(workingDirectory);
        var storeKey = StoreKey(Path.Combine(rigDir, CommandLine.StoreLayout.DbFileName));
        using var cache = QueryCache.Open(rigDirectory: rigDir, storeKey: storeKey);
        var key = cache is null ? null : EpCacheKey(storeKey, rulesHash);
        if (key is not null && cache!.Get(key) is { } blob && EpSiteCacheCodec.Decode(blob) is { } hit)
        {
            return hit;
        }

        var derived = await DeriveEpSiteKindAsync(context, workingDirectory, extraRules, handoffRules);
        if (key is not null)
        {
            TryCache(() => cache!.Put(key, EpSiteCacheCodec.Encode(derived)));
        }

        return derived;
    }

    // The actual whole-store EP derivation (uncached): rule EPs + class-inheritance EPs + promoted handoff
    // origins, flattened to a (file,line)->(kind,requires) map. Shared by the lazy query path and the
    // eager `rig graph` warm-up.
    internal static async Task<Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>> DeriveEpSiteKindAsync(
        RigDbContext context,
        string workingDirectory,
        IReadOnlyList<string> extraRules,
        IReadOnlyList<FactHandoffRule> handoffRules
    )
    {
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var (derivedEps, _, promoted) = await DeriveEntryPointsAsync(context, epData, workingDirectory, extraRules, handoffRules);

        var epSiteKind = new Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>();
        foreach (var e in derivedEps.Concat(promoted))
        {
            epSiteKind[(e.FilePath, e.Line)] = (e.Kind, e.Requires);
        }

        return epSiteKind;
    }

    // Materialize the pattern-independent EP-site set as a first-class table right after `rig graph`
    // rebuilds the store, so every later query reads it via raw ADO (no EF, no whole-store load, no derive)
    // instead of paying the ~2.1s derivation. Gated on deployments.json — projects without deployment
    // attribution never use the EP set, so they pay nothing. Built with the DEFAULT rules and stamped with
    // their hash; a --rules query sees the mismatch and derives live under its own rules.
    internal static async Task MaterializeEntryPointSitesAsync(RigDbContext context, string workingDirectory)
    {
        if (!File.Exists(Path.Combine(workingDirectory, "deployments.json")))
        {
            return;
        }

        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory).ToArray();
        var sites = await DeriveEpSiteKindAsync(context, workingDirectory, [], handoffRules);
        await EntryPointSiteStore.PersistAsync(context, sites, RulesFingerprint.Compute(workingDirectory, []));
    }

    // "  ▶ kind  ⟦svc⟧" suffix for a from/root symbol (reaches/path/callers roots), or "" when there is
    // no deployment context or the symbol has no known declaration site.
    internal static string HeaderSuffix(EpRenderContext? epContext, string symbolId)
    {
        var tag = epContext?.HeaderTag(symbolId);
        return string.IsNullOrEmpty(tag) ? "" : $"  {tag}";
    }
}
