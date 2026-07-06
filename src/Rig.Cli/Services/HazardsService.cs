using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Commands;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using static Rig.Cli.Caching.QueryCacheKeys;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// Per-method hazard marks for the tree rooted at `fromPattern` — the SAME whole-store hazard set
// `rig tree --view hazards` surfaces: the effect-attached findings (race_window / lazy_init_race /
// dual_write / n_plus_1 / …) PLUS the graph-tier ones (cache_coherence / event_cycle / static_init_capture),
// filtered to the tree's reachable methods. Hazards are a whole-store fact (EP-independent), cached by
// (store + rules); this just filters that set to the tree. The web overlays these marks on tree nodes.
public static class HazardsService
{
    // One mark per (method, hazard type): the worst confidence the method carries for that type, and how many
    // sites fired. The client groups these by method id and paints a ⚠ on the node.
    public sealed record HazardMark(string MethodId, string Type, string Confidence, int Sites);

    public static async Task<IReadOnlyList<HazardMark>> ForTreeAsync(
        string workingDirectory,
        string fromPattern,
        string? storeRef = null,
        bool gate = true,
        IReadOnlyList<string>? extraRules = null
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory: workingDirectory, extraRules: extraRules ?? [], loadedPaths: out var loadedPaths);
        var ws = new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef);
        await using var context = await OpenReadContextGatedAsync(ws);

        // Build the tree (sync, full) to get the set of reachable methods to filter hazards to.
        var computation = await TreeQueryService.ComputeAsync(
            context: context,
            rules: rules,
            shaped: rules,
            fromPattern: fromPattern,
            maxDepth: int.MaxValue,
            maxNodes: FactPathFinder.DefaultTreeNodeBudget,
            mode: CommonOptions.Mode(async: false),
            raw: false
        );
        var treeMethods = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in computation.Roots)
        {
            CollectMethods(root, treeMethods);
        }

        if (treeMethods.Count == 0)
        {
            return [];
        }

        // Store-correct cache keys (mirror TreeCommand/DeriveCommand), so `--store` and rule edits key right.
        var rigDir = StoreLayout.ResolveReadStoreDir(ws);
        var storeKey = StoreKey(Path.Combine(rigDir, StoreLayout.DbFileName));
        var rulesHash = RulesFingerprint.ComputeFromPaths(loadedPaths);

        // Effect-attached hazards: the whole-store hazard-augmented effect set (cached, shared with `derive`),
        // filtered to the tree's methods, then flattened to findings.
        var hazardEffects = await LoadOrDeriveHazardEffectsAsync(
            context: context,
            rigDirectory: rigDir,
            storeKey: storeKey,
            rulesHash: rulesHash,
            rules: rules,
            useCache: true,
            epData: computation.EpData,
            gate: gate
        );
        var filteredEffects = hazardEffects
            .Where(e => e.EnclosingSymbolId is not null && treeMethods.Contains(e.EnclosingSymbolId))
            .ToList();
        var effectFindings = DeriveCommand.HazardFindings(filteredEffects).Where(f => treeMethods.Contains(f.Enclosing));

        // Graph-tier hazards (cache_coherence / event_cycle / static_init_capture) — not effect-attached.
        var graphFindings = (
            await LoadOrDeriveGraphHazardFindingsAsync(
                context: context,
                rigDirectory: rigDir,
                storeKey: storeKey,
                rulesHash: rulesHash,
                rules: rules,
                useCache: true
            )
        ).Where(f => treeMethods.Contains(f.Enclosing));

        // Group by (method, type) → worst confidence + site count.
        return effectFindings
            .Concat(graphFindings)
            .GroupBy(f => (f.Enclosing, f.Type))
            .Select(g => new HazardMark(
                MethodId: g.Key.Enclosing,
                Type: g.Key.Type,
                Confidence: g.OrderBy(f => ConfidenceRank(f.Confidence)).First().Confidence,
                Sites: g.Count()
            ))
            .ToList();
    }

    private static void CollectMethods(TraceNode node, HashSet<string> into)
    {
        into.Add(node.SymbolId);
        foreach (var child in node.Children)
        {
            CollectMethods(child, into);
        }
    }

    // high < medium < low so OrderBy(...).First() picks the WORST tier a method carries; unknown sorts last.
    private static int ConfidenceRank(string confidence) =>
        confidence switch
        {
            "high" => 0,
            "medium" => 1,
            "low" => 2,
            _ => 3,
        };
}
