using System.Diagnostics;
using Microsoft.Data.Sqlite;

// rigq — slim, native-AOT read-only query over the derived call-graph views.
//
//   rigq reaches  <pattern> [--reverse] [--limit N] [--db PATH] [--count] [--time]
//   rigq index-nodes [--db PATH]      build the distinct-symbol seed index (one-time, opens RW)
//   rigq serve    [--db PATH] [--time]  REPL over stdin (emulated MCP-over-stdio): one warm process,
//                                       one request per line "reaches|callers <pattern>", count+latency out
//
// Closure walk over call_edges ∪ dispatch_edges via a recursive CTE. Forward = callees (reaches);
// reverse = callers. Seeds resolve from the `nodes` table (distinct symbol universe) when present —
// one small scan instead of four full edge-table scans for the leading-wildcard LIKE.

if (args.Length == 0)
    return Usage();
return args[0] switch
{
    "reaches" => RunReaches(args),
    "index-nodes" => RunIndexNodes(args),
    "serve" => RunServe(args),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("usage: rigq reaches <pattern> [--reverse] [--limit N] [--db PATH] [--count] [--time]");
    Console.Error.WriteLine("       rigq index-nodes [--db PATH]");
    Console.Error.WriteLine("       rigq serve [--db PATH] [--time]");
    return 2;
}

static int RunReaches(string[] args)
{
    if (args.Length < 2)
        return Usage();
    var pattern = args[1];
    var reverse = HasFlag(args, "--reverse");
    var countOnly = HasFlag(args, "--count");
    var time = HasFlag(args, "--time");
    var limit = TryGetInt(args, "--limit") ?? 200;
    var db = ResolveDb(args);
    if (db is null)
        return 2;

    var sw = Stopwatch.StartNew();
    using var connection = OpenReadOnly(db);
    var openMs = sw.Elapsed.TotalMilliseconds;
    var hasNodes = TableExists(connection, "nodes");

    using var command = connection.CreateCommand();
    command.CommandText = ReachSql(reverse, hasNodes);
    Bind(command, "$pat", "%" + EscapeLike(pattern) + "%");

    var total = 0;
    var shown = 0;
    using (var reader = command.ExecuteReader())
        while (reader.Read())
        {
            total++;
            if (!countOnly && shown < limit)
            {
                Console.Out.WriteLine(reader.GetString(0));
                shown++;
            }
        }
    sw.Stop();

    Console.Out.WriteLine(
        $"# {(reverse ? "callers" : "reaches")} '{pattern}': {total} symbol(s)"
            + (
                countOnly ? ""
                : shown < total ? $" ({shown} shown, raise --limit)"
                : ""
            )
    );
    if (time)
        Console.Error.WriteLine(
            $"# open {openMs:0.0}ms  query+read {sw.Elapsed.TotalMilliseconds:0.0}ms  seed={(hasNodes ? "nodes" : "edges")}"
        );
    return 0;
}

// Emulated MCP-over-stdio: keep ONE process + ONE open connection warm and answer a stream of
// requests, one per line ("reaches <pattern>" / "callers <pattern>"). This is exactly the shape an
// MCP server would take, so the per-line latency here is the steady-state cost a long-lived server
// would pay — startup fully amortised. Emits "<count>\t<ms>" per request; a summary to stderr at EOF.
static int RunServe(string[] args)
{
    var time = HasFlag(args, "--time");
    var db = ResolveDb(args);
    if (db is null)
        return 2;

    using var connection = OpenReadOnly(db);
    var hasNodes = TableExists(connection, "nodes");

    // Pre-create the two prepared statements once; reuse across the whole request stream.
    using var fwd = connection.CreateCommand();
    fwd.CommandText = ReachSql(reverse: false, hasNodes);
    Bind(fwd, "$pat", "");
    using var rev = connection.CreateCommand();
    rev.CommandText = ReachSql(reverse: true, hasNodes);
    Bind(rev, "$pat", "");
    fwd.Prepare();
    rev.Prepare();

    var requests = 0;
    var totalMs = 0.0;
    string? line;
    while ((line = Console.In.ReadLine()) is not null)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] == '#')
            continue;
        var sp = line.IndexOf(' ');
        if (sp <= 0)
        {
            Console.Out.WriteLine("ERR\tbad request");
            continue;
        }
        var dir = line[..sp];
        var pattern = line[(sp + 1)..].Trim().Trim('"');
        var cmd = dir switch
        {
            "reaches" => fwd,
            "callers" => rev,
            _ => null,
        };
        if (cmd is null)
        {
            Console.Out.WriteLine("ERR\tunknown direction");
            continue;
        }

        cmd.Parameters[0].Value = "%" + EscapeLike(pattern) + "%";
        var sw = Stopwatch.StartNew();
        var count = 0;
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                count++;
        sw.Stop();
        requests++;
        totalMs += sw.Elapsed.TotalMilliseconds;
        Console.Out.WriteLine($"{count}\t{sw.Elapsed.TotalMilliseconds:0.0}");
    }

    if (time && requests > 0)
        Console.Error.WriteLine(
            $"# served {requests} req in {totalMs:0}ms  avg {totalMs / requests:0.0}ms/req  {requests * 1000.0 / totalMs:0.0} req/s"
        );
    return 0;
}

