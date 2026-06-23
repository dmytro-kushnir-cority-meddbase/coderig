using Rig.Domain.Data;
using static System.Globalization.CultureInfo;

namespace Rig.Domain.Functions;

public static partial class FactPathFinder
{
    // Monomorphizes generic-FACTORY call edges (see FactGenericFactoryRule): an edge
    // `caller -> Factory<X,..>` whose call-site construct type arg X is concrete is rewritten to
    // `caller -> X.Target`, so the traversal goes straight to the constructed type's method and skips the
    // generic plumbing the factory forwards through (Entity.New``3 -> EntityCache`3.New -> ItemCache`3.Get
    // -> Construct`2.New -> ×N entity ctors). Edges with no concrete construct (a forwarded type
    // parameter) or whose target can't be resolved in the loaded graph are left intact — the in-memory
    // generic-dispatch narrowing remains the fallback there. Pure: returns a new FactGraphData with the
    // rewritten edges; Methods and type-relation edges are unchanged. Applied once after graph load so
    // tree / reaches / callers all see the collapsed graph.
    // Marks event-subscription method-group edges (`someEvent += Handler`) as async HANDOFFS, so the
    // synchronous traversal does NOT walk the handler as if it ran at subscription time — it runs later,
    // when the event is raised (the deferred-handler semantics, same as a background/timer dispatcher).
    // The subscription site is identified GENERICALLY (C# semantics, not codebase data): a `methodGroup`
    // edge co-located (same Caller/File/Line) with an event read — a "read" ref whose target is an event
    // (DocID "E:" prefix). A raise (`MyEvent?.Invoke`) reads the event too but has no co-located method-
    // group, so only real += / -= subscriptions match. Reclassified edges are sync-cut by default and
    // walked under --async (tagged "⤳ via event"). Pure: returns a new graph; --raw skips this entirely.
    public static FactGraphData MarkEventSubscriptionHandoffs(FactGraphData graph, ISet<EventSubscriptionSite> eventSites)
    {
        if (eventSites.Count == 0)
        {
            return graph;
        }

        var changed = false;
        var rewritten = new List<CallEdge>(graph.CallEdges.Count);
        foreach (var e in graph.CallEdges)
        {
            if (
                e.Kind == EdgeKinds.MethodGroup
                && eventSites.Contains(new EventSubscriptionSite(Caller: e.Caller, FilePath: e.FilePath, Line: e.Line))
            )
            {
                rewritten.Add(e with { Kind = "handoff", HandoffDispatcher = e.HandoffDispatcher ?? "event" });
                changed = true;
            }
            else
            {
                rewritten.Add(e);
            }
        }
        return changed ? graph with { CallEdges = rewritten } : graph;
    }

