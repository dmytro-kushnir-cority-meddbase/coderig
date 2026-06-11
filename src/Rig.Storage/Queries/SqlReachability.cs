using System.Data.Common;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
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

    // True when the derived edge views exist and are non-empty — i.e. `rig graph` has been run over
    // this store. Callers fall back to the EF path (or prompt to run `rig graph`) when false.
    public static async Task<bool> HasGraphAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        if (!await TableExistsAsync(connection, "call_edges", cancellationToken).ConfigureAwait(false))
            return false;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM call_edges LIMIT 1);";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(result) != 0;
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
            return result;

        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = BuildSetCte(command, seeds, direction, includeHandoff);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(reader.GetString(0));
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
        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        var hasNodes = await TableExistsAsync(connection, "nodes", cancellationToken).ConfigureAwait(false);
        var (frontier, next) = direction == Direction.Forward ? ("FromSym", "ToSym") : ("ToSym", "FromSym");

        // Iterative level-BFS in a temp table rather than one min-depth recursive CTE. A single CTE that
        // carries depth must dedup on (sym, depth), so a node reachable by many routes expands at up to
        // maxDepth distinct depths before a final GROUP BY min — a ~10× blow-up. Here each level expands
        // ONLY the current frontier (depth = d) and INSERT OR IGNORE keeps the first (shortest) depth per
        // sym, so total work is O(closure edges) — the same as the depth-less set walk, but with exact
        // depth. Two inserts per level (one per edge table) so each uses its FromSym/ToSym index.
        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_depth;", null, cancellationToken).ConfigureAwait(false);
        await ExecNonQueryAsync(
                connection,
                "CREATE TEMP TABLE reach_depth(sym TEXT PRIMARY KEY, depth INTEGER NOT NULL);",
                null,
                cancellationToken
            )
            .ConfigureAwait(false);
        await ExecNonQueryAsync(connection, "CREATE INDEX temp.ix_reach_depth_depth ON reach_depth(depth);", null, cancellationToken)
            .ConfigureAwait(false);

        await ExecNonQueryAsync(
                connection,
                $"INSERT OR IGNORE INTO reach_depth(sym, depth) SELECT sym, 0 FROM ({SeedSql(hasNodes)});",
                command => AddLikeParam(command, pattern),
                cancellationToken
            )
            .ConfigureAwait(false);

        var handoffFilter = includeHandoff ? "" : " AND ce.Kind <> 'handoff'";
        for (var d = 0; d < maxDepth; d++)
        {
            var added = 0;
            added += await ExecNonQueryAsync(
                    connection,
                    $"INSERT OR IGNORE INTO reach_depth(sym, depth) "
                        + $"SELECT ce.{next}, {d + 1} FROM call_edges ce JOIN reach_depth r ON ce.{frontier} = r.sym WHERE r.depth = {d}{handoffFilter};",
                    null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            added += await ExecNonQueryAsync(
                    connection,
                    $"INSERT OR IGNORE INTO reach_depth(sym, depth) "
                        + $"SELECT de.{next}, {d + 1} FROM dispatch_edges de JOIN reach_depth r ON de.{frontier} = r.sym WHERE r.depth = {d};",
                    null,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (added == 0)
                break; // frontier exhausted before the depth cap — the whole closure is enumerated
        }

        await ReadAsync(
                connection,
                "SELECT sym, depth FROM reach_depth;",
                reader => result[reader.GetString(0)] = reader.GetInt32(1),
                cancellationToken
            )
            .ConfigureAwait(false);
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
        var reachable = await ReachedWithDepthAsync(context, pattern, Direction.Reverse, maxDepth, includeHandoff, cancellationToken)
            .ConfigureAwait(false);
        if (reachable.Count == 0)
            return [];

        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_set;", null, cancellationToken).ConfigureAwait(false);
        await ExecNonQueryAsync(connection, "CREATE TEMP TABLE reach_set(sym TEXT PRIMARY KEY);", null, cancellationToken)
            .ConfigureAwait(false);
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT OR IGNORE INTO reach_set(sym) VALUES ($s);";
            var p = insert.CreateParameter();
            p.ParameterName = "$s";
            insert.Parameters.Add(p);
            foreach (var sym in reachable.Keys)
            {
                p.Value = sym;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

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
            )
            .ConfigureAwait(false);
        return roots;
    }

    // Seed selection. With the `nodes` table (built by GraphMaterializer = distinct edge endpoints ∪
    // symbol_facts methods) one indexed scan matches FactPathFinder's index.Nodes universe. Without it,
    // fall back to the four edge-column LIKEs plus symbol_facts methods (BuildReachSetAsync's seeds).
    private static string SeedSql(bool hasNodes) =>
        hasNodes
            ? "SELECT sym FROM nodes WHERE sym LIKE $pat ESCAPE '\\'"
            : """
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
                seedValues.Append(", ");
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
        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        await BuildReachSetAsync(connection, pattern, direction, cancellationToken).ConfigureAwait(false);
        return await LoadGraphFromReachSetAsync(connection, direction, cancellationToken).ConfigureAwait(false);
    }

    // Everything reaches/tree need to reproduce their exact output, bounded to the closure: the
    // subgraph PLUS the effect-derivation inputs (invocations / ctor refs / throw refs whose ENCLOSING
    // method is in the closure). Built off the single reach_set so the whole-codebase invocation scan
    // (the part that dominated reaches/tree latency) is replaced by a bounded, indexed join. Base edges
    // come from the graph (all base edges), so LoadFactEntryPointDataAsync — heavy and otherwise only
    // used for its base edges + ctor refs here — is skipped entirely.
    public sealed record ReachInputs(
        FactGraphData Graph,
        IReadOnlyList<FactInvocation> Invocations,
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)> CtorRefs,
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)> ThrowRefs
    );

    public static async Task<ReachInputs> LoadReachInputsAsync(
        RigDbContext context,
        string pattern,
        Direction direction,
        CancellationToken cancellationToken = default
    )
    {
        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        await BuildReachSetAsync(connection, pattern, direction, cancellationToken).ConfigureAwait(false);
        var graph = await LoadGraphFromReachSetAsync(connection, direction, cancellationToken).ConfigureAwait(false);

        var invocations = new List<FactInvocation>();
        await ReadAsync(
                connection,
                """
                SELECT r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.ReceiverType,
                       r.FirstArgumentTemplate, r.FirstArgumentType, r.EnclosingLoopKind, r.EnclosingLoopDetail,
                       r.EnclosingInvocations, r.EnclosingCatchTypes, r.TypeArguments, r.FirstArgumentName
                FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
                WHERE r.RefKind = 'invocation';
                """,
                reader =>
                    invocations.Add(
                        new FactInvocation(
                            reader.GetString(0),
                            reader.IsDBNull(1) ? null : reader.GetString(1),
                            reader.IsDBNull(2) ? "" : reader.GetString(2),
                            reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7),
                            reader.IsDBNull(8) ? null : reader.GetString(8),
                            reader.IsDBNull(9) ? null : reader.GetString(9),
                            reader.IsDBNull(10) ? null : reader.GetString(10),
                            reader.IsDBNull(11) ? null : reader.GetString(11),
                            reader.IsDBNull(12) ? null : reader.GetString(12)
                        )
                    ),
                cancellationToken
            )
            .ConfigureAwait(false);

        // ctor refs: dedup by (FilePath, Line), matching LoadFactEntryPointDataAsync.
        var ctorByLoc = new Dictionary<(string, int), (string, string?, string, int)>();
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
                    ctorByLoc.TryAdd((file, line), (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), file, line));
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        // throw refs: dedup by (FilePath, Line, Target), matching LoadThrowRefsAsync.
        var throwByKey = new Dictionary<(string, int, string), (string, string?, string, int)>();
        await ReadAsync(
                connection,
                """
                SELECT r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line
                FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
                WHERE r.RefKind = 'throw';
                """,
                reader =>
                {
                    var target = reader.GetString(0);
                    var file = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    var line = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
                    throwByKey.TryAdd((file, line, target), (target, reader.IsDBNull(1) ? null : reader.GetString(1), file, line));
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return new ReachInputs(graph, invocations, ctorByLoc.Values.ToArray(), throwByKey.Values.ToArray());
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

        // ReceiverType drives the in-memory edge-aware dispatch narrowing. Old stores (built before the
        // column existed and never re-`rig graph`-ed) lack it — probe so the SELECT degrades to a null
        // receiver (full CHA) instead of throwing.
        var hasReceiver = await ColumnExistsAsync(connection, "call_edges", "ReceiverType", cancellationToken).ConfigureAwait(false);
        var receiverSelect = hasReceiver ? "c.ReceiverType" : "NULL";
        // HandoffDispatcher rides along so the bounded in-memory graph carries the async-handoff
        // classification — the in-memory FactPathFinder applies the sync-cut / --async filter over the
        // (superset) bounded graph, so this column is what lets it tell handoff edges apart. Null on a
        // store built before classification (degrades to no handoffs = the pre-async behavior).
        var hasHandoff = await ColumnExistsAsync(connection, "call_edges", "HandoffDispatcher", cancellationToken).ConfigureAwait(false);
        var handoffSelect = hasHandoff ? "c.HandoffDispatcher" : "NULL";

        // Call-site generic type arguments aren't stored on call_edges; they live on reference_facts
        // (B1 capture) and drive generic-dispatch narrowing in the in-memory FactPathFinder. Load them in
        // ONE bulk pass keyed by (caller, callee, line) and attach in memory — NOT a per-edge correlated
        // subquery, which is pathological here: reach_set is the receiver-BLIND CHA superset, so even a
        // tiny query's bounded graph carries every fanned-out edge, and a per-edge subquery then runs
        // thousands of times. The bulk query is bounded to reach_set on the caller-side index and returns
        // only the (few) generic call sites. Probed so a store predating the column degrades to no
        // narrowing (full CHA). Non-generic / synthesized dispatch edges have no entry -> null.
        var hasTypeArgs = await ColumnExistsAsync(connection, "reference_facts", "TypeArguments", cancellationToken).ConfigureAwait(false);
        var typeArgsByEdge = new Dictionary<(string, string, int), string>();
        if (hasTypeArgs)
            await ReadAsync(
                    connection,
                    $"""
                    SELECT rf.EnclosingSymbolId, rf.TargetSymbolId, rf.Line, rf.TypeArguments
                    FROM reference_facts rf JOIN reach_set r ON rf.EnclosingSymbolId = r.sym
                    WHERE rf.TypeArguments IS NOT NULL;
                    """,
                    reader =>
                    {
                        var key = (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
                        typeArgsByEdge[key] = reader.GetString(3); // first wins; dup (caller,callee,line) is vanishingly rare
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);

        var callEdges = new List<CallEdge>();
        await ReadAsync(
                connection,
                $"""
                SELECT c.FromSym, c.ToSym, c.Kind, c.FilePath, c.Line, c.LoopKind, c.LoopDetail, {receiverSelect}, {handoffSelect}
                FROM call_edges c JOIN reach_set r ON c.{edgeJoinCol} = r.sym;
                """,
                reader =>
                {
                    var from = reader.GetString(0);
                    var to = reader.GetString(1);
                    var line = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
                    typeArgsByEdge.TryGetValue((from, to, line), out var typeArgs);
                    callEdges.Add(
                        new CallEdge(
                            from,
                            to,
                            reader.GetString(2),
                            reader.IsDBNull(3) ? "" : reader.GetString(3),
                            line,
                            reader.IsDBNull(5) ? null : reader.GetString(5),
                            reader.IsDBNull(6) ? null : reader.GetString(6),
                            reader.IsDBNull(7) ? null : reader.GetString(7),
                            reader.IsDBNull(8) ? null : reader.GetString(8),
                            typeArgs
                        )
                    );
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        var methodById = new Dictionary<string, MethodRef>(StringComparer.Ordinal);
        await ReadAsync(
                connection,
                "SELECT s.SymbolId, s.Name, s.ContainingSymbolId, s.IsOverride, s.FilePath, s.Line "
                    + "FROM symbol_facts s JOIN reach_set r ON s.SymbolId = r.sym WHERE s.Kind = 'method';",
                reader =>
                {
                    var id = reader.GetString(0);
                    if (!methodById.ContainsKey(id))
                        methodById[id] = new MethodRef(
                            id,
                            reader.GetString(1),
                            reader.IsDBNull(2) ? null : reader.GetString(2),
                            !reader.IsDBNull(3) && reader.GetInt32(3) != 0,
                            reader.IsDBNull(4) ? null : reader.GetString(4),
                            reader.IsDBNull(5) ? 0 : reader.GetInt32(5)
                        );
                },
                cancellationToken
            )
            .ConfigureAwait(false);

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
                        implEdges.Add(new ImplementsEdge(type, related));
                    else
                        baseEdges.Add(new BaseEdge(type, related));
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        // Mined dispatch facts ride into the bounded graph so the in-memory FactPathFinder resolves
        // dispatch exact-first over it, matching the full-graph oracle (and the materialised
        // dispatch_edges, which are built from the same facts). Loaded WHOLE like the type relations —
        // the table is tiny relative to reference_facts, and an unbounded superset can only agree with
        // the full graph. Probed: a pre-dispatch-facts store has no table → null → CHA fallback,
        // exactly matching its dispatch_edges (also built heuristic-only).
        IReadOnlyList<DispatchFact>? minedDispatch = null;
        if (await TableExistsAsync(connection, "dispatch_facts", cancellationToken).ConfigureAwait(false))
        {
            var mined = new HashSet<DispatchFact>();
            await ReadAsync(
                    connection,
                    "SELECT DISTINCT SourceMember, TargetMember, Kind FROM dispatch_facts;",
                    reader => mined.Add(new DispatchFact(reader.GetString(0), reader.GetString(1), reader.GetString(2))),
                    cancellationToken
                )
                .ConfigureAwait(false);
            minedDispatch = mined.ToArray();
        }

        return new FactGraphData(callEdges, implEdges.ToArray(), methodById.Values.ToArray(), baseEdges.ToArray(), minedDispatch);
    }

    // Builds the temp `reach_set` table = the directional closure of all pattern-matching symbols, in
    // a single recursive CTE seeded straight from the LIKE match (no C# round-trip for the seeds).
    private static async Task BuildReachSetAsync(
        DbConnection connection,
        string pattern,
        Direction direction,
        CancellationToken cancellationToken
    )
    {
        var (frontierCol, nextCol) = direction == Direction.Forward ? ("FromSym", "ToSym") : ("ToSym", "FromSym");

        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_set;", null, cancellationToken).ConfigureAwait(false);
        await ExecNonQueryAsync(connection, "CREATE TEMP TABLE reach_set(sym TEXT PRIMARY KEY);", null, cancellationToken)
            .ConfigureAwait(false);

        var like = "%" + EscapeLike(pattern) + "%";
        await ExecNonQueryAsync(
                connection,
                $"""
                INSERT OR IGNORE INTO reach_set(sym)
                WITH RECURSIVE
                seeds(sym) AS (
                    SELECT FromSym FROM call_edges WHERE FromSym LIKE $pat ESCAPE '\'
                    UNION SELECT ToSym FROM call_edges WHERE ToSym LIKE $pat ESCAPE '\'
                    UNION SELECT FromSym FROM dispatch_edges WHERE FromSym LIKE $pat ESCAPE '\'
                    UNION SELECT ToSym FROM dispatch_edges WHERE ToSym LIKE $pat ESCAPE '\'
                    UNION SELECT SymbolId FROM symbol_facts WHERE Kind = 'method' AND SymbolId LIKE $pat ESCAPE '\'
                ),
                edges(frontier, next) AS (
                    SELECT {frontierCol}, {nextCol} FROM call_edges
                    UNION ALL
                    SELECT {frontierCol}, {nextCol} FROM dispatch_edges
                ),
                reach(sym) AS (
                    SELECT sym FROM seeds
                    UNION
                    SELECT edges.next FROM edges JOIN reach ON edges.frontier = reach.sym
                )
                SELECT sym FROM reach;
                """,
                command =>
                {
                    var p = command.CreateParameter();
                    p.ParameterName = "$pat";
                    p.Value = like;
                    command.Parameters.Add(p);
                },
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task ReadAsync(
        DbConnection connection,
        string sql,
        Action<DbDataReader> onRow,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            onRow(reader);
    }

    private static async Task<int> ExecNonQueryAsync(
        DbConnection connection,
        string sql,
        Action<DbCommand>? configure,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DbConnection> OpenAsync(RigDbContext context, CancellationToken cancellationToken)
    {
        var connection = (DbConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> ColumnExistsAsync(
        DbConnection connection,
        string table,
        string column,
        CancellationToken cancellationToken
    )
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table});";
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static async Task<bool> TableExistsAsync(DbConnection connection, string table, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name LIMIT 1;";
        var p = command.CreateParameter();
        p.ParameterName = "$name";
        p.Value = table;
        command.Parameters.Add(p);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }
}