// Builds `nodes` = the distinct symbol universe (every FromSym/ToSym across both edge tables) with a
// PK index. Seeds then scan this small deduped table once instead of four full edge-table scans.
// Opens read-write; idempotent. One-off after `rig graph` (or fold into GraphMaterializer later).
static int RunIndexNodes(string[] args)
{
    var db = ResolveDb(args);
    if (db is null)
        return 2;

    var sw = Stopwatch.StartNew();
    using var connection = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString()
    );
    connection.Open();

    Exec(connection, "DROP TABLE IF EXISTS nodes;");
    Exec(connection, "CREATE TABLE nodes(sym TEXT PRIMARY KEY) WITHOUT ROWID;");
    Exec(
        connection,
        """
        INSERT OR IGNORE INTO nodes(sym)
        SELECT FromSym FROM call_edges     UNION SELECT ToSym FROM call_edges
        UNION SELECT FromSym FROM dispatch_edges UNION SELECT ToSym FROM dispatch_edges
        UNION SELECT SymbolId FROM symbol_facts WHERE Kind = 'method';
        """
    );
    Exec(connection, "ANALYZE nodes;");

    using var c = connection.CreateCommand();
    c.CommandText = "SELECT count(*) FROM nodes;";
    var n = Convert.ToInt64(c.ExecuteScalar());
    sw.Stop();
    Console.Out.WriteLine($"# nodes: {n} distinct symbol(s) in {sw.Elapsed.TotalMilliseconds:0}ms");
    return 0;
}

// Recursive closure. Seeds from `nodes` (one scan) when available, else the four edge-column LIKEs.
// The recursive part joins the BASE tables directly (one term per table) so each uses its FromSym/
// ToSym index, rather than a unioned `edges` CTE that SQLite would materialise.
static string ReachSql(bool reverse, bool hasNodes)
{
    var (frontier, next) = reverse ? ("ToSym", "FromSym") : ("FromSym", "ToSym");
    var seeds = hasNodes
        ? "SELECT sym FROM nodes WHERE sym LIKE $pat ESCAPE '\\'"
        : """
            SELECT FromSym FROM call_edges     WHERE FromSym LIKE $pat ESCAPE '\'
            UNION SELECT ToSym FROM call_edges WHERE ToSym   LIKE $pat ESCAPE '\'
            UNION SELECT FromSym FROM dispatch_edges WHERE FromSym LIKE $pat ESCAPE '\'
            UNION SELECT ToSym FROM dispatch_edges   WHERE ToSym   LIKE $pat ESCAPE '\'
            """;
    return $"""
        WITH RECURSIVE
        seeds(sym) AS ({seeds}),
        reach(sym) AS (
            SELECT sym FROM seeds
            UNION
            SELECT ce.{next} FROM call_edges     ce JOIN reach ON ce.{frontier} = reach.sym
            UNION
            SELECT de.{next} FROM dispatch_edges de JOIN reach ON de.{frontier} = reach.sym
        )
        SELECT sym FROM reach;
        """;
}

static SqliteConnection OpenReadOnly(string db)
{
    var conn = new SqliteConnection(
        new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()
    );
    conn.Open();
    return conn;
}

static bool TableExists(SqliteConnection conn, string table)
{
    using var c = conn.CreateCommand();
    c.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$n LIMIT 1;";
    Bind(c, "$n", table);
    return c.ExecuteScalar() is not null;
}

static void Exec(SqliteConnection conn, string sql)
{
    using var c = conn.CreateCommand();
    c.CommandText = sql;
    c.ExecuteNonQuery();
}

static void Bind(SqliteCommand command, string name, object value)
{
    var p = command.CreateParameter();
    p.ParameterName = name;
    p.Value = value;
    command.Parameters.Add(p);
}

static string? ResolveDb(string[] args)
{
    var db = GetOption(args, "--db") ?? Path.Combine(Directory.GetCurrentDirectory(), ".rig", "rig.db");
    if (File.Exists(db))
        return db;
    Console.Error.WriteLine($"no .rig store at {db}");
    return null;
}

static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
static string? GetOption(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
static int? TryGetInt(string[] args, string name) => int.TryParse(GetOption(args, name), out var v) ? v : null;
static string EscapeLike(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
