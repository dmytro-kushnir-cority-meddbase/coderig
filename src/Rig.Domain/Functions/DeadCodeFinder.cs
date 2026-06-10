using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Unreachable-symbol (dead-code) finder over the fact-derived call graph. A first-party method is a
// dead-code CANDIDATE when it is NOT reachable — forward, including interface->impl and base->override
// dispatch and method-group/ctor edges — from ANY entry-point root, and is not itself a root.
//
// Roots are supplied by the caller: the derived entry points (pages/actions/background/wcf/handoffs)
// PLUS process entry points (Main) PLUS test methods, and — in `treatExternallyVisibleAsRoots` (library)
// mode — every externally-visible (public/protected) member, since a library's API surface is reached
// from outside the indexed code. For an application, leave that off so unused public methods are
// flaggable (the C# compiler won't warn about them; that's where this finder earns its keep).
//
// This is a REPORT, never an auto-delete. Static facts cannot see reflection, DI-by-name,
// serialization, source-generator/Activator instantiation, or vtable contracts, so every candidate
// must be confirmed against the C# compiler (IDE0051/CS0169) or a human before removal. To keep the
// signal trustworthy, members reached through channels facts don't model are excluded up front:
// constructors/finalizers (instantiation), property/event accessors and operators (member/operator
// use isn't an invocation fact), abstract/interface members (contracts), and generated code. Override
// and virtual members are excluded by default too (they're dispatch targets) unless includeDispatchMembers.
//
// Tiering exploits an invariant: any *live* caller of a method makes that method live, so a dead
// method's direct callers are themselves all dead (or it has none). A dead method with zero callers
// is therefore a dead-CLUSTER ROOT — the actionable head to remove; one with callers is reached only
// by other dead code and falls out when its cluster goes.
public static class DeadCodeFinder
{
    public enum Tier
    {
        High,
        Medium,
        Low,
    }

    // Per-method facts the classifier needs beyond the call graph. Modifiers carries accessibility +
    // abstract/virtual/static (see FactExtractor.ModifiersOf); IsGenerated suppresses generated members;
    // Name discriminates ctors/accessors/operators reached off-graph.
    public sealed record MethodMeta(
        string SymbolId,
        string Name,
        string Modifiers,
        string FilePath,
        int Line,
        bool IsOverride,
        bool IsGenerated
    );

    public sealed record Candidate(string SymbolId, string FilePath, int Line, Tier Tier, string Reason, int DirectCallers);

    public static IReadOnlyList<Candidate> Find(
        FactGraphData graph,
        IEnumerable<string> roots,
        IReadOnlyList<MethodMeta> methods,
        bool treatExternallyVisibleAsRoots,
        bool includeDispatchMembers = false
    )
    {
        var rootList = new List<string>(roots);
        // Library mode: fold externally-visible members into the roots so the BFS marks them — and
        // everything they reach — live, rather than flagging the public API surface.
        if (treatExternallyVisibleAsRoots)
            foreach (var m in methods)
                if (IsExternallyVisible(m.Modifiers))
                    rootList.Add(m.SymbolId);

        var reachable = FactPathFinder.ReachableFromAll(graph, rootList);
        var rootSet = new HashSet<string>(rootList, StringComparer.Ordinal);

        // Direct caller count per callee, for the cluster-root vs reached-only-by-dead distinction.
        var directCallers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in graph.CallEdges)
        {
            directCallers.TryGetValue(e.Callee, out var c);
            directCallers[e.Callee] = c + 1;
        }

        var candidates = new List<Candidate>();
        foreach (var m in methods)
        {
            if (reachable.Contains(m.SymbolId) || rootSet.Contains(m.SymbolId))
                continue;
            if (m.IsGenerated)
                continue;
            if (IsExcludedKind(m.Name))
                continue;
            if (IsAbstract(m.Modifiers))
                continue;
            if (!includeDispatchMembers && (m.IsOverride || IsVirtual(m.Modifiers)))
                continue;

            directCallers.TryGetValue(m.SymbolId, out var callers);
            candidates.Add(
                new Candidate(
                    m.SymbolId,
                    m.FilePath,
                    m.Line,
                    ClassifyTier(m.Modifiers, callers),
                    callers == 0 ? "uncalled" : "only reached by dead code",
                    callers
                )
            );
        }

        return candidates.OrderBy(c => c.Tier).ThenBy(c => c.FilePath, StringComparer.Ordinal).ThenBy(c => c.Line).ToList();
    }

    // private uncalled = strongest (only reflection could reach a private member, rare); internal =
    // medium (project-internal API, but the project IS the indexed scope here); public/protected =
    // low (possible external/reflection/serialization use the index can't see).
    private static Tier ClassifyTier(string modifiers, int directCallers)
    {
        if (HasToken(modifiers, "private"))
            return Tier.High;
        if (HasToken(modifiers, "public") || HasToken(modifiers, "protected"))
            return Tier.Low;
        return Tier.Medium; // internal / unspecified
    }

    private static bool IsExternallyVisible(string modifiers) => HasToken(modifiers, "public") || HasToken(modifiers, "protected");

    private static bool IsAbstract(string modifiers) => HasToken(modifiers, "abstract");

    private static bool IsVirtual(string modifiers) => HasToken(modifiers, "virtual");

    // Constructors/finalizers (instantiation isn't a call edge to the method), property/event
    // accessors and operators (member/operator use isn't an invocation fact) — reached off-graph, so
    // never flag them.
    private static bool IsExcludedKind(string name) =>
        name is ".ctor" or ".cctor" or "Finalize"
        || name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("remove_", StringComparison.Ordinal)
        || name.StartsWith("op_", StringComparison.Ordinal);

    // Whitespace-delimited token test (netstandard2.0-safe; Modifiers is a space-joined list).
    private static bool HasToken(string modifiers, string token)
    {
        foreach (var part in modifiers.Split(' '))
            if (string.Equals(part, token, StringComparison.Ordinal))
                return true;
        return false;
    }
}
