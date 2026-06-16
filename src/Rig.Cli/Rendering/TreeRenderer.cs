using Rig.Analysis.Rules;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Rendering;

// The call-tree renderer: the box-drawing tree (RenderTreeNode), the single-impl-hop fold, the effect
// group/leaf formatting, and the seam-summary computation. Split out of the command layer so the tree's
// presentation logic — by far the densest rendering in rig — lives on its own, with the command body
// reduced to loading + filtering and a call into here.
internal static class TreeRenderer
{
    // Collapses single-target dispatch hops: when a node has exactly one child reached by impl-/override-
    // dispatch with no fan-out (Fanout <= 1) and the node itself carries no effect, the lone interface/base
    // hop is folded into its impl — the impl is promoted into the node's slot with a FoldedVia marker
    // naming the interface. Exact (a 1-target dispatch is determined, not an approximation); recurses
    // bottom-up so a chain of single-impl hops collapses. The node's own effect, a fan-out (>1), a
    // truncated/cut leaf, or extra children all block the fold (then children are folded in place).
    internal static TraceNode FoldSingleImplHops(TraceNode node, IReadOnlyDictionary<string, List<string>> effectsByMethod)
    {
        var kids = node.Children.Select(c => FoldSingleImplHops(c, effectsByMethod)).ToList();
        if (
            kids.Count == 1
            && kids[0].EdgeKind is "impl-dispatch" or "override-dispatch"
            && kids[0].Fanout <= 1
            && !kids[0].Truncated
            && !effectsByMethod.ContainsKey(node.SymbolId)
            && !node.Truncated
        )
            // Promote the impl into the folded-away interface's slot, carrying the interface node's generic
            // binding (the impl was reached via dispatch and has none of its own; it runs on the SAME
            // instantiation — identity base-list). Lets the impl's label + forwarding body monomorphize.
            return kids[0] with
            {
                FoldedVia = FoldedViaTypeName(node.SymbolId),
                DeclaringTypeArgBinding = kids[0].DeclaringTypeArgBinding ?? node.DeclaringTypeArgBinding,
                MethodTypeArgBinding = kids[0].MethodTypeArgBinding ?? node.MethodTypeArgBinding,
            };
        return node with { Children = kids };
    }

    // The folded-away interface/base TYPE's short name (e.g. "M:Ns.IFoo.Bar``1(..)" -> "IFoo"), for the
    // «via IFoo» marker — the impl's own name already carries the method, so only the type is informative.
    private static string FoldedViaTypeName(string methodSymbolId)
    {
        var paren = methodSymbolId.IndexOf('(');
        var head = paren >= 0 ? methodSymbolId.Substring(0, paren) : methodSymbolId;
        var lastDot = head.LastIndexOf('.');
        return ShortName(lastDot >= 0 ? head.Substring(0, lastDot) : head);
    }

    // Collects effect-bearing methods in DFS (source) order, deduped — the backing of `tree --effects`.
    internal static void CollectEffectful(
        TraceNode node,
        IReadOnlyDictionary<string, List<string>> effectsByMethod,
        List<string> ordered,
        HashSet<string> seen
    )
    {
        if (effectsByMethod.ContainsKey(node.SymbolId) && seen.Add(node.SymbolId))
            ordered.Add(node.SymbolId);
        foreach (var c in node.Children)
            CollectEffectful(c, effectsByMethod, ordered, seen);
    }

    internal static void CollectTreeMethods(TraceNode node, HashSet<string> seen)
    {
        seen.Add(node.SymbolId);
        foreach (var c in node.Children)
            CollectTreeMethods(c, seen);
    }

    // True when this node directly has an effect or any descendant does. A "↺seen" (Truncated) node
    // has no children here, so only its own effect counts — that's sound: the effects under the
    // method's real subtree are printed under its first (expanded) occurrence, so nothing is lost.
    internal static bool SubtreeHasEffect(TraceNode node, IReadOnlyDictionary<string, List<string>> effectsByMethod)
    {
        if (effectsByMethod.ContainsKey(node.SymbolId))
            return true;
        foreach (var c in node.Children)
            if (SubtreeHasEffect(c, effectsByMethod))
                return true;
        return false;
    }

