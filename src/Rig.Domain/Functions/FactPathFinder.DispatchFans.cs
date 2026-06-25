using Rig.Domain.Data;

namespace Rig.Domain.Functions;

public static partial class FactPathFinder
{
    // ---- rig dispatch-fans: an UN-NARROWED dispatch-fan worklist (read-only DIAGNOSTIC) -------------
    //
    // Dispatch resolution over-approximates at "god-seam" methods: a base/virtual/interface method B with
    // N mined overrides (e.g. EntityBase.Delete, 97) fans to all N targets UNLESS the call edge's receiver
    // type narrows it (DispatchTargets -> NarrowByReceiver -> ResolveNarrowRoot). Narrowing FAILS — falling
    // back to the full CHA cone — when the receiver is absent, a generic type-parameter, the declaring base
    // itself, or unbound (cross-assembly / heuristic). Those un-narrowed fans are often a HYPOTHESIS that a
    // rule or entry-point definition is missing the receiver/binding.
    //
    // This report classifies every DISPATCH SITE — a real call edge `caller -(R)-> B` whose receiver-BLIND
    // fan DispatchTargets(B, null) has >= 2 targets — by whether the edge's own receiver R narrowed it, and
    // if not WHY. It changes NOTHING in traversal; it only re-runs DispatchTargets to measure the fan. The
    // narrowing it measures mirrors the forward walk exactly (an index with NarrowDispatch=true, the same
    // receiver string the edge carries), so a "narrowed" verdict here matches what reaches/tree/path do.

    // Why an un-narrowed dispatch edge stayed at full CHA (first match wins). The two ACTIONABLE causes are
    // a likely-recoverable receiver/binding; the two IRREDUCIBLE causes are intentional (a base.M()/base-typed
    // call legitimately hits any override; an external/unbound receiver can't be resolved without the assembly).
    public static class DispatchFanCauses
    {
        public const string AbsentReceiver = "absent-receiver";
        public const string BaseTypedReceiver = "base-typed-receiver";
        public const string TypeParameter = "type-parameter";
        public const string ExternalOrUnbound = "external-or-unbound";
    }

    // One hub (a dispatch source B) with an un-narrowed fan, aggregated over the call edges into it.
    //   ResidualFan    = the receiver-blind fan DispatchTargets(B, null).Count (the over-approximation size).
    //   IncomingEdges  = count of un-narrowed call edges into B (edges whose receiver reduced nothing).
    //   Cause counts   = per-cause breakdown of those un-narrowed edges.
    //   Actionable     = any un-narrowed edge has cause absent-receiver or type-parameter (a likely-missing
    //                    rule/EP def to capture the receiver/binding); else irreducible.
    // Ranked by Rank = ResidualFan * IncomingEdges (descending) by the caller.
    public sealed record DispatchFanRow(
        string Hub,
        int ResidualFan,
        int IncomingEdges,
        int AbsentReceiver,
        int BaseTypedReceiver,
        int TypeParameter,
        int ExternalOrUnbound,
        bool Actionable
    )
    {
        public long Rank => (long)ResidualFan * IncomingEdges;
    }

