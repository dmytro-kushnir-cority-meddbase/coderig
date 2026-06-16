using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static partial class FactPathFinder
{
    // Direct call edges + the interface->concrete DI dispatch hop (single shared definition so
    // Find and Reaches traverse identically). Each dispatch edge carries the FAN-OUT DEGREE of its
    // source method — the total number of dispatch targets the virtual/base method resolves to
    // (impl-dispatch + override-dispatch) — so a `base.M()` that explodes to all N overrides is
    // distinguishable from a single concrete dispatch (degree 1) and from a real call (degree 0).
    // Direct call edges are degree 0. (A1/D3.)
    //
    // Dispatch is resolved EDGE-AWARE: it is computed per direct call edge `current -(R)-> callee`,
    // narrowed by that edge's receiver type R (CallEdge.ReceiverType) when narrowing is on. The
    // dispatch SOURCE attributed to the fanned-out targets is the `callee` (the virtual/base method),
    // matching the prior per-node model where the same fan-out was attributed to the virtual method.
    private static IEnumerable<(
        string Node,
        string Kind,
        string? File,
        int Line,
        string? LoopKind,
        string? LoopDetail,
        int Fanout,
        // The dispatch SOURCE method when this is a fanned-out dispatch edge (the virtual/base/interface
        // method `current` itself, whose call fanned out) — the DispatchVia tag for the reached node.
        // Null for direct call edges. Set even when degree==1 (harmless; fan-out tagging fires only >1).
        string? Via,
        // The static receiver type to carry forward to the TARGET node, so that when the target is later
        // expanded its own dispatch fan-out can be narrowed edge-aware. For a direct call edge this is
        // the edge's ReceiverType (the receiver of `target` at that call site). Null for dispatch hops
        // (a dispatch edge has no further call-site receiver).
        string? OutReceiver,
        // The dispatcher id when THIS successor is reached via an async handoff edge (Kind=="handoff"),
        // else null — the HandoffVia provenance seed for the reached node. Only produced under
        // AsyncInclude (SyncCut skips the edge entirely).
        string? HandoffVia,
        // Provenance of a dispatch edge: "roslyn" (exact mined fact) or "heuristic" (name/arity CHA
        // fallback — flagged to the user). Null for direct call / handoff edges.
        string? Basis,
        // The concrete generic type-arg binding to carry to the TARGET node (parallel to OutReceiver):
        // the incoming binding extended with this edge's own concrete type args. Drives generic-dispatch
        // narrowing when the target is later expanded. Reference-equal to the incoming binding when the
        // edge adds no concrete type args (the common case — most edges forward type parameters or are
        // non-generic), so threading it costs no allocation on those.
        IReadOnlyCollection<string>? OutBinding,
        // THIS edge's generic monomorphization bindings (CallEdge.DeclaringTypeArgBinding /
        // MethodTypeArgBinding) — forwarded onto the reached node for RENDERING only (TraceNode label
        // substitution). Null for dispatch hops (no call-site) and non-generic callees.
        string? OutDeclaringBinding,
        string? OutMethodBinding
    )> Successors(
        string current,
        GraphIndex index,
        string? incomingReceiver = null,
        IReadOnlyCollection<string>? incomingBinding = null,
        TraversalMode mode = TraversalMode.SyncCut
    )
    {
        // Traversal cut: if the current node matches a cut rule, emit no successors — it is a leaf.
        // The node itself was already emitted by the caller; we just stop walking into it here.
        if (index.ApplyTraversalCuts && index.IsTraversalCut(current))
            yield break;

        // Emit direct call edges in CALL-SITE SOURCE ORDER (by line, then callee for stable ties), not
        // storage order. The graph is loaded from SQL with no ORDER BY, so adjacency order is arbitrary
        // and non-deterministic; C# executes calls eagerly inline, so line order is a good approximation
        // of execution order and makes tree/path/reaches read top-to-bottom and reproduce deterministically.
        // (Approximation only: branches/loops/early-return mean lexical order != runtime order.) Each edge
        // carries its ReceiverType forward so the target's dispatch can be narrowed when it is expanded.
        if (index.Adjacency.TryGetValue(current, out var edges))
            foreach (var edge in edges.OrderBy(e => e.Line).ThenBy(e => e.Callee, StringComparer.Ordinal))
            {
                // Sync-cut: an async handoff edge schedules its callback to run later / elsewhere — it
                // is NOT a synchronous call, so we don't cross it. --async crosses it, seeding the
                // reached node with the dispatcher provenance (HandoffVia).
                // Extend the carried binding with THIS edge's concrete type args (a forwarded type
                // parameter / non-generic call adds nothing and returns the same set reference).
                var outBinding = ExtendBinding(incomingBinding, edge.TypeArguments);
                if (edge.Kind == EdgeKinds.Handoff)
                {
                    if (mode == TraversalMode.SyncCut)
                        continue;
                    yield return (
                        edge.Callee,
                        edge.Kind,
                        edge.FilePath,
                        edge.Line,
                        edge.LoopKind,
                        edge.LoopDetail,
                        0,
                        null,
                        edge.ReceiverType,
                        edge.HandoffDispatcher ?? "handoff",
                        null,
                        outBinding,
                        edge.DeclaringTypeArgBinding,
                        edge.MethodTypeArgBinding
                    );
                    continue;
                }
                yield return (
                    edge.Callee,
                    edge.Kind,
                    edge.FilePath,
                    edge.Line,
                    edge.LoopKind,
                    edge.LoopDetail,
                    0,
                    null,
                    ContextControllerCarry(incomingReceiver, edge, index) ?? PropagateReceiver(incomingReceiver, edge, index),
                    null,
                    null,
                    outBinding,
                    edge.DeclaringTypeArgBinding,
                    edge.MethodTypeArgBinding
                );
            }

        // Dispatch (synthetic, no call-site line) edges AFTER the line-ordered real calls — the fan-out
        // of `current` itself when it is a virtual/base/interface method, narrowed by the receiver of the
        // call that REACHED current (edge-aware) AND by the carried type-arg binding (generic-dispatch
        // narrowing). Tagged with the group's fan-out degree (N) and attributed to `current` (the dispatch
        // source). The dispatch hop adds no call-site type args, so targets inherit `current`'s binding.
        var dispatch = DispatchTargets(
            current,
            index,
            index.NarrowDispatch ? incomingReceiver : null,
            index.NarrowDispatch ? incomingBinding : null
        );
        var degree = dispatch.Count;
        // Dispatch SEEDS the concrete `this`-type for the target frame: resolving a virtual/interface call
        // to `Bar.M` means the object IS a `Bar`, so the method runs with `this` : Bar — seed that so the
        // dispatched-to method's own `this`-virtual self-calls narrow (collapsing post-impl-dispatch
        // fan-outs, e.g. `Master.SetInvoiceSettings` whose inner `this.ProvideRoles()` would otherwise
        // CHA-fan to every workflow Master). The seed is the MORE-DERIVED of the incoming concrete receiver
        // and the target's declaring type: when the receiver that reached `current` is a subtype of the
        // override's declaring type (a virtual call on a concrete object that INHERITS the override — e.g.
        // a specific Cache reaching the inherited Cache.GetResults), that concrete type is the real `this`,
        // so carrying it lets the NEXT dispatch (Cache.GetResult) narrow instead of seeding the less-derived
        // declaring type and CHA-fanning. Falls back to the declaring type for interface/abstract dispatch
        // where the incoming receiver isn't a concrete subtype (the original behaviour).
        foreach (var d in dispatch)
            yield return (
                d.Node,
                d.Kind,
                null,
                0,
                null,
                null,
                degree,
                current,
                SeedReceiver(incomingReceiver, d.Node, index),
                null,
                d.Basis,
                incomingBinding,
                null,
                null
            );
    }

    // The `this`-type to carry into a dispatched-to method `targetMethod`: the incoming concrete receiver
    // when it is the override's declaring type or a SUBTYPE of it (the precise runtime object, more derived
    // than the declaring type), otherwise the target's declaring type. Keeps the existing declaring-type
    // seed for interface/abstract dispatch (incoming receiver null / an interface / an unrelated type).
    private static string? SeedReceiver(string? incomingReceiver, string targetMethod, GraphIndex index)
    {
        var declDisplay = DeclaringTypeDisplay(targetMethod);
        if (incomingReceiver is null || declDisplay is null)
            return declDisplay;
        var inStripped = ReceiverToStrippedTypeId(incomingReceiver);
        var declStripped = ReceiverToStrippedTypeId(declDisplay);
        if (inStripped is null || declStripped is null)
            return declDisplay;
        return AncestorOrEqual(declStripped, inStripped, index) ? incomingReceiver : declDisplay;
    }

    // The display-FQN form of a method's declaring type ("M:Ns.Bar.M(..)" -> "Ns.Bar"), as ResolveNarrowRoot
    // / ReceiverToStrippedTypeId expect (no "T:" prefix; generic markers stripped downstream). Null for a
    // non-method id. Used to seed the dispatch target's concrete `this`-type as the carried receiver.
    private static string? DeclaringTypeDisplay(string methodDocId)
    {
        var typeId = ParseMethod(methodDocId)?.TypeId;
        return typeId is null ? null : typeId.Substring(2);
    }

    // Extends a carried type-arg binding with the CONCRETE type args of one call edge. "Concrete" =
    // contains a '.' (a namespaced first-party/system type, e.g. "MedDBase.…Account"); a forwarded
    // type-PARAMETER token ("TConstruct") or a bare primitive ("int") has no '.' and is skipped — only
    // namespaced types can be a dispatch candidate's declaring type. Splits on the TOP-LEVEL comma so a
    // tuple/generic arg never mis-splits. Returns the same set reference when nothing concrete is added
    // (the overwhelmingly common case), so threading the binding allocates only on genuinely generic
    // concrete call sites.
    private static IReadOnlyCollection<string>? ExtendBinding(IReadOnlyCollection<string>? current, string? typeArguments)
    {
        if (string.IsNullOrEmpty(typeArguments) || typeArguments!.IndexOf('.') < 0)
            return current;

        HashSet<string>? extended = null;
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= typeArguments.Length; i++)
        {
            if (i < typeArguments.Length)
            {
                var c = typeArguments[i];
                if (c is '<' or '(' or '[')
                    depth++;
                else if (c is '>' or ')' or ']')
                    depth--;
                if (!(c == ',' && depth == 0) && i < typeArguments.Length)
                    continue;
            }
            var part = typeArguments.Substring(start, i - start).Trim();
            start = i + 1;
            // Only namespaced types can name a dispatch candidate's declaring type; tuples/primitives/
            // type-parameter tokens can't, so they're never useful as a binding and are skipped.
            if (part.IndexOf('.') >= 0 && part.IndexOf(' ') < 0 && part.IndexOf('(') < 0)
            {
                extended ??= current is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(current, StringComparer.Ordinal);
                extended.Add(part);
            }
        }
        return extended ?? current;
    }

    // RECEIVER-CONTEXT RECOVERY (1-level object sensitivity). The static receiver type mined onto a
    // call edge is the DECLARED type at that site — for a `base.M()` it's the base, for a `this.M()` it's
    // the enclosing type — which loses the CONCRETE type the current frame is actually running as. That
    // loss is exactly what makes a `this`-virtual self-call inside a base method fan out to every sibling
    // override under CHA (e.g. `WorkflowControllerBase.Initialise` → all N workflow `Controller`s, when
    // this object is only ever `InvoiceDebtChase.Controller`). The concrete type IS known — it was pinned
    // at the outer call that entered this object (`InstantiateController<Controller>` / `c.Initialise()`)
    // and carried in `incomingReceiver`. A self-call runs the callee on the SAME object, so its `this`-type
    // is the caller's `this`-type: propagate the carried concrete receiver across self-call edges instead
    // of overwriting it with the (base-typed) edge receiver. External calls (a different object) keep the
    // edge's own receiver. Pure precision — narrowing stays recall-safe (an unmatched receiver falls back
    // to full CHA in ResolveNarrowRoot), so no real target is dropped.
    private static string? PropagateReceiver(string? incomingReceiver, CallEdge edge, GraphIndex index) =>
        IsSelfCall(incomingReceiver, edge, index) ? incomingReceiver : edge.ReceiverType;

    // Context-bound dispatch carry: a call to a context-interface member (e.g. IWorkflowState.Register
    // Events) loses the enclosing controller — its receiver is the interface-typed field (this.State),
    // not `this`. But the carried concrete `this`-type IS the controller. When the carrier is a known
    // controller (a context-family key), carry IT forward as the receiver so the interface dispatch
    // narrows to the controller's own state family (DispatchTargets -> NarrowByContextFamily) instead of
    // every implementer. Returns null (defer to PropagateReceiver) when the rule doesn't apply — recall-
    // safe, since an unknown controller leaves the full CHA fan-out.
    private static string? ContextControllerCarry(string? incomingReceiver, CallEdge edge, GraphIndex index)
    {
        if (incomingReceiver is null || index.StateFamilyByController.Count == 0)
            return null;
        var calleeType = ParseMethod(edge.Callee)?.TypeId;
        if (calleeType is null || !index.IsContextInterface(calleeType))
            return null;
        var controllerKey = ReceiverToStrippedTypeId(incomingReceiver);
        return controllerKey is not null && index.StateFamilyByController.ContainsKey(controllerKey) ? incomingReceiver : null;
    }

    // True when `edge` is a call on the SAME object as the carrying frame (`this.M()` / `base.M()` /
    // implicit-`this` `M()`), given the carried concrete this-type `incomingReceiver`. Requires a known
    // concrete carried type to be meaningful (else there is nothing better to propagate). For an explicit
    // receiver, self iff the edge's (declared) receiver is an ancestor-or-equal of the concrete this-type
    // (the `this`/`base` shape) — a sibling/unrelated/more-derived-downcast receiver is an external call.
    // For a bare receiver (implicit this OR a static call), self iff the callee is declared on the carried
    // this-type's own hierarchy — so a bare static call into an unrelated type is correctly NOT self.
    private static bool IsSelfCall(string? incomingReceiver, CallEdge edge, GraphIndex index)
    {
        var inThis = incomingReceiver is null ? null : ReceiverToStrippedTypeId(incomingReceiver);
        if (inThis is null)
            return false;

        if (!string.IsNullOrEmpty(edge.ReceiverType))
        {
            var edgeRecv = ReceiverToStrippedTypeId(edge.ReceiverType!);
            return edgeRecv is not null && AncestorOrEqual(edgeRecv, inThis, index);
        }

        var calleeType = ParseMethod(edge.Callee)?.TypeId;
        if (calleeType is null)
            return false;
        var calleeStripped = TypeClosure.StripGeneric(calleeType);
        return AncestorOrEqual(calleeStripped, inThis, index) || AncestorOrEqual(inThis, calleeStripped, index);
    }

    // True when `ancestor` == `descendant`, or `descendant` is a transitive base-edge subtype of
    // `ancestor`. Both are stripped "T:Ns.Type" ids; the stripped-aware descendant checks mirror
    // ResolveNarrowRoot so instantiated subtype edges (Foo{X}) still match their open-generic form.
    private static bool AncestorOrEqual(string ancestor, string descendant, GraphIndex index)
    {
        if (string.Equals(ancestor, descendant, StringComparison.Ordinal))
            return true;
        return Descendants(ancestor, index).Contains(descendant) || DescendantsContainStripped(ancestor, descendant, index);
    }

    // All synthetic dispatch successors of `method` (a virtual/base/interface method node), with the
    // PROVENANCE of each edge. Materialized (not lazily yielded) so the caller knows the fan-out
    // degree before emitting any edge. Deduped by target so a method reached via two mechanisms isn't
    // double-counted in the degree.
    //
    // Resolution order (exact facts first, heuristic only where Roslyn couldn't bind — flagged):
    //  1. MINED dispatch facts (Basis="roslyn"): the forward closure of the exact Roslyn-mined
    //     override/interface-impl edges from `method` (dispatch_facts; IMethodSymbol.OverriddenMethod +
    //     FindImplementationForInterfaceMember at extraction). Signature-exact and generic-correct —
    //     a same-named OVERLOAD can never be a target. Closure (not one hop) so a receiver narrowed to
    //     a grandchild type still finds the grandchild's override directly from the base method.
    //  2. Error-type simple-name recovery (Basis="heuristic", ALWAYS on): implementers whose interface
    //     edge failed to bind (`!:IFoo` — net48 partial binding) can have no mined edge by definition;
    //     recover them by simple name + arity. The single highest-recall feature — never dropped.
    //  3. Name/arity CHA fallback (Basis="heuristic"): ONLY when `method` has NO mined dispatch edges
    //     (Roslyn didn't see/bind it at extraction, or the store predates dispatch_facts) — the
    //     pre-mining interface-impl + override-descendant scan, arity-gated. ~99% correct; the CLI
    //     tells the user via the ~heuristic marker.
    //
    // `receiverType` is the static receiver type mined at the call site that reached `method` (a display
    // FQN, e.g. "MedDBase.CompanyEntity"). When it resolves to a concrete first-party type that is a
    // descendant of `method`'s declaring type, override/impl dispatch is NARROWED to that receiver's
    // own subtree — the precise CLR dispatch target set (orthogonal to WHICH member: it trims runtime
    // TYPES, the mined facts fix the member correspondence). Otherwise (null/interface/error-type/the
    // declaring base itself/an unknown type) it falls back to the full receiver-blind set.
    private static List<(string Node, string Kind, string Basis)> DispatchTargets(
        string method,
        GraphIndex index,
        string? receiverType = null,
        // Concrete generic type args in scope on the path that reached `method` (the carried binding).
        // When `method` is a GENERIC dispatch hub whose CHA fan-out includes the constructor/impl of one
        // of these types (e.g. `Construct`2.New` fanning to all entity constructors, with `Account` in
        // scope from `Entity.New<Account,…>` above), the fan-out is narrowed to that candidate. Recall-
        // safe: applied only when it leaves ≥1 target, else full CHA stands (so a hub reached without a
        // matching binding — the type flowed in by an uncaptured route — is never wrongly emptied).
        IReadOnlyCollection<string>? carriedBinding = null
    )
    {
        var targets = new List<(string Node, string Kind, string Basis)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // 18c delegate seam: a delegate SLOT (field/property/event) dispatches to its bound target(s)
        // via delegate_bind facts — the delegate-as-degenerate-interface hop. Resolved here, BEFORE the
        // method-parse, because a slot is a field/property/event DocID (not a method) and would
        // otherwise early-return. Multiple bindings -> multiple targets (like a multi-impl interface).
        if (index.MinedDispatchBySource.TryGetValue(method, out var bindings))
            foreach (var (target, kind) in bindings)
                if (kind == DispatchKinds.DelegateBind && seen.Add(target))
                    targets.Add((target, "delegate-dispatch", "roslyn"));

        var parsed = ParseMethod(method);
        if (parsed is null)
            return targets;

        // Source method's parameter ARITY — heuristic dispatch is gated on it so an interface/base
        // call only reaches an impl/override with the MATCHING signature, not a same-named OVERLOAD.
        // A true override/impl always shares the base/interface method's arity; an overload (e.g.
        // IWorkflows.Register(int, IWorkflowController) vs ...Register(IWorkflowMaster)) does NOT, so
        // name-only matching cross-contaminated them. Arity (not full param TYPES) is the safe gate:
        // it kills the cross-arity overload collision while staying robust to generic instantiation,
        // where an override's rendered param types legitimately differ from the open-generic base
        // (`0 vs int). Same-ARITY overloads are beyond it — that's what the mined facts resolve.
        var arity = ParamArity(method);

        // Resolve the receiver to a narrowing subtree, if it is reliable. `narrowRoot` non-null means
        // dispatch restricts to {narrowRoot} ∪ descendants(narrowRoot) instead of every candidate.
        var narrowRoot = ResolveNarrowRoot(receiverType, parsed.Value.TypeId, index);

        // Candidates are collected receiver-BLIND below; receiver-type devirtualization is applied once,
        // as a set, by NarrowByReceiver at the end (a per-candidate filter can't tell an INHERITED
        // ancestor override — keep — from a SHADOWED one — drop — since that is a property of the set).

        // 1. Mined facts: forward closure from `method`. hasMined reflects whether ANY mined edge
        // leaves the closure (pre-narrowing) — when true, the member correspondence is known exactly
        // and the CHA fallback (3) is suppressed; narrowing may still trim every target.
        var hasMined = false;
        if (index.MinedDispatchBySource.Count > 0)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { method };
            var stack = new Stack<string>();
            stack.Push(method);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!index.MinedDispatchBySource.TryGetValue(current, out var outs))
                    continue;
                foreach (var (target, kind) in outs)
                {
                    if (!visited.Add(target))
                        continue;
                    hasMined = true;
                    stack.Push(target); // walk the whole closure; NarrowByReceiver trims at the end
                    if (seen.Add(target))
                        targets.Add((target, kind == DispatchKinds.Impl ? "impl-dispatch" : "override-dispatch", "roslyn"));
                }
            }
        }

        void AddImplMethods(IEnumerable<string> impls)
        {
            foreach (var impl in impls)
            {
                if (!index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(impl), out var implMethods))
                    continue;
                foreach (var concrete in implMethods)
                    if (
                        string.Equals(concrete.Name, parsed.Value.Name, StringComparison.Ordinal)
                        && ParamArity(concrete.SymbolId) == arity
                        && seen.Add(concrete.SymbolId)
                    )
                        targets.Add((concrete.SymbolId, "impl-dispatch", "heuristic"));
            }
        }

        // 2. Simple-name fallback (always on): recover dispatch to implementers whose interface edge
        // failed to resolve (!:IFoo) by matching the call's interface simple name. Method-name-gated;
        // only consults error-type edges, so the blast radius is the partial-binding cases that would
        // otherwise be invisible — Roslyn never bound these, so no mined edge can cover them. (May
        // slightly over-dispatch across same-named interfaces in different namespaces — a deliberate
        // recall-over-precision trade for broken bindings.) Deduped against the mined set via `seen`.
        if (index.ImplsByErrorInterfaceName.TryGetValue(SimpleTypeName(parsed.Value.TypeId), out var nameImpls))
            AddImplMethods(nameImpls);

        // 3. Name/arity CHA fallback — only when the member has NO mined dispatch edge (residual
        // binding gaps + stores without dispatch_facts). Same scan as before mining existed.
        if (!hasMined)
        {
            // Interface -> concrete DI dispatch.
            if (index.ImplsByInterface.TryGetValue(parsed.Value.TypeId, out var impls))
                AddImplMethods(impls);

            // Base-virtual/abstract -> override dispatch (G6/G3): a call resolved to a base-type method
            // also reaches the SAME-named OVERRIDE on every (transitive) subtype. This is what makes an
            // abstract [ClientAction] (or framework virtual like OnSave) reach the effects in its
            // concrete override. Gated on IsOverride so it doesn't dispatch to unrelated same-named
            // (hidden) methods. Scan every descendant of the declaring base receiver-blind; NarrowByReceiver
            // trims to the receiver's dispatch line at the end (scanning the full set is what lets an
            // inherited ancestor override be found — it lives between the abstract base and the receiver).
            foreach (var sub in Descendants(parsed.Value.TypeId, index))
            {
                if (!index.MethodsByStrippedType.TryGetValue(TypeClosure.StripGeneric(sub), out var subMethods))
                    continue;
                foreach (var m in subMethods)
                    if (
                        m.IsOverride
                        && string.Equals(m.Name, parsed.Value.Name, StringComparison.Ordinal)
                        && ParamArity(m.SymbolId) == arity
                        && seen.Add(m.SymbolId)
                    )
                        targets.Add((m.SymbolId, "override-dispatch", "heuristic"));
            }
        }

        return NarrowByTypeArguments(
            NarrowByContextFamily(NarrowByReceiver(targets, narrowRoot, index), receiverType, parsed.Value.TypeId, index),
            carriedBinding,
            index
        );
    }

    // Context-bound dispatch narrowing (state-family): when `method` is a context-interface member and the
    // carried receiver is a known controller, keep only targets declared on a type in that controller's
    // bound family (e.g. an InvoiceDebtChase.Controller's IWorkflowState dispatch -> only InvoiceDebtChase
    // states, not the ~14 states across every workflow). Recall-safe: returns the input unchanged when no
    // rule applies, the receiver isn't a known controller, or the filter would empty the target set.
    private static List<(string Node, string Kind, string Basis)> NarrowByContextFamily(
        List<(string Node, string Kind, string Basis)> targets,
        string? receiverType,
        string methodDeclaringTypeId,
        GraphIndex index
    )
    {
        if (index.StateFamilyByController.Count == 0 || receiverType is null || targets.Count <= 1)
            return targets;
        if (!index.IsContextInterface(methodDeclaringTypeId))
            return targets;
        var controllerKey = ReceiverToStrippedTypeId(receiverType);
        if (controllerKey is null || !index.StateFamilyByController.TryGetValue(controllerKey, out var family))
            return targets;
        var filtered = targets.Where(t => ParseMethod(t.Node)?.TypeId is { } dt && family.Contains(NormType(dt))).ToList();
        return filtered.Count > 0 ? filtered : targets;
    }

    // Generic-dispatch narrowing (monomorphization): given a fanned-out candidate set and the concrete
    // type arguments in scope on the path, keep only candidates whose DECLARING TYPE is one of those
    // concretes (or a subtype) — `Construct`2.New` fanned to 43 entity constructors collapses to
    // `Account.New` when `Account` is the carried type arg from `Entity.New<Account,…>` above. The
    // concrete entity that the open generic `TConstruct` is bound to on this path picks its own
    // constructor out of the CHA over-approximation. Recall-safe: only applied when it keeps ≥1 target
    // (an unrelated/empty binding leaves the full set), and only when narrowing is enabled. Other carried
    // concretes (the pk type `int`, the record type) match no candidate and harmlessly drop out.
    private static List<(string Node, string Kind, string Basis)> NarrowByTypeArguments(
        List<(string Node, string Kind, string Basis)> targets,
        IReadOnlyCollection<string>? carriedBinding,
        GraphIndex index
    )
    {
        if (!index.NarrowDispatch || carriedBinding is not { Count: > 0 } || targets.Count <= 1)
            return targets;

        var roots = new List<string>();
        foreach (var t in carriedBinding)
            if (ReceiverToStrippedTypeId(t) is { } root)
                roots.Add(root);
        if (roots.Count == 0)
            return targets;

        var narrowed = new List<(string Node, string Kind, string Basis)>();
        foreach (var t in targets)
        {
            var declType = ParseMethod(t.Node)?.TypeId;
            if (declType is not null && roots.Any(r => InNarrowSubtree(declType, r, index)))
                narrowed.Add(t);
        }
        return narrowed.Count > 0 ? narrowed : targets;
    }

    // Resolves a call-site receiver type (a display FQN, e.g. "MedDBase.CompanyEntity"/"Ns.Foo<T>")
    // to the stripped type DocID to narrow override/impl dispatch to, or null to fall back to CHA.
    // Narrows ONLY when the receiver is a reliable concrete first-party type that is a STRICT
    // descendant of the declaring type `declaringTypeId` (so it is one of the real dispatch targets):
    //   * null/empty receiver, error-type, or interface  -> CHA (caller can't be pinned down),
    //   * the declaring base type itself                  -> CHA (no narrowing possible — the call
    //                                                         could hit any override),
    //   * a type with no methods in the index (unknown / not first-party) -> CHA (don't drop targets).
    // (Mirrors the existing recall-over-precision stance for broken bindings.)
    private static string? ResolveNarrowRoot(string? receiverType, string declaringTypeId, GraphIndex index)
    {
        if (string.IsNullOrEmpty(receiverType))
            return null;

        var stripped = ReceiverToStrippedTypeId(receiverType!);
        if (stripped is null)
            return null;

        var declaringStripped = TypeClosure.StripGeneric(declaringTypeId);

        // Receiver IS the declaring base type (or its stripped form): no narrowing — a `base.M()` or a
        // call typed as the base could dispatch to any override, so keep CHA.
        if (string.Equals(stripped, declaringStripped, StringComparison.Ordinal))
            return null;

        // The receiver must be a real CHA dispatch target of `declaringTypeId` to narrow to it:
        //  * a (transitive) base-edge descendant — the base-virtual/override case, OR
        //  * an implementer of the declaring INTERFACE (or a subtype of one) — the impl-dispatch case.
        // Being a descendant/implementer also confirms the receiver is a KNOWN FIRST-PARTY type (it
        // appears in the indexed type hierarchy). If it is neither, the binding is suspect (cross-
        // hierarchy simple-name collision, partial binding, an external type, etc.) — CHA is the safe
        // choice so no real override is dropped. (Note: we do NOT require the receiver type itself to
        // have methods in the closure — the bounded graph only loads methods inside the reach set, so a
        // pure intermediate base like ReferralEntityBase legitimately has none; its concrete override on
        // ReferralEntity is what matters and is found by scanning the narrowed subtree below.)
        var isBaseDescendant =
            Descendants(declaringTypeId, index).Contains(stripped) || DescendantsContainStripped(declaringTypeId, stripped, index);
        if (!isBaseDescendant && !ImplementsInterface(stripped, declaringTypeId, index))
            return null;

        return stripped;
    }

    // True when type `strippedType` implements the interface `interfaceTypeId` directly, or a base-edge
    // ancestor of it does (so a subtype of a declared implementer still narrows to the interface).
    private static bool ImplementsInterface(string strippedType, string interfaceTypeId, GraphIndex index)
    {
        if (!index.ImplsByInterface.TryGetValue(interfaceTypeId, out var impls))
            return false;
        foreach (var impl in impls)
        {
            var implStripped = TypeClosure.StripGeneric(impl);
            if (string.Equals(implStripped, strippedType, StringComparison.Ordinal))
                return true;
            // The receiver may be a subtype of a declared implementer.
            if (Descendants(impl, index).Contains(strippedType) || DescendantsContainStripped(impl, strippedType, index))
                return true;
        }
        return false;
    }

    private static bool DescendantsContainStripped(string declaringTypeId, string strippedReceiver, GraphIndex index)
    {
        foreach (var d in Descendants(declaringTypeId, index))
            if (string.Equals(TypeClosure.StripGeneric(d), strippedReceiver, StringComparison.Ordinal))
                return true;
        return false;
    }

    // Receiver-type devirtualization (applied to the receiver-blind candidate set as a whole): restrict
    // dispatch to the runtime targets for a receiver that narrows to `narrowRoot`.
    //   1. Keep candidates on the receiver's OWN subtree (root-or-descendant) — the receiver may be a
    //      base reference to a subtype, so any subtype's override is a possible runtime target. If any
    //      exist, that is the dispatch set (the receiver or a subtype overrides the member).
    //   2. Otherwise the receiver INHERITS the member — keep the NEAREST ancestor override (the one it
    //      actually runs), dropping SHADOWED further-up overrides and unrelated SIBLING branches (e.g. a
    //      CacheFunc-derived receiver inherits CacheFunc.GetResults, never the sibling Cache.GetResults).
    // Recall-safe: when neither yields a target (all candidates in unrelated branches — a suspect
    // binding) the full set stands. Pure class/interface DESCENDANT GEOMETRY — matches on type identity /
    // inheritance, never on generic type ARGUMENTS, so it is variance-agnostic (co/contravariance only
    // affects assignability of variant interface refs, not which override a concrete receiver runs).
    private static List<(string Node, string Kind, string Basis)> NarrowByReceiver(
        List<(string Node, string Kind, string Basis)> targets,
        string? narrowRoot,
        GraphIndex index
    )
    {
        if (narrowRoot is null || targets.Count <= 1)
            return targets;

        var subtree = targets.Where(t => ParseMethod(t.Node)?.TypeId is { } dt && InNarrowSubtree(dt, narrowRoot, index)).ToList();
        if (subtree.Count > 0)
            return subtree;

        var ancestors = targets.Where(t => ParseMethod(t.Node)?.TypeId is { } dt && AncestorOrEqual(dt, narrowRoot, index)).ToList();
        if (ancestors.Count == 0)
            return targets; // no candidate on the receiver's line at all — suspect binding, keep CHA

        // Among ancestor overrides, keep only the NEAREST to the receiver: one with no other ancestor
        // candidate strictly between it and the receiver (i.e. not a strict ancestor of another kept one).
        var nearest = ancestors
            .Where(a =>
                ParseMethod(a.Node)?.TypeId is { } ad
                && !ancestors.Any(b => ParseMethod(b.Node)?.TypeId is { } bd && IsStrictAncestor(ad, bd, index))
            )
            .ToList();
        return nearest.Count > 0 ? nearest : ancestors;
    }

    // True when `ancestorType` is a STRICT (proper) base-edge ancestor of `descendantType` — ancestor
    // of, and not the same (generic-stripped) type as, the descendant.
    private static bool IsStrictAncestor(string ancestorType, string descendantType, GraphIndex index) =>
        !string.Equals(TypeClosure.StripGeneric(ancestorType), TypeClosure.StripGeneric(descendantType), StringComparison.Ordinal)
        && AncestorOrEqual(ancestorType, descendantType, index);

    private static bool InNarrowSubtree(string typeId, string narrowRoot, GraphIndex index)
    {
        var stripped = TypeClosure.StripGeneric(typeId);
        if (string.Equals(stripped, narrowRoot, StringComparison.Ordinal))
            return true;
        foreach (var d in Descendants(narrowRoot, index))
            if (string.Equals(TypeClosure.StripGeneric(d), stripped, StringComparison.Ordinal))
                return true;
        return false;
    }

    // A receiver display FQN ("MedDBase.CompanyEntity", "Ns.Foo<T>") -> the generic-stripped type
    // DocID form ("T:MedDBase.CompanyEntity", "T:Ns.Foo") used as the MethodsByStrippedType / base-edge
    // key. Returns null for an error/unresolved receiver (a non-type display, e.g. one carrying "?"
    // or "<error>") so those degrade to CHA. Interfaces are NOT distinguishable here by name alone, so
    // an interface receiver that happens to resolve to a known type with no descendants simply narrows
    // to nothing extra (its own impls are found by the impl-dispatch path); the ResolveNarrowRoot
    // descendant check keeps a non-descendant interface receiver on the CHA path.
    private static string? ReceiverToStrippedTypeId(string receiver)
    {
        // Anonymous/error type (leading '<') -> CHA.
        if (receiver.Length == 0 || receiver[0] == '<')
            return null;

        // Remove generic argument LISTS at every nesting depth (balanced "<...>"), so a multi-arg
        // ("Foo<A, B>") or nested-generic ("Outer<A, B>.Inner") receiver normalises to its bare dotted
        // name ("Foo" / "Outer.Inner"). The display form renders type args as ", " (with a space) and
        // can carry a ".Inner" suffix AFTER the ">", so a naive space-reject or substring-to-first-'<'
        // both mishandled generic receivers — dropping narrowing for every generic type (e.g. the whole
        // CacheBase<T,R> hierarchy). Done BEFORE the shape checks so generic-internal spaces/brackets
        // (e.g. "Foo<int[], string>") don't disqualify an otherwise-named dispatch type.
        receiver = RemoveGenericArguments(receiver);

        // Reject the shapes that still aren't a first-party named dispatch type: nullable/pointer/by-ref/
        // tuple (a residual space) and arrays ('['). These degrade to CHA via a null narrow-root.
        if (
            receiver.Length == 0
            || receiver.IndexOf('?') >= 0
            || receiver.IndexOf('*') >= 0
            || receiver.IndexOf('[') >= 0
            || receiver.IndexOf(' ') >= 0
        )
            return null;

        // Strip the `n arity form too; the MethodsByStrippedType key is the generic-stripped DocID.
        receiver = TypeClosure.StripGeneric(receiver);
        return receiver.Length == 0 ? null : "T:" + receiver;
    }

    // Removes every balanced "<...>" generic-argument list from a type display name, at any nesting
    // depth, preserving the bare name and any nested-type suffix: "Outer<A, B>.Inner" -> "Outer.Inner".
    private static string RemoveGenericArguments(string type)
    {
        if (type.IndexOf('<') < 0)
            return type;
        var sb = new System.Text.StringBuilder(type.Length);
        var depth = 0;
        foreach (var c in type)
        {
            if (c == '<')
                depth++;
            else if (c == '>')
            {
                if (depth > 0)
                    depth--;
            }
            else if (depth == 0)
                sb.Append(c);
        }
        return sb.ToString();
    }
}