    // Renders the call tree with box-drawing connectors (├─ └─ │). When pruning, a node's VISIBLE
    // children are filtered first so the last visible child gets └─ correctly. The root prints flush-left.
    internal static void RenderTreeNode(
        TraceNode node,
        string prefix,
        bool isLast,
        bool isRoot,
        IReadOnlyDictionary<string, List<string>> effectsByMethod,
        bool prune,
        FactRenderRules renderRules,
        // Precomputed REALISTIC effect union per collapse-seam hub (keyed by hub DocID): the de-duped
        // effects over the hub's full reach closure, NOT the truncated rendered subtree. Empty falls
        // back to a subtree walk (the closure was unavailable, e.g. unit tests).
        IReadOnlyDictionary<string, List<string>> seamEffects,
        TextWriter output,
        // `--files`: append each node's DEFINITION location (relpath:line) so the tree links to source.
        // locById maps SymbolId -> (file, line) from the loaded methods; null/false leaves nodes bare.
        bool files = false,
        IReadOnlyDictionary<string, (string? File, int Line)>? locById = null,
        // `--signatures`: show each method's compact parameter signature so same-named overloads differ.
        bool signatures = false,
        // Traversal-cut rules for the «cut» marker: a node matching a cut rule gets a visible marker
        // indicating that its subtree was cut during traversal (not just render). Null = no markers.
        IReadOnlyList<FactTraversalCutRule>? cutRules = null,
        // Deployment/EP context: when supplied, a node that is itself a rule-detected entry point is
        // marked with the ▶ kind + service chip. Null = no EP marking (default tree).
        EpRenderContext? epContext = null,
        // `--full`: render effects as provenance leaf nodes (call site + line) BELOW each method instead of
        // the inline {…} tag. effectLeavesByMethod carries the precomputed leaf bodies. false/null = the
        // compact inline-tag rendering used by default/--effects/--summary.
        bool full = false,
        IReadOnlyDictionary<string, List<string>>? effectLeavesByMethod = null,
        // Path-contextual monomorphization: the PARENT node's resolved concrete instantiation — its
        // declaring-type args and its own-method args. A child resolves its forwarded T:/M: binding tokens
        // against these, so a chain of static factories / generic methods renders concretely. Null at roots.
        IReadOnlyList<string?>? parentDeclaringConcrete = null,
        IReadOnlyList<string?>? parentMethodConcrete = null
    )
    {
        // Compute visible children first — the fan-out label must reflect how many branches are
        // actually rendered (pruning may drop effectless children, making ×2 fan-out misleading
        // when only 1 child survives).
        var children = prune ? node.Children.Where(c => SubtreeHasEffect(c, effectsByMethod)).ToList() : node.Children.ToList();
        var childPrefix = isRoot ? "" : prefix + (isLast ? "   " : "│  ");

        var dispatchTag = node.DispatchBasis == "heuristic" ? $"{node.EdgeKind} ~heuristic" : node.EdgeKind;
        // A folded single-impl hop shows «via IFoo» (the collapsed interface) in place of the dispatch tag.
        var dispatch =
            node.FoldedVia is not null ? $" «via {node.FoldedVia}»"
            : node.EdgeKind is "impl-dispatch" or "override-dispatch"
                ? (children.Count > 1 ? $" «{dispatchTag} ×{children.Count} fan-out»" : $" «{dispatchTag}»")
            : "";
        // An async handoff hop (only present under --async): mark the cross-thread boundary.
        var handoff = node.EdgeKind == EdgeKinds.Handoff ? $" ⤳handoff via {ShortName(node.HandoffVia)} [cross_thread]" : "";
        var loop = node.LoopKind is null ? "" : $" 🔁[{ShortLoop(node.LoopDetail)}]";
        // Identical sibling edges collapsed under one parent (e.g. a generic method called once per
        // type-arg): show the call-site count rather than N repeated "↺seen" lines.
        var calls = node.CallSites > 1 ? $" ×{node.CallSites} calls" : "";
        var seen = node.Truncated ? " ↺seen" : "";
        // Opaque-type render rule: a matching non-root node is drawn as a leaf — its own effects still
        // print, but its subtree is suppressed (the type's internals aren't worth expanding).
        var opaque = isRoot ? null : renderRules.MatchOpaque(node.SymbolId);
        var opaqueTag = opaque is not null ? $" «opaque: {opaque.Label}»" : "";
        // Traversal-cut marker: a node whose successors were cut during traversal (empty children,
        // not because it has none, but because a cut rule stopped the walk). We detect this by
        // matching the cut rules against the node and checking it has no children (was a traversal leaf).
        var cutTag = "";
        if (cutRules is { Count: > 0 } && node.Children.Count == 0 && !node.Truncated)
        {
            FactTraversalCutRule? matchedCut = null;
            foreach (var rule in cutRules)
                if (rule.IsMatch(node.SymbolId))
                {
                    matchedCut = rule;
                    break;
                }
            if (matchedCut is not null)
                cutTag = $" «cut: {matchedCut.Label}»";
        }
        // --full hoists effects out to leaf nodes (below), so the inline {…} tag is suppressed in that mode.
        var fx =
            !full && effectsByMethod.TryGetValue(node.SymbolId, out var list) && list.Count > 0
                ? "  {" + string.Join(", ", list) + "}"
                : "";
        var loc =
            files && locById is not null && locById.TryGetValue(node.SymbolId, out var l) && l.File is not null
                ? $"  📄 {ShortenPath(l.File)}:{l.Line}"
                : "";
        // This node's concrete instantiation (declaring-type args + own-method args), resolved from its
        // monomorphization bindings against the PARENT's resolved instantiation — path-contextual
        // monomorphization. Carried down to children as THEIR parent binding so a forwarding chain resolves.
        // Two node kinds carry no binding of their own but render in the PARENT's type-param scope, so they
        // INHERIT the parent's resolved instantiation (for both label and children) instead of resetting it:
        //   • a synthetic lambda (`…~λN`) — its body forwards the enclosing method's T:/M: params, and
        //     ShortName drops the `~λN` for a parameterful method so it renders AS that method; otherwise the
        //     chain breaks at `skip: i => Create(...)`.
        //   • an impl/override-dispatch hop — `IFoo<A,B>.M` dispatches to `Impl<A,B>.M` on the SAME runtime
        //     instantiation (identity base-list `Impl<T,U> : IFoo<T,U>`, the common case). Arity-mismatched
        //     impls fall back to placeholders automatically (PrettyGenericName only substitutes on a match);
        //     a reordered base-list mapping (`Impl<U,T> : IFoo<T,U>`) would mislabel — rare, accepted limit.
        // A node with its OWN binding always resolves that (a folded interface hop transfers its binding to
        // the promoted impl — see FoldSingleImplHops). Only a binding-less lambda/dispatch node inherits.
        var hasOwnBinding = node.DeclaringTypeArgBinding is not null || node.MethodTypeArgBinding is not null;
        var inheritsParentScope =
            !hasOwnBinding
            && (node.SymbolId.Contains("~λ", StringComparison.Ordinal) || node.EdgeKind is "impl-dispatch" or "override-dispatch");
        var (declaringConcrete, methodConcrete) = inheritsParentScope
            ? (parentDeclaringConcrete, parentMethodConcrete)
            : ResolveNodeInstantiation(
                node.DeclaringTypeArgBinding,
                node.MethodTypeArgBinding,
                parentDeclaringConcrete,
                parentMethodConcrete
            );
        var name =
            PrettyGenericName(ShortName(node.SymbolId), declaringConcrete, methodConcrete)
            + (signatures ? ShortSignature(node.SymbolId) : "");
        // EP marker: when this node is itself a rule-detected entry point, wrap its name with "▶ kind"
        // and a trailing service chip — the same custom rendering used by derive/callers.
        var (epPrefix, epSuffix) = epContext?.ChipFor(node.SymbolId) ?? ("", "");
        var label = $"{epPrefix}{name}{dispatch}{handoff}{loop}{calls}{seen}{opaqueTag}{cutTag}{fx}{loc}{epSuffix}";
        output.WriteLine(isRoot ? label : $"{prefix}{(isLast ? "└─ " : "├─ ")}{label}");

        // Collapse-seam render rule: this node is a fan-out hub (e.g. a reflection service-locator or
        // an ORM entity-constructor factory). Fold its candidate children into ONE summary leaf —
        // the union of effects reachable through them + how many lines were hidden — instead of N
        // near-identical polymorphic subtrees that drown out the real call story. Computed here (ahead of
        // the effect-leaf pass) so leaf connectors know whether a trailing seam summary line follows.
        var seam = renderRules.MatchCollapseSeam(node.SymbolId);

        // --full: emit this method's effects as provenance leaf nodes (call site + line) ahead of the call
        // children, so the effect-producing calls (e.g. ExecuteAsync) are visible rather than folded into a
        // tag. The last leaf gets └─ only when nothing trails it — an opaque node renders no children, a
        // collapsed seam renders exactly one summary line, otherwise the visible call children follow.
        if (
            full
            && effectLeavesByMethod is not null
            && effectLeavesByMethod.TryGetValue(node.SymbolId, out var fxLeaves)
            && fxLeaves.Count > 0
        )
        {
            var trailing = opaque is not null ? 0 : (seam is not null && children.Count > 0 ? 1 : children.Count);
            for (var i = 0; i < fxLeaves.Count; i++)
            {
                var lastLeaf = trailing == 0 && i == fxLeaves.Count - 1;
                output.WriteLine($"{childPrefix}{(lastLeaf ? "└─ " : "├─ ")}{fxLeaves[i]}");
            }
        }

        if (opaque is not null)
            return;

        if (seam is not null && children.Count > 0)
        {
            // Lines-hidden is the rendered subtree size; the effect union is the REALISTIC reach-closure
            // set (precomputed), falling back to the subtree walk when no closure was supplied.
            var (subtreeEffects, hidden) = SummarizeSubtrees(children, prune, effectsByMethod);
            var effects = seamEffects.TryGetValue(node.SymbolId, out var realistic) ? realistic : subtreeEffects;
            // Aggregated provider:operation tallies are few; the cap mainly bounds the raw-subtree fallback.
            const int cap = 30;
            var shown = effects.Take(cap).Select(e => "{" + e + "}");
            var overflow = effects.Count > cap ? $" …+{effects.Count - cap} more" : "";
            var fxUnion = effects.Count == 0 ? "" : "  " + string.Join(" ", shown) + overflow;
            output.WriteLine(
                $"{childPrefix}└─ ⋯ {children.Count} dispatch targets collapsed [seam: {seam.Label}]{fxUnion}  (+{hidden} lines hidden — `tree --raw` to expand)"
            );
            return;
        }

        for (var i = 0; i < children.Count; i++)
            RenderTreeNode(
                children[i],
                childPrefix,
                i == children.Count - 1,
                isRoot: false,
                effectsByMethod,
                prune,
                renderRules,
                seamEffects,
                output,
                files,
                locById,
                signatures,
                cutRules,
                epContext,
                full,
                effectLeavesByMethod,
                declaringConcrete,
                methodConcrete
            );
    }

