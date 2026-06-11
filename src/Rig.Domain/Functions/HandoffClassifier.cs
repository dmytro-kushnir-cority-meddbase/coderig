using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Classifies dispatcher-consumed method-group call edges as async HANDOFFS (see
// docs/ASYNC-FLOW-PLAN.md). A `methodGroup` ref is already a distinct fact, but traversal walks it
// like an `invocation` — so `new RepeatingBackgroundProcessSchedule(ts, ProcessHealthcodeQueue, ..)`
// makes a `registrar -> ProcessHealthcodeQueue` edge that BFS crosses as if registration EXECUTED
// the callback (false synchronous reach). This rewrites the dispatcher-consumed subset to
// Kind="handoff" + HandoffDispatcher=<rule id>; the sync-cut traversal then skips them and --async
// walks them tagged. NOT every methodGroup is a handoff (`list.ForEach(Foo)` is synchronous), so only
// the curated handoffDispatchers set is split — never the reverse, or recall collapses.
//
// Generic infra; the dispatcher set is rule DATA (FactHandoffRule). Two matching paths, both keyed on
// the CONSUMER (the member the delegate is handed to):
//   * co-location — the consuming ctor/invocation edge sits at the SAME (Caller, FilePath, Line) as
//     the method-group edge (C# evaluates an argument expression at the call site), and its target
//     matches a dispatcher's ConsumerPatterns. This needs no extractor support and covers the whole
//     MedDBase dispatcher set (every dispatcher takes the delegate as a co-located argument).
// (An exact `DelegateConsumer` extractor fact — the consumer + arg index mined onto the methodGroup
// ref — would remove co-location's same-line requirement and catch `event +=`; deferred, see the
// handoff doc. Co-location is the primary path here.)
public static class HandoffClassifier
{
    // Returns the edge list with dispatcher-consumed method-group edges rewritten to Kind="handoff" +
    // HandoffDispatcher. Every other edge (incl. unmatched method-groups) is returned UNCHANGED — the
    // recall rail: only edges with a co-located dispatcher consumer are reclassified. Idempotent; a
    // no-op when `rules` is empty.
    public static IReadOnlyList<CallEdge> Classify(IReadOnlyList<CallEdge> edges, IReadOnlyList<FactHandoffRule>? rules)
    {
        if (rules is null || rules.Count == 0)
            return edges;

        // Index the consumer targets present at each call SITE (Caller, FilePath, Line) — the
        // invocation/ctor edges a co-located method-group could be an argument to.
        var consumersBySite = new Dictionary<(string, string, int), List<string>>();
        foreach (var e in edges)
        {
            if (e.Kind is not ("invocation" or "ctor"))
                continue;
            var key = (e.Caller, e.FilePath, e.Line);
            if (!consumersBySite.TryGetValue(key, out var list))
                consumersBySite[key] = list = new List<string>();
            list.Add(e.Callee);
        }

        var result = new List<CallEdge>(edges.Count);
        foreach (var e in edges)
        {
            if (e.Kind != "methodGroup")
            {
                result.Add(e);
                continue;
            }

            var match = MatchAtSite(consumersBySite, (e.Caller, e.FilePath, e.Line), rules);
            result.Add(match is null ? e : e with { Kind = "handoff", HandoffDispatcher = match.Id });
        }
        return result;
    }

    // The first dispatcher rule whose pattern matches a consumer target co-located at `site`, or null.
    private static FactHandoffRule? MatchAtSite(
        Dictionary<(string, string, int), List<string>> consumersBySite,
        (string, string, int) site,
        IReadOnlyList<FactHandoffRule> rules
    )
    {
        if (!consumersBySite.TryGetValue(site, out var consumers))
            return null;
        foreach (var consumer in consumers)
        {
            var rule = Match(consumer, rules);
            if (rule is not null)
                return rule;
        }
        return null;
    }

    // The first dispatcher rule whose ConsumerPatterns substring-match the (arity-stripped) consumer
    // DocID, or null. Public so the entry-point promotion (Phase 3) can resolve a handoff edge's
    // dispatcher id back to its rule (for the EP kind / repeating flag).
    public static FactHandoffRule? Match(string consumerDocId, IReadOnlyList<FactHandoffRule> rules)
    {
        var normalized = StripArity(consumerDocId);
        foreach (var rule in rules)
        foreach (var pattern in rule.ConsumerPatterns)
            if (normalized.IndexOf(pattern, StringComparison.Ordinal) >= 0)
                return rule;
        return null;
    }

    public static FactHandoffRule? ById(string? dispatcherId, IReadOnlyList<FactHandoffRule> rules)
    {
        if (dispatcherId is null)
            return null;
        foreach (var rule in rules)
            if (string.Equals(rule.Id, dispatcherId, StringComparison.Ordinal))
                return rule;
        return null;
    }

    // The handoff entry points carried by a CLASSIFIED edge set: one per handoff edge (Dispatcher/Kind
    // set from its rule) plus one per surviving (unclassified) method-group edge (Dispatcher/Kind null
    // — the residual). Deduped by (Target, RegisteredIn, FilePath, Line); classified entries first,
    // then by target. This is the single source both `rig derive`'s handoff listing and the Phase-3
    // origin-EP promotion read, so classification lives in exactly one place.
    public static IReadOnlyList<HandoffEntryPoint> HandoffEntryPoints(
        IReadOnlyList<CallEdge> classifiedEdges,
        IReadOnlyList<FactHandoffRule> rules
    )
    {
        var byKey = new Dictionary<(string, string, string, int), HandoffEntryPoint>();
        foreach (var e in classifiedEdges)
        {
            string? dispatcher = null;
            string? kind = null;
            if (e.Kind == "handoff")
            {
                dispatcher = e.HandoffDispatcher;
                kind = ById(e.HandoffDispatcher, rules)?.Kind;
            }
            else if (e.Kind != "methodGroup")
            {
                continue;
            }

            var key = (e.Callee, e.Caller, e.FilePath, e.Line);
            // A handoff classification wins over an unclassified duplicate at the same key.
            if (byKey.TryGetValue(key, out var existing) && existing.Dispatcher is not null)
                continue;
            byKey[key] = new HandoffEntryPoint(e.Callee, e.Caller, e.FilePath, e.Line, dispatcher, kind);
        }

        return byKey
            .Values.OrderBy(h => h.Dispatcher is null ? 1 : 0)
            .ThenBy(h => h.Target, StringComparer.Ordinal)
            .ThenBy(h => h.FilePath, StringComparer.Ordinal)
            .ThenBy(h => h.Line)
            .ToArray();
    }

    // Strips generic-arity markers (`1, `2 …) from a DocID so rule patterns can be written without
    // backticks — "IAsyncEvent.Add" then matches "M:Ns.IAsyncEvent`1.Add(..)".
    private static string StripArity(string docId)
    {
        if (docId.IndexOf('`') < 0)
            return docId;
        var sb = new System.Text.StringBuilder(docId.Length);
        for (var i = 0; i < docId.Length; i++)
        {
            if (docId[i] == '`')
            {
                i++;
                while (i < docId.Length && char.IsDigit(docId[i]))
                    i++;
                i--;
                continue;
            }
            sb.Append(docId[i]);
        }
        return sb.ToString();
    }
}