    // PUBLISH→CONSUMER DELIVERY EDGES — the SINGLE, framework-BLIND join. Resolves, by CHANNEL identity
    // (Tag, IdentityToken), which handler(s) run when a channel is PUBLISHED to, and ADDS those as handoff
    // edges producer→handler — the edge a publish (a `someEvent?.Invoke(..)` raise / an Echo
    // `Process.tell(name, msg)`) implies but that no syntactic call records. This is EDGE-CREATING, so it
    // MUST be baked into call_edges at graph-build (alongside RewriteGenericFactories) or the SQL bounding
    // walk never pulls a handler's closure into a bounded reach — see GraphMaterializer.
    //
    // Modeled as HANDOFF edges (HandoffDispatcher = the site's Tag, e.g. "event_raise" / "actor_tell"):
    // delivery is DEFERRED, exactly like a background/timer/event-subscription dispatch — sync-cut by
    // default, walked under --async, and visible to whole-program cycle detection (the FR-10 prerequisite).
    // The binding is only as precise as the channel identity the loader supplied: an event symbol (`E:`
    // DocID) is EXACT (a raise of E reaches precisely E's subscribers); a process-name string is ~heuristic
    // (a tell of "P" reaches every handler spawned under "P" — over-approximate on a shared name). Tag
    // namespaces the channel so an event raise never joins an actor tell even if their tokens collide.
    //
    // Scope line (deliberate): this is DELIVERY (the runtime causes the handler to run), NOT resource
    // COUPLING. A db/io/cache write→read on the same cell is correlated but NOT a delivery — folding those in
    // would make `reaches` claim every writer "reaches" every reader and destroy the graph's meaning. Those
    // stay out; they belong to the separate same-cell/consistency hazard layer (FR-1/dual_write).
    //
    // Producer vs registration is decided by DeliveryRole: a Producer always publishes; a Registration
    // contributes its co-located handler edge(s) to the channel; a ByColocation site is a registration IFF a
    // handler edge co-locates at its (Caller,FilePath,Line) (a C# event read is a subscription iff co-located,
    // else a raise) — so the loader need not know which a given event read is. The handler edge kind is
    // DATA-DRIVEN by the registration's HandlerDispatcher: when set (e.g. an Echo spawn), the co-located
    // HANDOFF edge(s) tagged with that dispatcher — the spawn's handler delegate(s), which the async-handoff
    // machinery reclassified from methodGroup to handoff BEFORE this join runs; when null (e.g. a C# event
    // subscription), the co-located `methodGroup` CallEdge (the subscription's `+= Handler`). A registration
    // that locates no handler edge contributes nothing.
    public static FactGraphData AddDeliveryEdges(FactGraphData graph, IReadOnlyList<DeliverySite> sites)
    {
        if (sites.Count == 0)
        {
            return graph;
        }

        // Co-located edges per call site — a registration's handler edge(s). An event subscription's handler is
        // the `+= H` METHODGROUP edge; a spawn's handler delegate(s) were reclassified by the async-handoff
        // machinery into HANDOFF edges tagged with the spawn dispatcher (e.g. meddbase.echo.spawn), so the
        // registration's HandlerDispatcher names which co-located handoff edges are its handlers. Index both
        // kinds once, on the co-location key (Caller, File, Line).
        var edgesBySite = new Dictionary<(string Caller, string File, int Line), List<(string Kind, string? Dispatcher, string Callee)>>();
        foreach (var e in graph.CallEdges)
        {
            if (e.Kind != EdgeKinds.MethodGroup && e.Kind != EdgeKinds.Handoff)
            {
                continue;
            }

            var key = (e.Caller, e.FilePath, e.Line);
            if (!edgesBySite.TryGetValue(key, out var list))
            {
                edgesBySite[key] = list = [];
            }
            list.Add((e.Kind, e.HandoffDispatcher, e.Callee));
        }

        // The handler(s) a registration site binds: its co-located HandlerDispatcher handoff edges when the rule
        // names one (spawn delegates), else its co-located methodGroup edges (event `+= H`).
        IReadOnlyList<string> HandlersAt(DeliverySite site)
        {
            if (!edgesBySite.TryGetValue((site.Caller, site.FilePath, site.Line), out var edges))
            {
                return [];
            }

            return site.HandlerDispatcher is { } dispatcher
                ? edges
                    .Where(x => x.Kind == EdgeKinds.Handoff && string.Equals(x.Dispatcher, dispatcher, StringComparison.Ordinal))
                    .Select(x => x.Callee)
                    .ToList()
                : edges.Where(x => x.Kind == EdgeKinds.MethodGroup).Select(x => x.Callee).ToList();
        }

        // Split sites into channel handlers ((Tag, IdentityToken) → handlers) and producers. A ByColocation
        // site is a registration iff it locates a handler; a Registration with no co-located handler edge
        // contributes nothing (the handler isn't a node we can resolve — a lambda subgraph / unresolved
        // target); a ByColocation site with no co-located handler is a raise → producer.
        var handlersByChannel = new Dictionary<(string Tag, string Token), HashSet<string>>();
        var producers = new List<DeliverySite>();
        foreach (var site in sites)
        {
            IReadOnlyList<string> handlers = site.Role == DeliveryRole.Producer ? [] : HandlersAt(site);
            var isRegistration = site.Role == DeliveryRole.Registration || (site.Role == DeliveryRole.ByColocation && handlers.Count > 0);

            if (isRegistration)
            {
                if (handlers.Count > 0)
                {
                    var channel = (site.Tag, site.IdentityToken);
                    if (!handlersByChannel.TryGetValue(channel, out var set))
                    {
                        handlersByChannel[channel] = set = new HashSet<string>(StringComparer.Ordinal);
                    }
                    set.UnionWith(handlers);
                }
            }
            else
            {
                producers.Add(site);
            }
        }

        if (handlersByChannel.Count == 0 || producers.Count == 0)
        {
            return graph; // nothing to connect (no handlers, or no producers)
        }

        // One handoff edge per (producer, handler), at the publish site, tagged with the producer's Tag.
        // Dedup on (Caller, Callee, Tag) so a method that publishes the same channel on several lines doesn't
        // multiply identical edges.
        var added = new List<CallEdge>();
        var seen = new HashSet<(string, string, string)>();
        foreach (var producer in producers)
        {
            if (!handlersByChannel.TryGetValue((producer.Tag, producer.IdentityToken), out var channelHandlers))
            {
                continue;
            }

            // Precision marks whether this producer→handler binding is trustworthy. A channel with a SINGLE
            // handler is unambiguous (the raise can only reach that one handler) — Exact. A channel with
            // MANY handlers means the join (by event symbol / process-name token, with no instance or
            // call-site identity) fanned the producer out to every same-symbol subscriber regardless of
            // which caller wired which handler — Fanout, the imprecise binding the default --async walk
            // quarantines (see docs/FIX-event-raise-overapproximation.md).
            var precision = channelHandlers.Count == 1 ? DeliveryPrecisions.Exact : DeliveryPrecisions.Fanout;
            foreach (var handler in channelHandlers)
            {
                if (seen.Add((producer.Caller, handler, producer.Tag)))
                {
                    added.Add(
                        new CallEdge(
                            Caller: producer.Caller,
                            Callee: handler,
                            Kind: EdgeKinds.Handoff,
                            FilePath: producer.FilePath,
                            Line: producer.Line,
                            HandoffDispatcher: producer.Tag,
                            DeliveryPrecision: precision
                        )
                    );
                }
            }
        }

        return added.Count == 0 ? graph : graph with { CallEdges = [.. graph.CallEdges, .. added] };
    }