    // Finds every collapse-seam hub in the tree(s) and precomputes its REALISTIC effect summary: the
    // effects over the hub's full forward reach closure (NOT the dedup/depth-truncated rendered subtree),
    // AGGREGATED by provider:operation with a distinct-resource count (e.g. `📥 llblgen:fetch ×42`). A
    // folded seam reaches hundreds of distinct resource-typed effects; listing them is noise, so we
    // collapse them after retrieval into a compact per-operation tally — what the folded region does, at
    // a readable altitude. Bounded by the tree's depth + the reach node budget.
    internal static Dictionary<string, List<string>> ComputeSeamEffects(
        IReadOnlyList<TraceNode> roots,
        FactRenderRules renderRules,
        FactGraphData graph,
        int maxDepth,
        FactPathFinder.TraversalMode mode,
        IReadOnlyDictionary<string, List<DerivedEffect>> structuredByMethod,
        Func<string, string, string> emojiFor
    )
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (renderRules.CollapseSeams.Count == 0)
            return result;

        var hubs = new HashSet<string>(StringComparer.Ordinal);
        void FindHubs(TraceNode node)
        {
            if (renderRules.MatchCollapseSeam(node.SymbolId) is not null)
                hubs.Add(node.SymbolId);
            foreach (var child in node.Children)
                FindHubs(child);
        }
        foreach (var root in roots)
            FindHubs(root);

