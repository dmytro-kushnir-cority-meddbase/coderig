using System.Text;
using System.Text.RegularExpressions;
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
        {
            return kids[0] with
            {
                FoldedVia = FoldedViaTypeName(node.SymbolId),
                DeclaringTypeArgBinding = kids[0].DeclaringTypeArgBinding ?? node.DeclaringTypeArgBinding,
                MethodTypeArgBinding = kids[0].MethodTypeArgBinding ?? node.MethodTypeArgBinding,
            };
        }

        return node with
        {
            Children = kids,
        };
    }

    // The folded-away interface/base TYPE's short name (e.g. "M:Ns.IFoo.Bar``1(..)" -> "IFoo"), for the
    // «via IFoo» marker — the impl's own name already carries the method, so only the type is informative.
    private static string FoldedViaTypeName(string methodSymbolId)
    {
        var paren = methodSymbolId.IndexOf('(');
        var head = paren >= 0 ? methodSymbolId.Substring(startIndex: 0, length: paren) : methodSymbolId;
        var lastDot = head.LastIndexOf('.');
        return ShortName(lastDot >= 0 ? head.Substring(startIndex: 0, length: lastDot) : head);
    }

    // --guards: render a frozen control-dependence guard set (FactStructuralContext-encoded on the reaching
    // edge) as a compact AND-chain of branch predicates — every guard must hold for the call to run, so they
    // join with " && ". Polarity: `pred` for the if-arm (WhenTrue), `!pred` for the else-arm. A compound
    // predicate is parenthesised when negated (`!(a == null)`) so the `!` binds unambiguously; a single token
    // (`!flag`, `!invoice.IsHealthcode`) stays bare. Each predicate's whitespace is collapsed (a condition can
    // span lines) and the joined string is length-capped for single-line trace output.
    //
    // Redundant-with-loop filter: a call DIRECTLY in `foreach (x in COLL)` is control-dependent on the
    // enumerator MoveNext, whose predicate Roslyn surfaces as COLL — but 🔁[x in COLL] already conveys exactly
    // that, so a guard whose predicate IS the loop's iterated collection is dropped as noise (`loopDetail` is
    // the reaching edge's LoopDetail). A genuine inner condition (`File.Exists(x)`, an `if`) has a different
    // predicate and is kept. while/for carry no " in " collection marker, so they are never filtered (and
    // empirically don't emit the redundant condition-guard). Returns "" when nothing remains (all guards were
    // loop-redundant, or the set decoded empty) — the caller then omits the ⎇ glyph entirely.
    // maxLength caps the rendered predicate (default 60, the terminal-friendly cap the CLI uses). Callers that
    // present their own truncation — e.g. the web UI, which sends full text and ellipsises in CSS — pass a
    // large value to get the UNtruncated predicate. The semantic formatting (foreach-guard filtering, else-arm
    // negation, short-circuit join) is unaffected; only the trailing length clamp honours maxLength.
    internal static string ShortGuards(string? encoded, string? loopDetail = null, int maxLength = 60)
    {
        var guards = FactStructuralContext.DecodeGuards(encoded);
        if (guards.Count == 0)
        {
            return "";
        }

        var loopCollection = ForeachCollection(loopDetail);
        var parts = new List<string>();
        foreach (var g in guards)
        {
            var pred = CollapseWhitespace(g.Predicate);
            if (loopCollection is not null && string.Equals(pred, loopCollection, StringComparison.Ordinal))
            {
                continue; // the foreach MoveNext guard — redundant with the 🔁[x in COLL] marker
            }

            parts.Add(
                g.WhenTrue ? pred
                : pred.Contains(' ', StringComparison.Ordinal) ? $"!({pred})"
                : $"!{pred}"
            );
        }

        if (parts.Count == 0)
        {
            return "";
        }

        var s = string.Join(" && ", parts);
        return s.Length <= maxLength ? s : s.Substring(startIndex: 0, length: maxLength - 3) + "...";
    }

    // The COLL of a `foreach (ident in COLL)` loop detail (StructuralContext's "{ident} in {expr}" form),
    // whitespace-collapsed for comparison; null when the detail is a while/for/null (no " in " marker).
    private static string? ForeachCollection(string? loopDetail)
    {
        if (string.IsNullOrEmpty(loopDetail))
        {
            return null;
        }

        var inAt = loopDetail!.IndexOf(" in ", StringComparison.Ordinal);
        return inAt < 0 ? null : CollapseWhitespace(loopDetail.Substring(inAt + 4));
    }

    private static string CollapseWhitespace(string s) =>
        string.Join(' ', s.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    // Collects effect-bearing methods in DFS (source) order, deduped — the backing of `tree --effects`.
    internal static void CollectEffectful(
        TraceNode node,
        IReadOnlyDictionary<string, List<string>> effectsByMethod,
        List<string> ordered,
        HashSet<string> seen
    )
    {
        if (effectsByMethod.ContainsKey(node.SymbolId) && seen.Add(node.SymbolId))
        {
            ordered.Add(node.SymbolId);
        }

        foreach (var c in node.Children)
        {
            CollectEffectful(c, effectsByMethod, ordered, seen);
        }
    }

    internal static void CollectTreeMethods(TraceNode node, HashSet<string> seen)
    {
        seen.Add(node.SymbolId);
        foreach (var c in node.Children)
        {
            CollectTreeMethods(c, seen);
        }
    }

    // True when this node directly has an effect or any descendant does. A "⋯elided" (Truncated) node
    // has no children here, so only its own effect counts — that's sound: the effects under the
    // method's real subtree are printed under its first (expanded) occurrence, so nothing is lost.
    internal static bool SubtreeHasEffect(TraceNode node, IReadOnlyDictionary<string, List<string>> effectsByMethod)
    {
        if (effectsByMethod.ContainsKey(node.SymbolId))
        {
            return true;
        }

        foreach (var c in node.Children)
        {
            if (SubtreeHasEffect(c, effectsByMethod))
            {
                return true;
            }
        }

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
        // `--plain`: drop the box-drawing connectors (├─ └─ │) for pure 2-space-per-depth indentation. The
        // hierarchy stays legible but the lines carry no positional glyphs, so a diff of two plain trees shows
        // only real structure changes — the connectors otherwise churn whenever a sibling is added/removed.
        bool plain = false,
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
        IReadOnlyList<string?>? parentMethodConcrete = null,
        // `--hazards`: a precomputed compact hazard marker per enclosing method (SymbolId -> e.g.
        // "  ⚠ dual_write(medium), race_window(high)"), appended to the node label so the pattern findings
        // sit inline on the EP's reachable tree. Null/absent leaves nodes unmarked (the default tree).
        IReadOnlyDictionary<string, string>? hazardsByMethod = null,
        // `--guards`: mark a control-dependence-GUARDED call edge with ⎇ [predicate] (the analog of 🔁[loop]),
        // decoded from the reaching edge's frozen guard set (TraceNode.EnclosingGuards). Off by default so
        // golden tree tests don't churn; a must-run edge (empty guard set) carries no glyph.
        bool guards = false
    )
    {
        // Compute visible children first — the fan-out label must reflect how many branches are
        // actually rendered (pruning may drop effectless children, making ×2 fan-out misleading
        // when only 1 child survives).
        var children = prune ? node.Children.Where(c => SubtreeHasEffect(c, effectsByMethod)).ToList() : node.Children.ToList();
        // Plain mode: pure indentation, no vertical guides — children indent 2 spaces per level (the root prints
        // flush-left, but ITS children still indent). Box-drawing mode: the standard ├─/└─ guides with the │
        // continuation lane, and the root contributes no prefix (its children align under it).
        var childPrefix =
            plain ? prefix + "  "
            : isRoot ? ""
            : prefix + (isLast ? "   " : "│  ");
        // The branch connector for a NON-root line at this prefix, given whether it is the last visible child.
        string Connector(bool last) =>
            plain ? ""
            : last ? "└─ "
            : "├─ ";

        var dispatchTag = node.DispatchBasis == "heuristic" ? $"{node.EdgeKind} ~heuristic" : node.EdgeKind;
        // A folded single-impl hop shows «via IFoo» (the collapsed interface) in place of the dispatch tag.
        var dispatch =
            node.FoldedVia is not null ? $" «via {node.FoldedVia}»"
            : node.EdgeKind is "impl-dispatch" or "override-dispatch"
                ? (children.Count > 1 ? $" «{dispatchTag} ×{children.Count} fan-out»" : $" «{dispatchTag}»")
            // Delegate-field fan-out belongs to the reaching edge, not this node's child count.
            : node.EdgeKind == EdgeKinds.DelegateField
                ? (node.Fanout > 1 ? $" «{dispatchTag} ×{node.Fanout} fan-out»" : $" «{dispatchTag}»")
            : "";
        // An async handoff hop (only present under --async): mark the cross-thread boundary.
        var handoff = node.EdgeKind == EdgeKinds.Handoff ? $" ⤳handoff via {ShortName(node.HandoffVia)} [cross_thread]" : "";
        var loop = node.LoopKind is null ? "" : $" 🔁[{ShortLoop(node.LoopDetail)}]";
        // --guards: the control-dependence guard set of the edge that reached this node — the branch
        // predicates gating whether the call runs in its parent. Empty (must-run) → no glyph; the ⎇
        // analog of 🔁. The reaching edge's LoopDetail lets ShortGuards drop a foreach's own MoveNext guard
        // (redundant with 🔁). Off unless --guards (keeps default golden trees stable).
        var guardText =
            guards && node.EnclosingGuards is not null ? ShortGuards(encoded: node.EnclosingGuards, loopDetail: node.LoopDetail) : "";
        // Space between ⎇ and [ : the ⎇ glyph (U+2387) renders narrow in some terminals and overlaps the
        // following bracket without it. (🔁 is a full-width emoji and doesn't need the gap.)
        var guardTag = guardText.Length == 0 ? "" : $" ⎇ [{guardText}]";
        // Identical sibling edges collapsed under one parent (e.g. a generic method called once per
        // type-arg): show the call-site count rather than N repeated "⋯elided" lines.
        var calls = node.CallSites > 1 ? $" ×{node.CallSites} calls" : "";
        // Subtree not drawn here: this method was already expanded elsewhere (cycle / shared callee), or
        // a depth/budget cap was hit. "elided" states the consequence without implying a cycle (the
        // marker fires for all three), and reads unambiguously to a model parsing the tree.
        var elided = node.Truncated ? " ⋯elided" : "";
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
            {
                if (rule.IsMatch(node.SymbolId))
                {
                    matchedCut = rule;
                    break;
                }
            }

            if (matchedCut is not null)
            {
                cutTag = $" «cut: {matchedCut.Label}»";
            }
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
                declaringBinding: node.DeclaringTypeArgBinding,
                methodBinding: node.MethodTypeArgBinding,
                parentDeclaring: parentDeclaringConcrete,
                parentMethod: parentMethodConcrete
            );
        // A lambda renders AS its enclosing method when ShortName drops `~λN` (it does so only for a
        // PARAMETERFUL method — load-bearing for the generic-chain monomorphization above), so sibling
        // lambdas of one method look like identical duplicate lines. Append ONE `λN` discriminator. ShortName
        // KEEPS `~λN` for a PARAMETERLESS enclosing method, so strip that first or we'd double it
        // (`LoadBuiltIn~λ0 λ0`).
        var shortName = ShortName(node.SymbolId);
        var lambdaTag = "";
        var lambdaAt = node.SymbolId.IndexOf("~λ", StringComparison.Ordinal);
        if (lambdaAt >= 0)
        {
            var seg = node.SymbolId[lambdaAt..];
            var segParen = seg.IndexOf('(');
            lambdaTag = " " + (segParen >= 0 ? seg[..segParen] : seg).TrimStart('~');
            var keptAt = shortName.IndexOf("~λ", StringComparison.Ordinal);
            if (keptAt >= 0)
            {
                shortName = shortName[..keptAt];
            }
        }
        var name =
            PrettyGenericName(shortName, declaringArgs: declaringConcrete, methodArgs: methodConcrete)
            + (signatures ? ShortSignature(node.SymbolId) : "")
            + lambdaTag;
        // EP marker: when this node is itself a rule-detected entry point, wrap its name with "▶ kind"
        // and a trailing service chip — the same custom rendering used by derive/callers.
        var (epPrefix, epSuffix) = epContext?.ChipFor(node.SymbolId) ?? ("", "");
        // --full: the reaching edge's call site (where the PARENT calls this node) — a `new X()` line for a
        // ctor, the inline-lambda decl line, the call line for a method — so ctors/lambdas and every node get
        // a source line, not just the effect/library leaves. Trailing so SourceLocDedupWriter dedups it in
        // print order; the root has no reaching edge (CallFile null).
        var callLoc = full && !string.IsNullOrEmpty(node.CallFile) ? $"  {ShortenPath(node.CallFile)}:{node.CallLine}" : "";
        // `--hazards`: the inline pattern-finding marker for this method (empty when unmarked). Placed before
        // the source-loc suffixes so SourceLocDedupWriter's trailing-loc regex still matches the line.
        var hazard = hazardsByMethod is not null && hazardsByMethod.TryGetValue(node.SymbolId, out var hz) ? hz : "";
        var label =
            $"{epPrefix}{name}{dispatch}{handoff}{loop}{guardTag}{calls}{elided}{opaqueTag}{cutTag}{fx}{hazard}{loc}{epSuffix}{callLoc}";
        output.WriteLine(isRoot ? label : $"{prefix}{Connector(isLast)}{label}");

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
                output.WriteLine($"{childPrefix}{Connector(lastLeaf)}{fxLeaves[i]}");
            }
        }

        if (opaque is not null)
        {
            return;
        }

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
            var fxUnion = effects.Count == 0 ? "" : "  " + string.Join(' ', shown) + overflow;
            output.WriteLine(
                $"{childPrefix}{Connector(last: true)}⋯ {children.Count} dispatch targets collapsed [seam: {seam.Label}]{fxUnion}  (+{hidden} lines hidden — `tree --raw` to expand)"
            );
            return;
        }

        for (var i = 0; i < children.Count; i++)
        {
            RenderTreeNode(
                children[i],
                childPrefix,
                isLast: i == children.Count - 1,
                isRoot: false,
                effectsByMethod: effectsByMethod,
                prune: prune,
                renderRules: renderRules,
                seamEffects: seamEffects,
                output: output,
                files: files,
                locById: locById,
                signatures: signatures,
                plain: plain,
                cutRules: cutRules,
                epContext: epContext,
                full: full,
                effectLeavesByMethod: effectLeavesByMethod,
                parentDeclaringConcrete: declaringConcrete,
                parentMethodConcrete: methodConcrete,
                hazardsByMethod: hazardsByMethod,
                guards: guards
            );
        }
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
        {
            return result;
        }

        var hubs = new HashSet<string>(StringComparer.Ordinal);
        void FindHubs(TraceNode node)
        {
            if (renderRules.MatchCollapseSeam(node.SymbolId) is not null)
            {
                hubs.Add(node.SymbolId);
            }

            foreach (var child in node.Children)
            {
                FindHubs(child);
            }
        }
        foreach (var root in roots)
        {
            FindHubs(root);
        }

        foreach (var hub in hubs)
        {
            var reach = FactPathFinder.ReachesWithFanout(graph, hub, maxDepth, mode: mode);
            // Distinct resource types per (provider, operation) over the whole reach closure.
            var perOp = new Dictionary<(string Provider, string Operation), HashSet<string>>();
            foreach (var sym in reach.Keys)
            {
                if (structuredByMethod.TryGetValue(sym, out var list))
                {
                    foreach (var effect in list)
                    {
                        if (!perOp.TryGetValue((effect.Provider, effect.Operation), out var resources))
                        {
                            perOp[(effect.Provider, effect.Operation)] = resources = new HashSet<string>(StringComparer.Ordinal);
                        }

                        resources.Add(effect.ResourceType);
                    }
                }
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
            {
                foreach (var effect in list)
                {
                    if (seen.Add(effect))
                    {
                        effects.Add(effect);
                    }
                }
            }

            var kids = prune ? node.Children.Where(c => SubtreeHasEffect(c, effectsByMethod)) : node.Children;
            foreach (var child in kids)
            {
                Walk(child);
            }
        }

        foreach (var node in nodes)
        {
            Walk(node);
        }

        return (effects, count);
    }

    // Formats the raw effect group for one method into display strings, applying three transforms:
    // (1) lock:acquire+release pairs on the same resource → single "🔒 lock [resource]" entry
    //     (the pair is always emitted together and adds no information individually);
    //     if the sole resource is Threading.Monitor the resource name is omitted (always the same).
    // (2) identical rendered strings → deduplicated with a "×N" suffix.
    // (3) all effects are returned as individual strings; the caller joins them inside one {…} block.
    internal static List<string> FormatEffectGroup(IEnumerable<DerivedEffect> effects, IReadOnlyDictionary<string, string> emoji)
    {
        var list = effects.ToList();

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

        foreach (var resource in pairedResources.OrderBy(r => r, StringComparer.OrdinalIgnoreCase))
        {
            var lockEmoji = EmojiLookup.For(emoji, provider: "lock", operation: "held");
            // Omit resource name when it is always Threading.Monitor — adds no information.
            var resourceLabel = resource.Contains("Threading.Monitor", StringComparison.OrdinalIgnoreCase) ? "" : $" {ShortName(resource)}";
            result.Add($"{lockEmoji} lock{resourceLabel}");
        }

        foreach (var e in list)
        {
            var isPaired =
                pairedResources.Contains(e.ResourceType ?? "")
                && (e.Provider == "lock" && (e.Operation == "acquire" || e.Operation == "release"));
            if (isPaired)
            {
                continue;
            }

            var glyph = EmojiLookup.For(emoji, provider: e.Provider, operation: e.Operation);
            result.Add($"{glyph} {e.Provider}:{e.Operation} {ShortName(e.ResourceType)}");
        }

        // Dedup: collapse identical strings to "label ×N".
        return result.GroupBy(s => s, StringComparer.Ordinal).Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key).ToList();
    }

    // `tree --full`: one effect rendered as its OWN provenance leaf body — glyph + provider:op + resource +
    // the producing call site (file:line) — instead of the compact inline {…} tag the other modes hoist
    // onto the enclosing method. The caller orders a method's leaves by source line.
    internal static string FormatEffectLeaf(DerivedEffect e, IReadOnlyDictionary<string, string> emoji)
    {
        var loc = string.IsNullOrEmpty(e.FilePath) ? "" : $"  {ShortenPath(e.FilePath)}:{e.Line}";
        var glyph = EmojiLookup.For(emoji, provider: e.Provider, operation: e.Operation);
        // A guarded effect (e.g. a db:write under `if`, a guarded `throw`) carries its control-dependence
        // condition; mark it with ⎇ like a resolved call edge. Empty (must-run) → no glyph.
        var guardText = ShortGuards(e.EnclosingGuards);
        var guardTag = guardText.Length == 0 ? "" : $" ⎇ [{guardText}]";
        return $"{glyph} {e.Provider}:{e.Operation} {ShortName(e.ResourceType)}{guardTag}{loc}";
    }

    // `tree --full`: a library call that produced NO effect (resolved to a referenced-assembly target, but
    // no rule matched it). Rendered as a dim leaf (· marker) so the call is visible without implying an
    // effect — distinct from the glyph-prefixed effect leaves above.
    internal static string FormatUnresolvedLeaf(string target, string? filePath, int line, string? encodedGuards = null)
    {
        var loc = string.IsNullOrEmpty(filePath) ? "" : $"  {ShortenPath(filePath)}:{line}";
        var name = ShortName(target);
        // ShortName keeps a leading DocID kind prefix ("M:"/"T:"/…) for a namespace-less symbol; strip it.
        if (name.Length > 2 && name[1] == ':')
        {
            name = name[2..];
        }

        // A guarded library call (e.g. a switch-arm or if-gated BCL call) carries its control-dependence
        // condition; mark it with the ⎇ glyph, same as a resolved call edge. Empty (must-run) → no glyph.
        var guardText = ShortGuards(encodedGuards);
        var guardTag = guardText.Length == 0 ? "" : $" ⎇ [{guardText}]";

        // Render generic arity the same way resolved tree nodes do (`Seq`1.Iter` -> `Seq<T>.Iter`) so the
        // library leaves don't show raw backtick arity next to the `<T,U>` of resolved siblings.
        return $"· {PrettyGenericName(name)}{guardTag}{loc}";
    }
}

