using Rig.Domain.Data;
using Rig.Domain.Functions;

namespace Rig.Cli.CommandLine;

// Correctness-of-disclosure for pattern arguments: the traversal commands match by substring, so one
// pattern can resolve to MULTIPLE distinct symbols (same method name on unrelated types). The results
// then span all of them — a tree forest with mixed roots, a merged caller/reach set — which reads as if
// it were ONE symbol's answer. rig already discloses ~heuristic dispatch, fan-out, and truncation;
// this discloses pattern ambiguity the same way: a stderr notice (stdout formats — tsv/llm — stay
// machine-clean), never a behavior change. Overloads are NOT ambiguous (they share a param-free FQN).
internal static class AmbiguityNotice
{
    private const int MaxListed = 5;

    // Resolve `pattern` against the graph's nodes and warn when it spans >1 distinct target.
    internal static void WarnIfAmbiguous(TextWriter error, string pattern, FactGraphData graph) =>
        WarnIfAmbiguous(error, pattern, FactPathFinder.DistinctMatchTargets(graph.Methods.Select(m => m.SymbolId), pattern));

    // Warn from an already-computed distinct-target set (tree passes its built roots' FQNs so the
    // disclosure also fires on a full cache hit, where the graph is never loaded).
    internal static void WarnIfAmbiguous(TextWriter error, string pattern, IReadOnlyList<string> distinctTargets)
    {
        if (distinctTargets.Count <= 1)
        {
            return;
        }

        var listed = string.Join(", ", distinctTargets.Take(MaxListed));
        var more = distinctTargets.Count > MaxListed ? $", +{distinctTargets.Count - MaxListed} more" : "";
        error.WriteLine(
            $"note: pattern '{pattern}' matched {distinctTargets.Count} distinct symbols ({listed}{more}) — results span ALL of them; qualify the pattern (e.g. 'DeclaringType.Method') to narrow."
        );
    }
}
