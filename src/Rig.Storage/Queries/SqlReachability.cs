using System.Data.Common;
using System.Text;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

// On-disk reachability over the derived edge views (call_edges + dispatch_edges) using SQLite
// recursive CTEs. This is the perf path that replaces loading the whole 1.4M-row graph into process
// memory: SQLite walks only the reachable frontier via the FromSym/ToSym indexes, so latency and RAM
// scale with the RESULT, not the store. No daemon, no state — each call is a single query.
//
// The traversed edge set is exactly call_edges ∪ dispatch_edges, the same edges FactPathFinder
// computes in memory (dispatch_edges is materialised from FactPathFinder.AllDispatchEdges), so the
// reachable SET matches the EF oracle. Requires `rig graph` to have been run (GraphMaterializer);
// HasGraphAsync reports whether the views exist + are populated.
public static class SqlReachability
{
    public enum Direction
    {
        Forward,
        Reverse,
    }

    // True when the store carries a CURRENT graph — i.e. `rig graph` (GraphMaterializer) has stamped the
    // graph schema version into `meta`. Routed through SchemaGate.GraphAvailableAsync (the single source
    // of graph-presence truth) rather than probing call_edges directly: the graph is built as a UNIT, so
    // its presence is a property of the db file, not of any one table. Callers fall back to the EF path
    // (or prompt to run `rig graph`) when false.
    public static async Task<bool> HasGraphAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        return await SchemaGate.GraphAvailableAsync(connection, cancellationToken);
    }

    // The transitive set reachable from (Forward) / reaching (Reverse) the given seed symbols, over
    // call_edges ∪ dispatch_edges. Seeds are matched by EXACT SymbolId. The result INCLUDES the seeds
    // themselves (mirrors FactPathFinder.ReachableFromAll / the depth-0 start nodes). Cycles terminate
    // via UNION's set dedup; the closure is unbounded (no maxDepth) — the dead-code / full-reach case.
    public static async Task<HashSet<string>> ReachableSetAsync(
        RigDbContext context,
        IReadOnlyCollection<string> seeds,
        Direction direction,
        bool includeHandoff = false,
        CancellationToken cancellationToken = default
    )
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (seeds.Count == 0)
        {
            return result;
        }

        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = BuildSetCte(command, seeds, direction, includeHandoff);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    // Pattern-seeded reachability WITH shortest depth, computed entirely in SQL — the fast equivalent
    // of LoadBoundedGraphAsync + FactPathFinder.ReachedBy/Reaches for the set+depth queries (callers,
    // and the reachable-method map behind reaches/tree). Seeds = every node whose DocID matches
    // `pattern` (case-insensitive substring, mirroring FactPathFinder.Contains), depth 0; the closure
    // walks call_edges ∪ dispatch_edges in `direction`, bounded by maxDepth (matching ReachedBy's cap).
    // Returns sym -> shortest hop count, including the depth-0 seeds. No in-memory graph load.
    //
    // Equivalence: the traversed edge set is exactly the one FactPathFinder walks (dispatch_edges is
    // FactPathFinder.AllDispatchEdges), and the seed set matches index.Nodes (the `nodes` table unions
    // edge endpoints with symbol_facts methods), so the keyset matches the in-memory oracle for the
    // same maxDepth. min(depth) over the bounded CTE is the BFS-shortest depth.
    // includeHandoff=false (sync-cut, the default) appends `AND Kind <> 'handoff'` to the call_edges
    // leg, so an async handoff registration does not count as reaching its callback — matching the
    // in-memory oracle's SyncCut mode. true (--async) walks them. The dispatch_edges leg is never
    // handoff, so it is unfiltered in both modes.
    public static async Task<IReadOnlyDictionary<string, int>> ReachedWithDepthAsync(
        RigDbContext context,
        string pattern,
        Direction direction,
        int maxDepth,
        bool includeHandoff = false,
        CancellationToken cancellationToken = default
    )
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        await BuildReachDepthAsync(connection, pattern, direction, maxDepth, includeHandoff, cancellationToken);

        await ReadAsync(
            connection,
            "SELECT sym, depth FROM reach_depth;",
            reader => result[reader.GetString(0)] = reader.GetInt32(1),
            cancellationToken
        );
        return result;
    }

    // Entry-point CANDIDATES reaching `pattern` (rig callers --roots): the reverse-reachable nodes with
    // NO predecessor at all — no incoming call edge and no incoming dispatch edge (ToSym = node). Mirrors
    // FactPathFinder.EntryRootsReaching: the tops of the reverse closure (framework/DI/reflection entry
    // points). Computed in SQL off the same bounded reverse closure.
    public static async Task<IReadOnlyList<string>> EntryRootsReachingAsync(
        RigDbContext context,
        string pattern,
        int maxDepth,
        bool includeHandoff = false,
        CancellationToken cancellationToken = default
    )
    {
        var reachable = await ReachedWithDepthAsync(context, pattern, Direction.Reverse, maxDepth, includeHandoff, cancellationToken);
        if (reachable.Count == 0)
        {
            return [];
        }

        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_set;", null, cancellationToken);
        await ExecNonQueryAsync(connection, "CREATE TEMP TABLE reach_set(sym TEXT PRIMARY KEY);", null, cancellationToken);
        // The reverse closure already lives in `reach_depth` — built by ReachedWithDepthAsync above on this
        // SAME shared connection (temp tables are connection-scoped, and it isn't dropped until the next
        // ReachedWithDepthAsync). Copy it in ONE set-based statement instead of marshalling every key back to
        // C# and re-inserting it row-by-row (a synchronous round-trip per closure member — thousands).
        await ExecNonQueryAsync(connection, "INSERT OR IGNORE INTO reach_set(sym) SELECT sym FROM reach_depth;", null, cancellationToken);

        // A no-predecessor root has no incoming SYNCHRONOUS call edge (handoff edges don't count under
        // sync-cut — that's exactly what makes a scheduled callback a background ORIGIN) and no incoming
        // dispatch edge. Under --async, handoff edges count as predecessors so a callback only surfaces
        // as a root when nothing — sync or scheduled — reaches it.
        var handoffFilter = includeHandoff ? "" : " AND Kind <> 'handoff'";
        var roots = new List<string>();
        await ReadAsync(
            connection,
            $"""
            SELECT s.sym FROM reach_set s
            WHERE NOT EXISTS (SELECT 1 FROM call_edges     WHERE ToSym = s.sym{handoffFilter})
              AND NOT EXISTS (SELECT 1 FROM dispatch_edges WHERE ToSym = s.sym)
            ORDER BY s.sym;
            """,
            reader => roots.Add(reader.GetString(0)),
            cancellationToken
        );
        return roots;
    }

    // Seed selection. With the `nodes` table (built by GraphMaterializer = distinct edge endpoints ∪
    // symbol_facts methods) one indexed scan matches FactPathFinder's index.Nodes universe. Without it,
    // fall back to the four edge-column LIKEs plus symbol_facts methods (BuildReachSetAsync's seeds).
    // Plain substring LIKE, mirroring FactPathFinder.Contains — so the bounded graph this seeds is a
    // faithful SUPERSET for the in-memory traversal that runs over it (which re-seeds with Contains).
    private static string SeedSql(bool hasNodes) =>
        hasNodes ? "SELECT sym FROM nodes WHERE sym LIKE $pat ESCAPE '\\'" : EdgeEndpointSeedUnion;

    // Seeds = every endpoint of call_edges / dispatch_edges plus every method symbol whose DocID matches
    // the LIKE pattern. The fallback for stores without the precomputed `nodes` table; also the seed CTE
    // of BuildReachSetAsync. Single definition so the two seed paths can't drift.
    private const string EdgeEndpointSeedUnion = """
        SELECT FromSym FROM call_edges     WHERE FromSym LIKE $pat ESCAPE '\'
        UNION SELECT ToSym FROM call_edges WHERE ToSym   LIKE $pat ESCAPE '\'
        UNION SELECT FromSym FROM dispatch_edges WHERE FromSym LIKE $pat ESCAPE '\'
        UNION SELECT ToSym FROM dispatch_edges   WHERE ToSym   LIKE $pat ESCAPE '\'
        UNION SELECT SymbolId FROM symbol_facts WHERE Kind = 'method' AND SymbolId LIKE $pat ESCAPE '\'
        """;

    private static void AddLikeParam(DbCommand command, string pattern)
    {
        var p = command.CreateParameter();
        p.ParameterName = "$pat";
        p.Value = "%" + EscapeLike(pattern) + "%";
        command.Parameters.Add(p);
    }

    // Forward: follow FromSym -> ToSym (callees + dispatch targets). Reverse: follow ToSym -> FromSym
    // (callers + reverse dispatch). A single recursive term joins the recursion frontier against a
    // UNION ALL of both edge tables, so the FromSym/ToSym indexes drive the walk.
    private static string BuildSetCte(DbCommand command, IReadOnlyCollection<string> seeds, Direction direction, bool includeHandoff)
    {
        var (frontierCol, nextCol) = direction == Direction.Forward ? ("FromSym", "ToSym") : ("ToSym", "FromSym");
        var handoffFilter = includeHandoff ? "" : " WHERE Kind <> 'handoff'";

        var seedValues = new StringBuilder();
        var i = 0;
        foreach (var seed in seeds)
        {
            if (i > 0)
            {
                seedValues.Append(", ");
            }

            var name = "$s" + i;
            seedValues.Append('(').Append(name).Append(')');
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = seed;
            command.Parameters.Add(p);
            i++;
        }

        return $"""
            WITH RECURSIVE
            seeds(sym) AS (VALUES {seedValues}),
            edges(frontier, next) AS (
                SELECT {frontierCol}, {nextCol} FROM call_edges{handoffFilter}
                UNION ALL
                SELECT {frontierCol}, {nextCol} FROM dispatch_edges
            ),
            reach(sym) AS (
                SELECT sym FROM seeds
                UNION
                SELECT edges.next FROM edges JOIN reach ON edges.frontier = reach.sym
            )
            SELECT sym FROM reach;
            """;
    }

    // Loads the BOUNDED call-graph subgraph for the closure of every symbol matching `pattern`, in the
    // given direction, via the derived edge views. The returned FactGraphData contains exactly the
    // reachable (Forward) / reaching (Reverse) closure's edges + the methods in that closure + ALL type
    // relations (small). Running the UNCHANGED FactPathFinder over this is identical to running it over
    // the full in-memory graph for the same pattern — the closure is complete (dispatch_edges captures
    // every dispatch target, so degree counts match) — but the load is O(result), not O(whole store).
    // This is what lets reaches/tree/callers keep their exact output while dropping the 1.4M-row load.
    //
    // Pattern matching mirrors FactPathFinder's case-insensitive substring match (SQLite LIKE is
    // ASCII-case-insensitive); DocIDs are ASCII. Returns an empty graph when nothing matches.
    public static async Task<FactGraphData> LoadBoundedGraphAsync(
        RigDbContext context,
        string pattern,
        Direction direction,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        await BuildReachSetAsync(connection, pattern, direction, cancellationToken);
        return await LoadGraphFromReachSetAsync(connection, direction, cancellationToken);
    }

    // Everything reaches/tree need to reproduce their exact output, bounded to the closure: the
    // subgraph PLUS the effect-derivation inputs (invocations / ctor refs / throw refs whose ENCLOSING
    // method is in the closure). Built off the single reach_set so the whole-codebase invocation scan
    // (the part that dominated reaches/tree latency) is replaced by a bounded, indexed join. Base edges
    // come from the graph (all base edges), so LoadFactEntryPointDataAsync — heavy and otherwise only
    // used for its base edges + ctor refs here — is skipped entirely.
    // F2: EpData carries the FactEntryPointData the EF-fallback path (LoadReachInputsFromRowsAsync)
    // already loaded, so a caller that also needs the EP site map (DeriveEpSiteKindAsync) can reuse it
    // instead of issuing a second LoadFactEntryPointDataAsync. Null on the SQL path (not loaded there).
    public sealed record ReachInputs(
        FactGraphData Graph,
        IReadOnlyList<FactInvocation> Invocations,
        IReadOnlyList<SymbolRef> CtorRefs,
        IReadOnlyList<SymbolRef> ThrowRefs,
        IReadOnlyList<AllocationFact> AllocationFacts,
        FactEntryPointDeriver.FactEntryPointData? EpData = null
    );

    public static async Task<ReachInputs> LoadReachInputsAsync(
        RigDbContext context,
        string pattern,
        Direction direction,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        await BuildReachSetAsync(connection, pattern, direction, cancellationToken);
        var graph = await LoadGraphFromReachSetAsync(connection, direction, cancellationToken);

        var invocations = new List<FactInvocation>();
        await ReadAsync(
            connection,
            """
            SELECT r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.ReceiverType,
                   r.FirstArgumentTemplate, r.FirstArgumentType, r.EnclosingLoopKind, r.EnclosingLoopDetail,
                   r.EnclosingInvocations, r.EnclosingCatchTypes, r.TypeArguments, r.FirstArgumentName,
                   r.ArgumentTemplates, r.ArgumentNames, r.EnclosingGuards
            FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
            WHERE r.RefKind = 'invocation';
            """,
            reader =>
                invocations.Add(
                    // Positional through FirstArgName (index 12); the new nth-argument lists are
                    // appended as NAMED args because EnclosingScopes (param 13) is skipped on this path.
                    new FactInvocation(
                        Target: reader.GetString(0),
                        Enclosing: reader.IsDBNull(1) ? null : reader.GetString(1),
                        FilePath: reader.IsDBNull(2) ? "" : reader.GetString(2),
                        Line: reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                        Receiver: reader.IsDBNull(4) ? null : reader.GetString(4),
                        FirstArgTemplate: reader.IsDBNull(5) ? null : reader.GetString(5),
                        FirstArgType: reader.IsDBNull(6) ? null : reader.GetString(6),
                        LoopKind: reader.IsDBNull(7) ? null : reader.GetString(7),
                        LoopDetail: reader.IsDBNull(8) ? null : reader.GetString(8),
                        EnclosingInvocations: reader.IsDBNull(9) ? null : reader.GetString(9),
                        CatchTypes: reader.IsDBNull(10) ? null : reader.GetString(10),
                        TypeArguments: reader.IsDBNull(11) ? null : reader.GetString(11),
                        FirstArgName: reader.IsDBNull(12) ? null : reader.GetString(12),
                        ArgumentTemplates: reader.IsDBNull(13) ? null : reader.GetString(13),
                        ArgumentNames: reader.IsDBNull(14) ? null : reader.GetString(14),
                        EnclosingGuards: reader.IsDBNull(15) ? null : reader.GetString(15)
                    )
                ),
            cancellationToken
        );

        // ctor refs: dedup by (FilePath, Line), matching LoadFactEntryPointDataAsync.
        var ctorByLoc = new Dictionary<(string, int), SymbolRef>();
        await ReadAsync(
            connection,
            """
            SELECT r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line
            FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
            WHERE r.RefKind = 'ctor';
            """,
            reader =>
            {
                var file = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var line = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                ctorByLoc.TryAdd(
                    (file, line),
                    new SymbolRef(
                        Target: reader.GetString(0),
                        Enclosing: reader.IsDBNull(1) ? null : reader.GetString(1),
                        FilePath: file,
                        Line: line
                    )
                );
            },
            cancellationToken
        );

        // throw refs: dedup by (FilePath, Line, Target), matching LoadThrowRefsAsync.
        var throwByKey = new Dictionary<(string, int, string), SymbolRef>();
        await ReadAsync(
            connection,
            """
            SELECT r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.EnclosingGuards
            FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
            WHERE r.RefKind = 'throw';
            """,
            reader =>
            {
                var target = reader.GetString(0);
                var file = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var line = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                throwByKey.TryAdd(
                    (file, line, target),
                    new SymbolRef(
                        Target: target,
                        Enclosing: reader.IsDBNull(1) ? null : reader.GetString(1),
                        FilePath: file,
                        Line: line,
                        EnclosingGuards: reader.IsDBNull(4) ? null : reader.GetString(4)
                    )
                );
            },
            cancellationToken
        );

        var allocations = new List<AllocationFact>();
        await ReadAsync(
            connection,
            """
            SELECT a.Operation, a.ResourceType, a.EnclosingSymbolId, a.FilePath, a.Line,
                   a.EnclosingLoopKind, a.EnclosingLoopDetail, a.EnclosingGuards
            FROM allocation_facts a JOIN reach_set s ON a.EnclosingSymbolId = s.sym;
            """,
            reader =>
                allocations.Add(
                    new AllocationFact(
                        Operation: reader.GetString(0),
                        ResourceType: reader.GetString(1),
                        EnclosingSymbolId: reader.GetString(2),
                        FilePath: reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Line: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                        EnclosingLoopKind: reader.IsDBNull(5) ? null : reader.GetString(5),
                        EnclosingLoopDetail: reader.IsDBNull(6) ? null : reader.GetString(6),
                        EnclosingGuards: reader.IsDBNull(7) ? null : reader.GetString(7)
                    )
                ),
            cancellationToken
        );

        return new ReachInputs(
            graph,
            invocations,
            CtorRefs: ctorByLoc.Values.ToArray(),
            ThrowRefs: throwByKey.Values.ToArray(),
            AllocationFacts: allocations
        );
    }

    // Loads the bounded FactGraphData from the already-built reach_set temp table. Forward joins
    // out-edges (FromSym in set); reverse joins in-edges (ToSym in set) for the reverse BFS.
    private static async Task<FactGraphData> LoadGraphFromReachSetAsync(
        DbConnection connection,
        Direction direction,
        CancellationToken cancellationToken
    )
    {
        var edgeJoinCol = direction == Direction.Forward ? "FromSym" : "ToSym";

        // The call-site generic type-args AND the monomorphization bindings (DeclaringTypeArgBinding /
        // MethodTypeArgBinding) live on reference_facts, NOT on the persisted call_edges view — so the bounded
        // graph must re-attach them keyed by (caller, callee, line). Without the bindings the ShapeGraph
        // monomorphization seam sees nothing and the bounded path can't reproduce the full-graph (narrowed)
        // reach. One bulk pass over reach_set; non-generic edges have no entry -> null. (The columns are
        // guaranteed by the open-time index-schema gate, so no per-column probe.)
        var typeArgsByEdge = new Dictionary<(string, string, int), string>();
        var declBindingByEdge = new Dictionary<(string, string, int), string>();
        var methBindingByEdge = new Dictionary<(string, string, int), string>();
        await ReadAsync(
            connection,
            """
            SELECT rf.EnclosingSymbolId, rf.TargetSymbolId, rf.Line, rf.TypeArguments, rf.DeclaringTypeArgBinding, rf.MethodTypeArgBinding
            FROM reference_facts rf JOIN reach_set r ON rf.EnclosingSymbolId = r.sym
            WHERE rf.TypeArguments IS NOT NULL OR rf.DeclaringTypeArgBinding IS NOT NULL OR rf.MethodTypeArgBinding IS NOT NULL;
            """,
            reader =>
            {
                // first wins; a dup (caller, callee, line) is vanishingly rare
                var key = (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
                if (!reader.IsDBNull(3))
                {
                    typeArgsByEdge.TryAdd(key, reader.GetString(3));
                }

                if (!reader.IsDBNull(4))
                {
                    declBindingByEdge.TryAdd(key, reader.GetString(4));
                }

                if (!reader.IsDBNull(5))
                {
                    methBindingByEdge.TryAdd(key, reader.GetString(5));
                }
            },
            cancellationToken
        );

        var callEdges = new List<CallEdge>();
        await ReadAsync(
            connection,
            $"""
            SELECT c.FromSym, c.ToSym, c.Kind, c.FilePath, c.Line, c.LoopKind, c.LoopDetail, c.ReceiverType, c.HandoffDispatcher, c.DeliveryPrecision, c.NonVirtual, c.EnclosingGuards
            FROM call_edges c JOIN reach_set r ON c.{edgeJoinCol} = r.sym;
            """,
            reader =>
            {
                var from = reader.GetString(0);
                var to = reader.GetString(1);
                var line = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                typeArgsByEdge.TryGetValue((from, to, line), out var typeArgs);
                declBindingByEdge.TryGetValue((from, to, line), out var declBinding);
                methBindingByEdge.TryGetValue((from, to, line), out var methBinding);
                callEdges.Add(
                    new CallEdge(
                        Caller: from,
                        Callee: to,
                        Kind: reader.GetString(2),
                        FilePath: reader.IsDBNull(3) ? "" : reader.GetString(3),
                        Line: line,
                        LoopKind: reader.IsDBNull(5) ? null : reader.GetString(5),
                        LoopDetail: reader.IsDBNull(6) ? null : reader.GetString(6),
                        ReceiverType: reader.IsDBNull(7) ? null : reader.GetString(7),
                        HandoffDispatcher: reader.IsDBNull(8) ? null : reader.GetString(8),
                        TypeArguments: typeArgs,
                        DeclaringTypeArgBinding: declBinding,
                        MethodTypeArgBinding: methBinding,
                        DeliveryPrecision: reader.IsDBNull(9) ? null : reader.GetString(9),
                        NonVirtual: !reader.IsDBNull(10) && reader.GetInt64(10) != 0,
                        EnclosingGuards: reader.IsDBNull(11) ? null : reader.GetString(11)
                    )
                );
            },
            cancellationToken
        );

        var methodById = new Dictionary<string, MethodRef>(StringComparer.Ordinal);
        await ReadAsync(
            connection,
            "SELECT s.SymbolId, s.Name, s.ContainingSymbolId, s.IsOverride, s.FilePath, s.Line "
                + "FROM symbol_facts s JOIN reach_set r ON s.SymbolId = r.sym WHERE s.Kind = 'method';",
            reader =>
            {
                var id = reader.GetString(0);
                if (!methodById.ContainsKey(id))
                {
                    methodById[id] = new MethodRef(
                        SymbolId: id,
                        Name: reader.GetString(1),
                        ContainingTypeId: reader.IsDBNull(2) ? null : reader.GetString(2),
                        IsOverride: !reader.IsDBNull(3) && reader.GetInt32(3) != 0,
                        FilePath: reader.IsDBNull(4) ? null : reader.GetString(4),
                        Line: reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                    );
                }
            },
            cancellationToken
        );

        var implEdges = new HashSet<ImplementsEdge>();
        var baseEdges = new HashSet<BaseEdge>();
        await ReadAsync(
            connection,
            "SELECT TypeSymbolId, RelatedSymbolId, RelationKind FROM type_relation_facts WHERE RelationKind IN ('interface','base');",
            reader =>
            {
                var type = reader.GetString(0);
                var related = reader.GetString(1);
                if (reader.GetString(2) == "interface")
                {
                    implEdges.Add(new ImplementsEdge(ImplType: type, InterfaceType: related));
                }
                else
                {
                    baseEdges.Add(new BaseEdge(SubType: type, BaseType: related));
                }
            },
            cancellationToken
        );

        // Mined dispatch facts ride into the bounded graph so the in-memory FactPathFinder resolves
        // dispatch exact-first over it, matching the full-graph oracle (and the materialised
        // dispatch_edges, which are built from the same facts). Loaded WHOLE like the type relations —
        // the table is tiny relative to reference_facts, and an unbounded superset can only agree with
        // the full graph. dispatch_facts is a FACT table guaranteed by the open-time index gate (it is
        // part of the v1 fact schema), so no per-table probe — an old store fails fast at open, not here.
        var mined = new HashSet<DispatchFact>();
        await ReadAsync(
            connection,
            "SELECT DISTINCT SourceMember, TargetMember, Kind FROM dispatch_facts;",
            reader =>
                mined.Add(
                    new DispatchFact(SourceMember: reader.GetString(0), TargetMember: reader.GetString(1), Kind: reader.GetString(2))
                ),
            cancellationToken
        );
        IReadOnlyList<DispatchFact>? minedDispatch = mined.ToArray();

        return new FactGraphData(callEdges, implEdges.ToArray(), methodById.Values.ToArray(), baseEdges.ToArray(), minedDispatch);
    }

    // Builds the temp `reach_depth` table = the bounded closure from the pattern-seeds over
    // call_edges ∪ dispatch_edges in `direction`, with each node's SHORTEST hop depth. Iterative
    // level-BFS: each level expands ONLY the current frontier (depth = d), and INSERT OR IGNORE keeps the
    // first (shortest) depth per sym, so total work is O(closure edges).
    //
    // The per-level joins are written `reach_depth CROSS JOIN <edges>` deliberately: CROSS JOIN pins the
    // join order so the tiny depth-d frontier DRIVES and the edge table is probed via its
    // FromSym/ToSym index. A plain JOIN lets the planner reorder — and with NO statistics on the temp
    // frontier it assumes the frontier is large and inverts into a full SCAN of the edge table on every
    // level. (`nodes`, the seed source via SeedSql, is part of the graph UNIT — present whenever this
    // path runs, since every caller gates on HasGraphAsync first.)
    private static async Task BuildReachDepthAsync(
        DbConnection connection,
        string pattern,
        Direction direction,
        int maxDepth,
        bool includeHandoff,
        CancellationToken cancellationToken
    )
    {
        var (frontier, next) = direction == Direction.Forward ? ("FromSym", "ToSym") : ("ToSym", "FromSym");

        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_depth;", null, cancellationToken);
        await ExecNonQueryAsync(
            connection,
            "CREATE TEMP TABLE reach_depth(sym TEXT PRIMARY KEY, depth INTEGER NOT NULL);",
            null,
            cancellationToken
        );
        await ExecNonQueryAsync(connection, "CREATE INDEX temp.ix_reach_depth_depth ON reach_depth(depth);", null, cancellationToken);

        await ExecNonQueryAsync(
            connection,
            $"INSERT OR IGNORE INTO reach_depth(sym, depth) SELECT sym, 0 FROM ({SeedSql(hasNodes: true)});",
            command => AddLikeParam(command, pattern),
            cancellationToken
        );

        var handoffFilter = includeHandoff ? "" : " AND ce.Kind <> 'handoff'";
        for (var d = 0; d < maxDepth; d++)
        {
            var added = 0;
            added += await ExecNonQueryAsync(
                connection,
                $"INSERT OR IGNORE INTO reach_depth(sym, depth) "
                    + $"SELECT ce.{next}, {d + 1} FROM reach_depth r CROSS JOIN call_edges ce "
                    + $"WHERE ce.{frontier} = r.sym AND r.depth = {d}{handoffFilter};",
                null,
                cancellationToken
            );
            added += await ExecNonQueryAsync(
                connection,
                $"INSERT OR IGNORE INTO reach_depth(sym, depth) "
                    + $"SELECT de.{next}, {d + 1} FROM reach_depth r CROSS JOIN dispatch_edges de "
                    + $"WHERE de.{frontier} = r.sym AND r.depth = {d};",
                null,
                cancellationToken
            );
            if (added == 0)
            {
                break; // frontier exhausted before the depth cap — the whole closure is enumerated
            }
        }
    }

    // Builds the temp `reach_set` table = the directional closure of all pattern-matching symbols: the
    // FULL bounded closure (uncapped, handoff-INCLUSIVE superset — the in-memory ShapeGraph/traversal
    // re-applies sync-cut over it).
    //
    // It walks via the iterative level-BFS (BuildReachDepthAsync), NOT a recursive CTE. Why: a SQLite
    // recursive CTE may reference its own table only once, so the two edge tables had to be merged into a
    // single `edges(frontier,next)` UNION — which SQLite materialises into an AUTOMATIC COVERING INDEX over
    // the WHOLE call_edges ∪ dispatch_edges set (a full table SCAN of 600k+ wide rows) on EVERY query,
    // regardless of result size. That was the dominant per-query disk read (~1 GB, measured). The level-BFS
    // instead probes IX_call_edges_FromSym per frontier, so it touches only the closure's edges.
    private static async Task BuildReachSetAsync(
        DbConnection connection,
        string pattern,
        Direction direction,
        CancellationToken cancellationToken
    )
    {
        await BuildReachDepthAsync(connection, pattern, direction, maxDepth: int.MaxValue, includeHandoff: true, cancellationToken);

        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_set;", null, cancellationToken);
        await ExecNonQueryAsync(connection, "CREATE TEMP TABLE reach_set(sym TEXT PRIMARY KEY);", null, cancellationToken);
        await ExecNonQueryAsync(connection, "INSERT OR IGNORE INTO reach_set(sym) SELECT sym FROM reach_depth;", null, cancellationToken);

        // Give the planner statistics for the freshly-filled TEMP table. Without sqlite_stat1 for
        // reach_set, SQLite assumes it is large and INVERTS every `reference_facts JOIN reach_set`: it
        // SCANS reference_facts (millions of rows) probing the temp PK, instead of scanning the tiny
        // reach_set (hundreds of rows) and probing IX_reference_facts_EnclosingSymbolId. ANALYZE on the
        // temp table writes temp.sqlite_stat1 (allowed even on a Mode=ReadOnly main DB) and flips the
        // join order — measured ~300x on the bounded reference reads. Best-effort: if it fails the
        // queries still return correct results, just on the slower plan.
        try
        {
            await ExecNonQueryAsync(connection, "ANALYZE reach_set;", null, cancellationToken);
        }
        catch (DbException)
        {
            // statistics are an optimization only — ignore and run on the default plan
        }
    }

    private static string EscapeLike(string value) =>
        value.Replace(oldValue: "\\", newValue: "\\\\").Replace(oldValue: "%", newValue: "\\%").Replace(oldValue: "_", newValue: "\\_");

    private static async Task ReadAsync(
        DbConnection connection,
        string sql,
        Action<DbDataReader> onRow,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            onRow(reader);
        }
    }

    private static async Task<int> ExecNonQueryAsync(
        DbConnection connection,
        string sql,
        Action<DbCommand>? configure,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