    // The SINGLE shaping pass for the attribution/traversal commands (reaches/tree/path/callers). Applied
    // once at graph load, direction-agnostic: (1) monomorphize generic-factory edges into the CallEdges,
    // then (2) carry the cut + context-dispatch rules ON the graph so BuildIndex picks them up and BOTH
    // forward (Successors) and reverse (Predecessors) traversals honour the identical shaping. This is
    // what makes `callers` agree with `path`/`reaches` — they all walk the same shaped graph rather than
    // each command shaping (or not) on its own. Empty rule sets => the unshaped graph (the `--raw` path,
    // and what `dead` uses — it needs the sound CHA superset, so it never calls this).
    public static FactGraphData ShapeGraph(
        FactGraphData graph,
        IReadOnlyList<FactGenericFactoryRule> factoryRules,
        IReadOnlyList<FactTraversalCutRule> cutRules,
        IReadOnlyList<FactContextDispatchRule> contextRules
    )
    {
        var shaped = RewriteGenericFactories(graph, factoryRules);
        if (cutRules.Count == 0 && contextRules.Count == 0)
        {
            return shaped;
        }

        return shaped with
        {
            CutRules = cutRules.Count > 0 ? cutRules : null,
            ContextRules = contextRules.Count > 0 ? contextRules : null,
        };
    }

