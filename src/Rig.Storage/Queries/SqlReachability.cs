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
    public enum Direction { Forward, Reverse }

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
        RigDbContext context, IReadOnlyCollection<string> seeds, Direction direction,
        CancellationToken cancellationToken = default)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (seeds.Count == 0)
            return result;

        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = BuildSetCte(command, seeds, direction);
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(reader.GetString(0));
        return result;
    }

    // Forward: follow FromSym -> ToSym (callees + dispatch targets). Reverse: follow ToSym -> FromSym
    // (callers + reverse dispatch). A single recursive term joins the recursion frontier against a
    // UNION ALL of both edge tables, so the FromSym/ToSym indexes drive the walk.
    private static string BuildSetCte(DbCommand command, IReadOnlyCollection<string> seeds, Direction direction)
    {
        var (frontierCol, nextCol) = direction == Direction.Forward
            ? ("FromSym", "ToSym")
            : ("ToSym", "FromSym");

        var seedValues = new StringBuilder();
        var i = 0;
        foreach (var seed in seeds)
        {
            if (i > 0) seedValues.Append(", ");
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
        RigDbContext context, string pattern, Direction direction, CancellationToken cancellationToken = default)
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
        IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line)> ThrowRefs);

    public static async Task<ReachInputs> LoadReachInputsAsync(
        RigDbContext context, string pattern, Direction direction, CancellationToken cancellationToken = default)
    {
        var connection = await OpenAsync(context, cancellationToken).ConfigureAwait(false);
        await BuildReachSetAsync(connection, pattern, direction, cancellationToken).ConfigureAwait(false);
        var graph = await LoadGraphFromReachSetAsync(connection, direction, cancellationToken).ConfigureAwait(false);

        var invocations = new List<FactInvocation>();
        await ReadAsync(connection,
            """
            SELECT r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.ReceiverType,
                   r.FirstArgumentTemplate, r.FirstArgumentType, r.EnclosingLoopKind, r.EnclosingLoopDetail,
                   r.EnclosingInvocations, r.EnclosingCatchTypes
            FROM reference_facts r JOIN reach_set s ON r.EnclosingSymbolId = s.sym
            WHERE r.RefKind = 'invocation';
            """,
            reader => invocations.Add(new FactInvocation(
                reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2), reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10))),
            cancellationToken).ConfigureAwait(false);

        // ctor refs: dedup by (FilePath, Line), matching LoadFactEntryPointDataAsync.
        var ctorByLoc = new Dictionary<(string, int), (string, string?, string, int)>();
        await ReadAsync(connection,
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
            cancellationToken).ConfigureAwait(false);

        // throw refs: dedup by (FilePath, Line, Target), matching LoadThrowRefsAsync.
        var throwByKey = new Dictionary<(string, int, string), (string, string?, string, int)>();
        await ReadAsync(connection,
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
            cancellationToken).ConfigureAwait(false);

        return new ReachInputs(graph, invocations, ctorByLoc.Values.ToArray(), throwByKey.Values.ToArray());
    }

    // Loads the bounded FactGraphData from the already-built reach_set temp table. Forward joins
    // out-edges (FromSym in set); reverse joins in-edges (ToSym in set) for the reverse BFS.
    private static async Task<FactGraphData> LoadGraphFromReachSetAsync(
        DbConnection connection, Direction direction, CancellationToken cancellationToken)
    {
        var edgeJoinCol = direction == Direction.Forward ? "FromSym" : "ToSym";

        var callEdges = new List<CallEdge>();
        await ReadAsync(connection,
            $"""
            SELECT c.FromSym, c.ToSym, c.Kind, c.FilePath, c.Line, c.LoopKind, c.LoopDetail
            FROM call_edges c JOIN reach_set r ON c.{edgeJoinCol} = r.sym;
            """,
            reader => callEdges.Add(new CallEdge(
                reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetString(3),
                reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6))),
            cancellationToken).ConfigureAwait(false);

        var methodById = new Dictionary<string, MethodRef>(StringComparer.Ordinal);
        await ReadAsync(connection,
            "SELECT s.SymbolId, s.Name, s.ContainingSymbolId, s.IsOverride " +
            "FROM symbol_facts s JOIN reach_set r ON s.SymbolId = r.sym WHERE s.Kind = 'method';",
            reader =>
            {
                var id = reader.GetString(0);
                if (!methodById.ContainsKey(id))
                    methodById[id] = new MethodRef(
                        id, reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        !reader.IsDBNull(3) && reader.GetInt32(3) != 0);
            },
            cancellationToken).ConfigureAwait(false);

        var implEdges = new HashSet<ImplementsEdge>();
        var baseEdges = new HashSet<BaseEdge>();
        await ReadAsync(connection,
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
            cancellationToken).ConfigureAwait(false);

        return new FactGraphData(callEdges, implEdges.ToArray(), methodById.Values.ToArray(), baseEdges.ToArray());
    }

    // Builds the temp `reach_set` table = the directional closure of all pattern-matching symbols, in
    // a single recursive CTE seeded straight from the LIKE match (no C# round-trip for the seeds).
    private static async Task BuildReachSetAsync(
        DbConnection connection, string pattern, Direction direction, CancellationToken cancellationToken)
    {
        var (frontierCol, nextCol) = direction == Direction.Forward
            ? ("FromSym", "ToSym")
            : ("ToSym", "FromSym");

        await ExecNonQueryAsync(connection, "DROP TABLE IF EXISTS reach_set;", null, cancellationToken).ConfigureAwait(false);
        await ExecNonQueryAsync(connection, "CREATE TEMP TABLE reach_set(sym TEXT PRIMARY KEY);", null, cancellationToken).ConfigureAwait(false);

        var like = "%" + EscapeLike(pattern) + "%";
        await ExecNonQueryAsync(connection,
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
            cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static async Task ReadAsync(
        DbConnection connection, string sql, Action<DbDataReader> onRow, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            onRow(reader);
    }

    private static async Task ExecNonQueryAsync(
        DbConnection connection, string sql, Action<DbCommand>? configure, CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DbConnection> OpenAsync(RigDbContext context, CancellationToken cancellationToken)
    {
        var connection = (DbConnection)context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
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