    // Builds the ranked un-narrowed dispatch-fan worklist over the whole graph. Pure measurement: re-runs
    // DispatchTargets (memoised) per edge, never mutates the graph or affects any traversal. The returned
    // list is sorted by Rank desc, then ResidualFan desc, then Hub ordinal (a stable total order).
    public static IReadOnlyList<DispatchFanRow> DispatchFanReport(FactGraphData graph)
    {
        // NarrowDispatch=true mirrors the forward traversal: DispatchTargets only applies NarrowByReceiver/
        // ContextFamily/TypeArguments when the index says narrowing is on, so a blind/narrowed comparison
        // here measures the SAME fan the real walk would.
        var index = BuildIndex(graph, narrowDispatch: true);

        // Memoise DispatchTargets by (hub, stripped-receiver-or-null-sentinel) — same precedent as
        // BuildReverseMaps: a god-seam has few distinct receivers across thousands of edges.
        const string nullReceiverSentinel = "\0null";
        var fanMemo = new Dictionary<(string Hub, string ReceiverKey), int>();

        int FanCount(string hub, string? receiver)
        {
            var stripped = string.IsNullOrEmpty(receiver) ? null : ReceiverToStrippedTypeId(receiver!);
            var key = (hub, stripped ?? nullReceiverSentinel);
            if (!fanMemo.TryGetValue(key, out var count))
            {
                fanMemo[key] = count = DispatchTargets(method: hub, index: index, receiverType: stripped is null ? null : receiver).Count;
            }

            return count;
        }

        // Per-hub accumulators for the un-narrowed edges into it.
        var hubs = new Dictionary<string, FanAccumulator>(StringComparer.Ordinal);

        foreach (var edge in graph.CallEdges)
        {
            // A `base.M()` edge binds to exactly the base body and is never re-dispatched (one-hop / NonVirtual),
            // so it is not a dispatch SITE — skip it (mirrors Successors / BuildReverseMaps).
            if (edge.NonVirtual)
            {
                continue;
            }

            // Only handoff edges that the traversal would NOT cross are non-sites; but the dispatch fan of a
            // call edge is receiver-driven regardless of mode, and a dispatch SITE is a real call. Handoff
            // edges carry no dispatch fan-out in Successors (they yield Via=null), so skip them outright.
            if (edge.Kind == EdgeKinds.Handoff)
            {
                continue;
            }

            // Cut-aware: a hub that is a configured traversal-cut seam (e.g. the ProvideService<T> /
            // CreateService service-locator) NEVER expands its dispatch fan in reaches/tree/path — Successors
            // `yield break`s on a cut node BEFORE emitting dispatch — so its raw fan does not pollute real
            // reachability and must NOT appear in the worklist. Likewise a cut CALLER never has its edges
            // walked. Mirrors Successors (IsTraversalCut) / Predecessors. Without this the report over-reports
            // already-handled seams (ProvideService``1 topped the list at fan 5 × 980 though it is fully cut).
            if (index.ApplyTraversalCuts && (index.IsTraversalCut(edge.Callee) || index.IsTraversalCut(edge.Caller)))
            {
                continue;
            }

            var blindFan = FanCount(hub: edge.Callee, receiver: null);
            if (blindFan < 2)
            {
                continue; // not a dispatch site (single concrete target or a real leaf call)
            }

            var narrowedFan = FanCount(hub: edge.Callee, receiver: edge.ReceiverType);
            if (narrowedFan != blindFan)
            {
                continue; // the receiver reduced the fan — narrowed, not a problem
            }

            // Un-narrowed: classify WHY (first match wins).
            var cause = ClassifyCause(hub: edge.Callee, receiver: edge.ReceiverType, index: index);

            if (!hubs.TryGetValue(edge.Callee, out var acc))
            {
                hubs[edge.Callee] = acc = new FanAccumulator { ResidualFan = blindFan };
            }

            acc.IncomingEdges++;
            switch (cause)
            {
                case DispatchFanCauses.AbsentReceiver:
                    acc.AbsentReceiver++;
                    break;
                case DispatchFanCauses.BaseTypedReceiver:
                    acc.BaseTypedReceiver++;
                    break;
                case DispatchFanCauses.TypeParameter:
                    acc.TypeParameter++;
                    break;
                default:
                    acc.ExternalOrUnbound++;
                    break;
            }
        }

        var rows = hubs.Select(kv => new DispatchFanRow(
                Hub: kv.Key,
                ResidualFan: kv.Value.ResidualFan,
                IncomingEdges: kv.Value.IncomingEdges,
                AbsentReceiver: kv.Value.AbsentReceiver,
                BaseTypedReceiver: kv.Value.BaseTypedReceiver,
                TypeParameter: kv.Value.TypeParameter,
                ExternalOrUnbound: kv.Value.ExternalOrUnbound,
                Actionable: kv.Value.AbsentReceiver > 0 || kv.Value.TypeParameter > 0
            ))
            .OrderByDescending(r => r.Rank)
            .ThenByDescending(r => r.ResidualFan)
            .ThenBy(r => r.Hub, StringComparer.Ordinal)
            .ToList();

        return rows;
    }

    private sealed class FanAccumulator
    {
        public int ResidualFan;
        public int IncomingEdges;
        public int AbsentReceiver;
        public int BaseTypedReceiver;
        public int TypeParameter;
        public int ExternalOrUnbound;
    }

    // Why this un-narrowed edge stayed at full CHA (first match wins). `receiver` is the call edge's
    // ReceiverType; `hub` is the dispatch source (the callee B). `index` is the same one DispatchFanReport
    // measured with. Uses the SAME private resolvers the traversal uses (ReceiverToStrippedTypeId /
    // ResolveNarrowRoot / ParseMethod) so the classification agrees with WHY narrowing actually failed.
    private static string ClassifyCause(string hub, string? receiver, GraphIndex index)
    {
        if (string.IsNullOrEmpty(receiver))
        {
            return DispatchFanCauses.AbsentReceiver;
        }

        var stripped = ReceiverToStrippedTypeId(receiver!);
        var declaringTypeId = ParseMethod(hub)?.TypeId;

        // The receiver IS the declaring base type (a base/base.M()-typed receiver) — CHA is intentional here.
        if (stripped is not null && declaringTypeId is not null)
        {
            var declStripped = TypeClosure.StripGeneric(declaringTypeId);
            if (string.Equals(stripped, declStripped, StringComparison.Ordinal))
            {
                return DispatchFanCauses.BaseTypedReceiver;
            }
        }

        // The receiver didn't resolve to a narrowing root. Split type-parameter-shaped (e.g. "TEntity":
        // unnamespaced, no '.', not a known node/type in the index) from genuinely external/unbound.
        var resolves =
            declaringTypeId is not null
            && ResolveNarrowRoot(receiverType: receiver, declaringTypeId: declaringTypeId, index: index) is not null;
        if (!resolves && LooksLikeTypeParameter(receiver!, index))
        {
            return DispatchFanCauses.TypeParameter;
        }

        return DispatchFanCauses.ExternalOrUnbound;
    }

    // Heuristic (v1): a receiver looks like a generic type PARAMETER (e.g. "TEntity", "T", "TConstruct")
    // when its bare name has no '.' (unnamespaced) and the index knows no type by that name — i.e. it is
    // neither a node nor a type with methods in the index. A namespaced receiver (has '.') is external/
    // unbound, not a type parameter.
    private static bool LooksLikeTypeParameter(string receiver, GraphIndex index)
    {
        var bare = RemoveGenericArguments(receiver);
        if (bare.IndexOf('.') >= 0)
        {
            return false;
        }

        // Known to the index as a real type? Then it's not a type parameter (it bound to something).
        var stripped = ReceiverToStrippedTypeId(receiver);
        if (stripped is not null && index.MethodsByStrippedType.ContainsKey(stripped))
        {
            return false;
        }

        return true;
    }
}