    public static FactGraphData RewriteGenericFactories(FactGraphData graph, IReadOnlyList<FactGenericFactoryRule> rules)
    {
        if (rules.Count == 0)
        {
            return graph;
        }

        // (stripped construct type DocID, method name) -> overloads, for resolving X.Target.
        var methodsByTypeAndName = new Dictionary<(string Type, string Name), List<MethodRef>>();
        foreach (var m in graph.Methods)
        {
            if (m.ContainingTypeId is null)
            {
                continue;
            }

            var key = (TypeClosure.StripGeneric(m.ContainingTypeId), m.Name);
            if (!methodsByTypeAndName.TryGetValue(key, out var list))
            {
                methodsByTypeAndName[key] = list = new List<MethodRef>();
            }

            list.Add(m);
        }

        var ruleByMethod = new Dictionary<string, FactGenericFactoryRule>(StringComparer.Ordinal);
        foreach (var r in rules)
        {
            ruleByMethod[r.Method] = r;
        }

        var rewritten = new List<CallEdge>(graph.CallEdges.Count);
        var changed = false;
        foreach (var edge in graph.CallEdges)
        {
            var resolved = ResolveFactoryEdge(edge, ruleByMethod, methodsByTypeAndName);
            if (resolved is null)
            {
                rewritten.Add(edge);
            }
            else
            {
                rewritten.AddRange(resolved);
                changed = true;
            }
        }
        return changed ? graph with { CallEdges = rewritten } : graph;
    }

    // Resolves one call edge against the factory rules: null when the edge isn't a (concrete) factory
    // call, else the rewritten edge(s) targeting the construct type's method overloads (arity-matched to
    // the factory call). Returns null (keep the edge) when nothing resolves — never drops the edge.
    private static List<CallEdge>? ResolveFactoryEdge(
        CallEdge edge,
        Dictionary<string, FactGenericFactoryRule> ruleByMethod,
        Dictionary<(string Type, string Name), List<MethodRef>> methodsByTypeAndName
    )
    {
        // Cheap guard FIRST: a concrete generic-factory call must carry type arguments (the construct
        // type lives in edge.TypeArguments — see below). The overwhelming majority of call edges are
        // non-generic, so this short-circuits before ParseMethod, whose per-edge substring allocations
        // dominated ShapeGraph's churn. Pure reorder — the empty-TypeArguments case returned null anyway.
        if (string.IsNullOrEmpty(edge.TypeArguments))
        {
            return null;
        }

        var parsed = ParseMethod(edge.Callee);
        if (parsed is null)
        {
            return null;
        }

        // ParseMethod returns TypeId WITH the "T:" prefix and a name that still carries the method
        // generic-arity marker (e.g. "New``3"); rule.Method is a plain "<declType>.<name>", so strip
        // the "``N" before matching.
        var name = parsed.Value.Name;
        var tick = name.IndexOf("``", StringComparison.Ordinal);
        if (tick >= 0)
        {
            name = name.Substring(startIndex: 0, length: tick);
        }

        var methodKey = parsed.Value.TypeId.Substring(2) + "." + name;
        if (!ruleByMethod.TryGetValue(methodKey, out var rule))
        {
            return null;
        }

        var construct = NthTopLevelArg(edge.TypeArguments!, rule.ConstructArgIndex);
        // Only a concrete, namespaced type can name a real construct; a bare type-parameter token
        // ("TConstruct") or primitive has no '.' and isn't resolvable -> leave the edge for the fallback.
        if (construct is null || construct.IndexOf('.') < 0)
        {
            return null;
        }

        var constructType = "T:" + TypeClosure.StripGeneric(construct);
        if (!methodsByTypeAndName.TryGetValue((constructType, rule.TargetMethod), out var candidates))
        {
            return null;
        }

        var arity = ParamArity(edge.Callee);
        var matched = candidates.Where(c => ParamArity(c.SymbolId) == arity).ToList();
        if (matched.Count == 0)
        {
            matched = candidates; // no arity match — take all overloads; still bypasses the plumbing
        }

        if (matched.Count == 0)
        {
            return null;
        }

        // Disambiguate same-arity overloads by the PK type. The factory's first parameter is a method
        // type-param reference (Entity.New``3(``1) -> index 1 = TPk), so the pk type is type-arg[1];
        // keep only target overloads whose own first parameter type matches it (C#-keyword normalized,
        // so `int` == System.Int32). Without this, `Entity.New<Account,Guid,…>` resolved to BOTH
        // Account.New(Guid) and Account.New(Int32) (same arity). Recall-safe: an empty match keeps the
        // arity-matched set.
        if (matched.Count > 1)
        {
            var pkIndex = TypeParamRefIndex(FirstTopLevelParam(edge.Callee));
            if (pkIndex >= 0 && NthTopLevelArg(edge.TypeArguments!, pkIndex) is { } pkType)
            {
                var pkNorm = NormalizeTypeName(pkType);
                var byPk = matched.Where(c => NormalizeTypeName(FirstTopLevelParam(c.SymbolId)) == pkNorm).ToList();
                if (byPk.Count > 0)
                {
                    matched = byPk;
                }
            }
        }
        return matched.Select(m => edge with { Callee = m.SymbolId, TypeArguments = null }).ToList();
    }