// Print-order source-loc dedup for the tree. Nodes/leaves carry a trailing source location
// (`  <relpath>:<line>`, or the `--files` definition form `  📄 <relpath>:<line>`), and consecutive lines
// usually share a file, so the path is re-printed on nearly every line. This wraps the tree's output and
// rewrites the path to nothing — `  :<line>` / `  📄 :<line>` — when it is unchanged from the previously
// written line, so the file name appears only when it CHANGES, in print order. MODE-AGNOSTIC by design: it
// keys off the rendered location, not on which flag produced it (--files/--full/--raw), so every loc dedups
// through one filter with no per-flag matrix. Display-only; line numbers and the `📄` marker are preserved.
// One instance per forest so the cursor spans every root.
internal sealed class SourceLocDedupWriter(TextWriter inner) : TextWriter
{
    // Trailing "  [📄 ]<relpath>:<line>": the path must contain '/' (ShortenPath emits forward-slash
    // relpaths) and no ':' before the line number, so resources like "Data.RunSummary"/"<anon>" never match.
    // The optional 📄 prefix is the --files definition-loc marker, captured so it survives the rewrite.
    private static readonly Regex LocSuffix = new(@"  (?<icon>📄 )?(?<p>[^\s:]+/[^\s:]+):(?<l>\d+)$", RegexOptions.CultureInvariant);

    private string? _lastPath;

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value) => inner.Write(value);

    public override void Write(string? value) => inner.Write(value);

    public override void Flush() => inner.Flush();

    public override void WriteLine(string? value) => inner.WriteLine(value is null ? null : Dedup(value));

    private string Dedup(string line)
    {
        var m = LocSuffix.Match(line);
        if (!m.Success)
        {
            return line;
        }

        var path = m.Groups["p"].Value;
        if (string.Equals(path, _lastPath, StringComparison.Ordinal))
        {
            // Drop the repeated path, keep the marker (📄 or none) and the line number.
            return $"{line[..m.Index]}  {m.Groups["icon"].Value}:{m.Groups["l"].Value}";
        }

        _lastPath = path;
        return line;
    }
}