        foreach (var hub in hubs)
        {
            var reach = FactPathFinder.ReachesWithFanout(graph, hub, maxDepth, mode: mode);
            // Distinct resource types per (provider, operation) over the whole reach closure.
            var perOp = new Dictionary<(string Provider, string Operation), HashSet<string>>();
            foreach (var sym in reach.Keys)
                if (structuredByMethod.TryGetValue(sym, out var list))
                    foreach (var effect in list)
                    {
                        if (!perOp.TryGetValue((effect.Provider, effect.Operation), out var resources))
                            perOp[(effect.Provider, effect.Operation)] = resources = new HashSet<string>(StringComparer.Ordinal);
                        resources.Add(effect.ResourceType);
                    }
            result[hub] = perOp
                .OrderByDescending(kv => kv.Value.Count)
                .ThenBy(kv => kv.Key.Provider, StringComparer.Ordinal)
                .ThenBy(kv => kv.Key.Operation, StringComparer.Ordinal)
                .Select(kv => $"{emojiFor(kv.Key.Provider, kv.Key.Operation)} {kv.Key.Provider}:{kv.Key.Operation} ×{kv.Value.Count}")
                .ToList();
        }
        return result;
    }

    // Walks a set of subtrees (respecting the same prune filter the renderer uses) and returns the
    // de-duplicated union of effect glyphs found in them, in first-seen order, plus the total rendered
    // node count. Backs the collapse-seam summary line so a folded fan-out still reports what it touches.
    private static (List<string> Effects, int Nodes) SummarizeSubtrees(
        IReadOnlyList<TraceNode> nodes,
        bool prune,
        IReadOnlyDictionary<string, List<string>> effectsByMethod
    )
    {
        var effects = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        void Walk(TraceNode node)
        {
            count++;
            if (effectsByMethod.TryGetValue(node.SymbolId, out var list))
                foreach (var effect in list)
                    if (seen.Add(effect))
                        effects.Add(effect);
            var kids = prune ? node.Children.Where(c => SubtreeHasEffect(c, effectsByMethod)) : node.Children;
            foreach (var child in kids)
                Walk(child);
        }

        foreach (var node in nodes)
            Walk(node);
        return (effects, count);
    }

    // Formats the raw effect group for one method into display strings, applying three transforms:
    // (1) lock:acquire+release pairs on the same resource → single "🔒 lock [resource]" entry
    //     (the pair is always emitted together and adds no information individually);
    //     if the sole resource is Threading.Monitor the resource name is omitted (always the same).
    // (2) identical rendered strings → deduplicated with a "×N" suffix.
    // (3) all effects are returned as individual strings; the caller joins them inside one {…} block.
    internal static List<string> FormatEffectGroup(
        IEnumerable<Rig.Domain.Data.DerivedEffect> effects,
        IReadOnlyDictionary<string, string> emoji
    )
    {
        var list = effects.ToList();

        // Collapse lock acquire+release pairs per resource.
        var acquiresByResource = list.Where(e => e.Provider == "lock" && e.Operation == "acquire")
            .GroupBy(e => e.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key ?? "", g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var releasesByResource = list.Where(e => e.Provider == "lock" && e.Operation == "release")
            .GroupBy(e => e.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key ?? "", g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var pairedResources = acquiresByResource
            .Keys.Intersect(releasesByResource.Keys, StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<string>();

        // Emit one collapsed "lock" entry per paired resource.
        foreach (var resource in pairedResources.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var lockEmoji = FactEffectEmojiProvider.For(emoji, "lock", "held");
            // Omit resource name when it is always Threading.Monitor — adds no information.
            var resourceLabel = resource.Contains("Threading.Monitor", StringComparison.OrdinalIgnoreCase) ? "" : $" {ShortName(resource)}";
            result.Add($"{lockEmoji} lock{resourceLabel}");
        }

        // Emit non-lock effects and any unpaired lock effects normally.
        foreach (var e in list)
        {
            var isPaired =
                pairedResources.Contains(e.ResourceType ?? "")
                && (e.Provider == "lock" && (e.Operation == "acquire" || e.Operation == "release"));
            if (isPaired)
                continue;
            result.Add(
                $"{FactEffectEmojiProvider.For(emoji, e.Provider, e.Operation)} {e.Provider}:{e.Operation} {ShortName(e.ResourceType)}"
            );
        }

        // Dedup: collapse identical strings to "label ×N".
        return result.GroupBy(s => s, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
    }

    // `tree --full`: one effect rendered as its OWN provenance leaf body — glyph + provider:op + resource +
    // the producing call site (file:line) — instead of the compact inline {…} tag the other modes hoist
    // onto the enclosing method. The caller orders a method's leaves by source line.
    internal static string FormatEffectLeaf(Rig.Domain.Data.DerivedEffect e, IReadOnlyDictionary<string, string> emoji)
    {
        var loc = string.IsNullOrEmpty(e.FilePath) ? "" : $"  {ShortenPath(e.FilePath)}:{e.Line}";
        return $"{FactEffectEmojiProvider.For(emoji, e.Provider, e.Operation)} {e.Provider}:{e.Operation} {ShortName(e.ResourceType)}{loc}";
    }

    // `tree --full`: a library call that produced NO effect (resolved to a referenced-assembly target, but
    // no rule matched it). Rendered as a dim leaf (· marker) so the call is visible without implying an
    // effect — distinct from the glyph-prefixed effect leaves above.
    internal static string FormatUnresolvedLeaf(string target, string? filePath, int line)
    {
        var loc = string.IsNullOrEmpty(filePath) ? "" : $"  {ShortenPath(filePath)}:{line}";
        var name = ShortName(target);
        // ShortName keeps a leading DocID kind prefix ("M:"/"T:"/…) for a namespace-less symbol; strip it.
        if (name.Length > 2 && name[1] == ':')
            name = name[2..];
        // Render generic arity the same way resolved tree nodes do (`Seq`1.Iter` -> `Seq<T>.Iter`) so the
        // library leaves don't show raw backtick arity next to the `<T,U>` of resolved siblings.
        return $"· {PrettyGenericName(name)}{loc}";
    }
}