    // C# keyword aliases -> BCL simple name, so a pk type arg rendered as a keyword (`int`) compares
    // equal to a DocID parameter type (`System.Int32`).
    private static readonly Dictionary<string, string> CSharpKeywordTypes = new(StringComparer.Ordinal)
    {
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["bool"] = "Boolean",
        ["char"] = "Char",
        ["string"] = "String",
        ["object"] = "Object",
        ["float"] = "Single",
        ["double"] = "Double",
        ["decimal"] = "Decimal",
        ["nint"] = "IntPtr",
        ["nuint"] = "UIntPtr",
    };

    // Simple type name (namespace + generic/array suffix stripped), with C# keyword aliases mapped to
    // their BCL name so "int" and "System.Int32" compare equal. "" for null/blank.
    private static string NormalizeTypeName(string? type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return "";
        }

        var t = type!.Trim();
        var marker = t.IndexOfAny(['{', '<', '[']);
        if (marker >= 0)
        {
            t = t.Substring(startIndex: 0, length: marker);
        }

        var dot = t.LastIndexOf('.');
        var simple = dot >= 0 ? t.Substring(dot + 1) : t;
        return CSharpKeywordTypes.TryGetValue(key: simple, value: out var bcl) ? bcl : simple;
    }

    // The first top-level parameter substring of a method DocID's "(...)" list, or null if there is none.
    private static string? FirstTopLevelParam(string docId)
    {
        var open = docId.IndexOf('(');
        if (open < 0)
        {
            return null;
        }

        var close = docId.LastIndexOf(')');
        if (close <= open + 1)
        {
            return null;
        }

        var depth = 0;
        for (var i = open + 1; i < close; i++)
        {
            var c = docId[i];
            if (c is '{' or '[' or '(' or '<')
            {
                depth++;
            }
            else if (c is '}' or ']' or ')' or '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                return docId.Substring(startIndex: open + 1, length: i - (open + 1)).Trim();
            }
        }
        return docId.Substring(startIndex: open + 1, length: close - (open + 1)).Trim();
    }

    // A method type-parameter reference token ("``N") -> its index N; -1 for anything else (a concrete
    // type, a type-level "`N", or null). Used to find which type arg fills a factory's pk parameter.
    private static int TypeParamRefIndex(string? param)
    {
        if (param is null || !param.StartsWith("``", StringComparison.Ordinal))
        {
            return -1;
        }

        return int.TryParse(param.Substring(2), InvariantCulture, out var n) ? n : -1;
    }

    // The Nth (0-based) top-level element of a comma-joined type-arg list — commas inside <>/()/[]
    // don't split (so a tuple/generic arg stays whole). Null when out of range / blank.
    private static string? NthTopLevelArg(string typeArguments, int index)
    {
        if (index < 0)
        {
            return null;
        }

        var depth = 0;
        var position = 0;
        var start = 0;
        for (var i = 0; i < typeArguments.Length; i++)
        {
            var c = typeArguments[i];
            if (c is '<' or '(' or '[')
            {
                depth++;
            }
            else if (c is '>' or ')' or ']')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                if (position == index)
                {
                    return typeArguments.Substring(startIndex: start, length: i - start).Trim();
                }

                position++;
                start = i + 1;
            }
        }
        return position == index ? typeArguments.Substring(start).Trim() : null;
    }
}
