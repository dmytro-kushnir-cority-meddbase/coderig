using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig dead` — unreachable-symbol / dead-code finder over the fact graph. Roots = the derived entry points
// (pages/actions/background/wcf) + delegate/method-group handoffs + every Main + every test method. A
// first-party method NOT reachable from any root (forward, incl. dispatch) is a candidate. REPORT ONLY —
// confirm against the C# compiler (IDE0051/CS0169) or a human before removing; static facts miss
// reflection/DI/serialization.
internal static class DeadCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var rules = CommonOptions.Rules();
        var root = new Option<string[]>("--root")
        {
            Description = "Add every method whose SymbolId contains <pattern> as a root (repeatable).",
        };
        var lib = new Option<bool>("--include-lib", "--lib")
        {
            Description = "Treat public/protected members as roots (library API surface).",
        };
        var includeDispatch = new Option<bool>("--include-dispatch")
        {
            Description = "Also flag unreached override/virtual members (dispatch targets).",
        };
        var all = new Option<bool>("--all") { Description = "Include Low-confidence (public/protected) candidates." };
        var limit = CommonOptions.Limit(80);
        var format = CommonOptions.Format();
        var cmd = new Command("dead", "Find unreachable first-party methods (report-only).")
        {
            rules,
            root,
            lib,
            includeDispatch,
            all,
            limit,
            format,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        CommonOptions.RulesOf(pr.GetValue(rules)),
                        pr.GetValue(root) ?? [],
                        pr.GetValue(lib),
                        pr.GetValue(includeDispatch),
                        pr.GetValue(all),
                        pr.GetValue(limit),
                        pr.GetValue(format),
                        output,
                        workingDirectory
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        IReadOnlyList<string> extraRules,
        IReadOnlyList<string> rootPatterns,
        bool libMode,
        bool includeDispatch,
        bool showAll,
        int limit,
        string? format,
        TextWriter output,
        string workingDirectory
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);

        await using var context = OpenReadContext(workingDirectory);

        // TODO(perf): `dead` still loads the full ~1.4M-row call graph into memory (LoadFactGraphAsync) and
        // runs ReachableFromAll(roots) in process. This is the last read command doing a full-graph load. It
        // maps directly onto the SQL primitive (SqlReachability.ReachableSetAsync); left as-is intentionally —
        // `dead` is a cold/occasional audit path, not a hot query, so the in-memory load is acceptable for now.
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var graph = await Reads.LoadFactGraphAsync(context, handoffRules);
        var methods = await Reads.LoadDeadCodeMethodsAsync(context);
        if (methods.Count == 0)
        {
            output.WriteLine("No method symbols in the index — run `rig index`/`rig mine` first.");
            return 1;
        }

        // --- Roots: derived entry points + handoffs + Main + test methods ---
        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ep in FactEntryPointDeriver.Derive(epData, epRules, classRules))
            roots.Add(ep.Method);
        // RECALL RAIL: every delegate/method-group target stays a root REGARDLESS of classification — both
        // the surviving unclassified methodGroup edges AND the reclassified handoff edges. The sync-cut prunes
        // the registrar->callback edge from reach, so the callback must be a root or it would be falsely
        // flagged dead. (Constraint #1 in the handoff.)
        foreach (var edge in graph.CallEdges)
            if (edge.Kind is EdgeKinds.MethodGroup or EdgeKinds.Handoff)
                roots.Add(edge.Callee);
        // Process entry points: any method named Main.
        foreach (var m in methods)
            if (m.Name == "Main")
                roots.Add(m.SymbolId);
        // Test methods are framework-invoked roots: a ctor ref to a test attribute marks its enclosing method
        // ([Fact]/[Theory]/[Test]). Built in so `rig dead` works with no rules file.
        foreach (var cr in epData.CtorRefs)
            if (cr.Enclosing is not null && IsTestAttribute(cr.Target))
                roots.Add(cr.Enclosing);
        // User-supplied roots (--root <pattern>): every method whose SymbolId contains the pattern.
        if (rootPatterns.Count > 0)
            foreach (var m in methods)
                if (rootPatterns.Any(p => m.SymbolId.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0))
                    roots.Add(m.SymbolId);

        var candidates = DeadCodeFinder.Find(graph, roots, methods, libMode, includeDispatch);
        var shown = candidates.Where(c => showAll || c.Tier != DeadCodeFinder.Tier.Low).ToList();

        if (tsv)
        {
            foreach (var c in shown)
                output.WriteLine($"{c.Tier}\t{c.Reason}\t{c.DirectCallers}\t{c.SymbolId}\t{c.FilePath}:{c.Line}");
            return 0;
        }

        output.WriteLine($"Roots (entry points + handoffs + Main + tests): {roots.Count}");
        output.WriteLine($"First-party methods examined: {methods.Count}");
        output.WriteLine(
            $"Dead-code candidates: {candidates.Count}  (High {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.High)}, "
                + $"Medium {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.Medium)}, Low {candidates.Count(c => c.Tier == DeadCodeFinder.Tier.Low)})"
        );
        output.WriteLine(libMode ? "Mode: library (public/protected = roots)" : "Mode: application (public methods are flaggable)");
        output.WriteLine("REPORT ONLY — confirm each against the C# compiler (IDE0051/CS0169) before removing.");
        if (!showAll && candidates.Any(c => c.Tier == DeadCodeFinder.Tier.Low))
            output.WriteLine("(Low-confidence public/protected candidates hidden; pass --all to include them.)");
        output.WriteLine();
        foreach (var tierGroup in shown.GroupBy(c => c.Tier).OrderBy(g => g.Key))
        {
            output.WriteLine($"=== {tierGroup.Key} confidence ({tierGroup.Count()}) ===");
            foreach (var c in tierGroup.Take(limit))
            {
                var note = c.DirectCallers == 0 ? "" : $"  [reached only by {c.DirectCallers} dead caller(s)]";
                output.WriteLine($"{Indent.L1}{ShortName(c.SymbolId)}  {ShortenPath(c.FilePath)}:{c.Line}{note}");
            }
            if (tierGroup.Count() > limit)
                output.WriteLine($"{Indent.L1}… and {tierGroup.Count() - limit} more (raise --limit)");
        }
        return 0;
    }

    private static bool IsTestAttribute(string targetSymbolId) =>
        targetSymbolId.IndexOf("FactAttribute", StringComparison.Ordinal) >= 0
        || targetSymbolId.IndexOf("TheoryAttribute", StringComparison.Ordinal) >= 0
        || targetSymbolId.IndexOf("TestAttribute", StringComparison.Ordinal) >= 0;
}
