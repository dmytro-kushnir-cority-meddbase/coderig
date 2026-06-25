using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

public static class Writes
{
    // fastBulkWrite trades crash durability for speed (journal/fsync off). It is the DEFAULT, because
    // it is safe for a single exclusive writer producing a throwaway-until-published DB (rig index,
    // which writes to a temp file and atomically renames on success — a corrupt temp is never
    // published). It is turned OFF (set false) for the in-place APPEND path that writes the live DB
    // directly — `--merge` and mine's `--identity` (potentially parallel) appends, which must keep the
    // journal. progress, when set, reports batched save throughput.
    // The required state for merging into a store: the assembly registry must exist. We DON'T migrate
    // old stores (no ALTER clutter) — a store that predates multi-solution support is required to be
    // re-mined. EnsureCreated builds these tables on any fresh index. See docs/multi-solution-storage.md.
    public static async Task<bool> HasAssemblyRegistryAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        return await StorageProbes.TableExistsAsync(connection, "assemblies", cancellationToken);
    }

    // BEFORE an in-place APPEND (`--merge` / mine's `--identity`) into an EXISTING store: reject a store
    // whose stamped index schema doesn't match the version this rig writes. Appending current-shaped facts
    // into an old-shaped store would silently mix shapes; the store is disposable, so the fix is a re-index,
    // not an append. A store with NO meta row (predates schema stamping) is left alone here — the existing
    // assembly-registry guard already requires such a store to be re-mined for `--merge`, and SaveAsync will
    // stamp the current version on the way out. Throws RigStoreException on a genuine version mismatch.
    public static async Task AssertAppendableAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);
        var (index, _) = await SchemaMeta.ReadAsync(connection, cancellationToken);
        if (index is not null && index != SchemaVersion.Index)
        {
            throw new RigStoreException(
                $"Store schema v{index}, this rig writes v{SchemaVersion.Index} — re-index, don't append (the .rig store is disposable; rebuild it with `rig index`)."
            );
        }
    }

    public static async Task<string> SaveAsync(
        RigDbContext context,
        AnalysisResult result,
        CancellationToken cancellationToken = default,
        Action<string>? progress = null,
        GitProvenance? provenance = null
    )
    {
        var runId = Guid.NewGuid().ToString("n");
        await context.Database.EnsureCreatedAsync(cancellationToken);
        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);

        const string pragma = 
                     """
                     PRAGMA journal_mode=OFF;
                     PRAGMA synchronous=OFF;
                     PRAGMA temp_store=MEMORY;
                     PRAGMA mmap_size=4294967296;
                     PRAGMA cache_size=-262144;
                     PRAGMA locking_mode=EXCLUSIVE;
                     """;
        
        await context.Database.ExecuteSqlRawAsync(pragma, cancellationToken);

        // Bulk insert: skip per-Add change detection (we never mutate tracked entities) and flush in
        // batches, clearing the tracker each time so memory stays flat over millions of fact rows.
        context.ChangeTracker.AutoDetectChangesEnabled = false;

        var run = new RunEntity
        {
            Id = runId,
            CreatedAtUtcText = DateTimeOffset.UtcNow.ToString("O"),
            SolutionPath = Path.GetFullPath(result.SolutionPath),
            ProjectIdentity = result.ProjectIdentity,
            SourceProjectPath = result.SourceProjectPath is not null ? Path.GetFullPath(result.SourceProjectPath) : null,
            SymbolCount = result.Symbols?.Count ?? 0,
            ReferenceCount = result.References?.Count ?? 0,
            DiRegistrationCount = result.DiRegistrations.Count,
            SourceCommit = provenance?.Commit,
            SourceBranch = provenance?.Branch,
            SourceDirty = provenance?.Dirty ?? false,
        };

        // Header rows first (small) — flushed and detached before the fact batches start clearing
        // the tracker, so they aren't dropped by a later ChangeTracker.Clear().
        context.Runs.Add(run);
        AddSourceFiles(context, runId, result);
        AddDiRegistrations(context, runId, result);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();

        await SaveFactsBatchedAsync(context, runId, result, progress, cancellationToken);
        try
        {
            await WriteAssemblyRegistryAsync(context, result, progress, cancellationToken);
        }
        catch (Exception exception)
        {
            // The assembly registry is metadata (multi-solution dedup/membership). It must NEVER lose a
            // completed fact write — degrade to a warning and leave the facts intact.
            progress?.Invoke($"WARN: assembly registry skipped ({exception.GetType().Name}: {exception.Message})");
        }

        // Stamp the DB-file schema version now the facts are written: index=current, graph=NULL. A fresh
        // index (or an append) invalidates any prior graph, so the graph stage resets to "absent" until
        // GraphMaterializer re-stamps it. This is the row the read-time SchemaGate checks; SchemaMeta is
        // raw SQL on the same connection EF holds open. NOT a migration — a tripwire (see SchemaGate).
        await SchemaMeta.WriteIndexVersionAsync(connection, cancellationToken);
        return runId;
    }

    // Populates the assembly registry + solution membership (docs/multi-solution-storage.md). An
    // assembly is keyed by name and content-addressed by a digest over its emitted FACT identities
    // (symbol DocIDs + reference target/enclosing/line), so a re-index of the same source is a no-op and
    // a changed assembly is detected. Membership records that this solution contains the assembly.
    // Additive: runs alongside the run-scoped fact write and does not change existing query behaviour.
    private static async Task WriteAssemblyRegistryAsync(
        RigDbContext context,
        AnalysisResult result,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        var symbols = result.Symbols ?? [];
        if (symbols.Count == 0)
        {
            return;
        }

        var references = result.References ?? [];
        var solutionPath = Path.GetFullPath(result.SolutionPath);
        var indexedAt = DateTimeOffset.UtcNow.ToString("O");

        // SymbolId -> owning assembly, so a reference is attributed to the assembly of its enclosing method.
        var symbolAssembly = new Dictionary<string, string>(symbols.Count, StringComparer.Ordinal);
        foreach (var s in symbols)
        {
            symbolAssembly[s.SymbolId] = s.DefiningAssembly;
        }

        // Stream each assembly's fact identities into an order-independent XOR+sum digest, hashing and
        // discarding per item. O(#assemblies) retained memory — the earlier list-based version
        // materialised millions of item strings on top of the in-memory fact arrays and OOM'd a 2M+
        // reference store. Order-independence (XOR/+ are commutative) makes a re-mine idempotent.
        var acc = new Dictionary<string, AssemblyAccumulator>(StringComparer.Ordinal);
        AssemblyAccumulator For(string assembly)
        {
            if (!acc.TryGetValue(assembly, out var a))
            {
                acc[assembly] = a = new AssemblyAccumulator();
            }

            return a;
        }

        foreach (var s in symbols)
        {
            if (string.IsNullOrEmpty(s.DefiningAssembly))
            {
                continue;
            }

            var a = For(s.DefiningAssembly);
            a.Fold("S:" + s.SymbolId);
            a.Symbols++;
        }

        foreach (var r in references)
        {
            if (
                r.EnclosingSymbolId is null
                || !symbolAssembly.TryGetValue(r.EnclosingSymbolId, out var assembly)
                || string.IsNullOrEmpty(assembly)
            )
            {
                continue;
            }

            var a = For(assembly);
            a.Fold($"R:{r.TargetSymbolId}|{r.EnclosingSymbolId}|{r.Line}");
            a.References++;
        }

        // Re-enable change detection for this small upsert (the fact write left it off + tracker cleared).
        context.ChangeTracker.AutoDetectChangesEnabled = true;
        var existing = await context.Assemblies.ToDictionaryAsync(a => a.AssemblyName, cancellationToken);
        var existingMembership = new HashSet<string>(
            await context
                .SolutionMemberships.Where(m => m.SolutionPath == solutionPath)
                .Select(m => m.AssemblyName)
                .ToListAsync(cancellationToken),
            StringComparer.Ordinal
        );

        foreach (var (assembly, a) in acc)
        {
            var hash = a.ContentHash();
            if (existing.TryGetValue(assembly, out var row))
            {
                if (row.ContentHash != hash)
                {
                    // Same name, divergent content. Expected for a re-mine of the same solution; a
                    // genuine cross-solution collision (a fork) would carry a different source solution.
                    if (!string.Equals(row.SourceSolutionPath, solutionPath, StringComparison.OrdinalIgnoreCase))
                    {
                        progress?.Invoke(
                            $"WARN: assembly '{assembly}' has divergent content across solutions ('{row.SourceSolutionPath}' vs '{solutionPath}') — possible fork; keeping latest"
                        );
                    }

                    row.ContentHash = hash;
                    row.SymbolCount = a.Symbols;
                    row.ReferenceCount = a.References;
                    row.IndexedAtUtcText = indexedAt;
                }
            }
            else
            {
                context.Assemblies.Add(
                    new AssemblyEntity
                    {
                        AssemblyName = assembly,
                        ContentHash = hash,
                        IndexedAtUtcText = indexedAt,
                        SymbolCount = a.Symbols,
                        ReferenceCount = a.References,
                        SourceSolutionPath = solutionPath,
                    }
                );
            }

            if (existingMembership.Add(assembly))
            {
                context.SolutionMemberships.Add(new SolutionMembershipEntity { SolutionPath = solutionPath, AssemblyName = assembly });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
        progress?.Invoke($"Registered {acc.Count} assemblies for {Path.GetFileName(solutionPath)}");
    }

    // Order-independent, streaming content digest of an assembly's fact identities. Each item's SHA-256 is
    // folded by XOR (commutative) + a running 64-bit sum (catches the XOR self-cancellation of an exact
    // duplicate pair); item count rides along. Constant memory per assembly — no item retention.
    private sealed class AssemblyAccumulator
    {
        private readonly byte[] xor = new byte[32];
        private ulong sum;
        private long count;

        public int Symbols;
        public int References;

        public void Fold(string item)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(item));
            for (var i = 0; i < 32; i++)
            {
                xor[i] ^= hash[i];
            }

            sum += BitConverter.ToUInt64(hash);
            count++;
        }

        public string ContentHash()
        {
            Span<byte> final = stackalloc byte[32 + sizeof(ulong) + sizeof(long)];
            xor.CopyTo(final);
            BitConverter.TryWriteBytes(final[32..], sum);
            BitConverter.TryWriteBytes(final[(32 + sizeof(ulong))..], count);
            return Convert.ToHexString(SHA256.HashData(final));
        }
    }

    private const int FactProgressInterval = 50_000;

    private static readonly string[] FactTableNames = ["symbol_facts", "reference_facts", "type_relation_facts", "dispatch_facts"];

    // Bulk-inserts the symbol/reference/type-relation/dispatch facts over RAW ADO — a single reused
    // prepared INSERT per table (param.Value reset per row, as GraphMaterializer/EntryPointSiteStore do),
    // inside ONE transaction. This bypasses the EF change tracker + per-entity allocation that
    // Add/SaveChanges pay even with AutoDetectChanges off, and Microsoft.Data.Sqlite's "async" is
    // synchronous under the hood, so the hot loop calls ExecuteNonQuery() directly (no per-row Task).
    //
    // On the fresh standalone path (fastBulkWrite) the secondary indexes are DROPPED first and rebuilt
    // once at the end — otherwise every one of ~2M rows maintains every secondary index as it lands.
    // The in-place append path (--merge / mine) keeps its indexes: the table already holds prior writers'
    // rows, so a drop + global rebuild would be slower and is unsafe under concurrent writers.
    private static async Task SaveFactsBatchedAsync(
        RigDbContext context,
        string runId,
        AnalysisResult result,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        var symbols = result.Symbols ?? [];
        var references = result.References ?? [];
        var relations = result.TypeRelations ?? [];
        var dispatch = result.DispatchFacts ?? [];
        long total = symbols.Count + references.Count + relations.Count + dispatch.Count;
        long saved = 0;

        var connection = await StorageProbes.OpenConnectionAsync(context, cancellationToken);

        var deferredIndexes = await DropSecondaryIndexesAsync(connection, FactTableNames, cancellationToken);

        await using (var transaction = await connection.BeginTransactionAsync(cancellationToken))
        {
            saved += InsertRows(
                connection,
                transaction,
                "INSERT INTO symbol_facts (RunId, SymbolFactIndex, SymbolId, Kind, Name, Namespace, ContainingSymbolId, "
                    + "Modifiers, TypeKind, Signature, FilePath, Line, EndLine, DefiningAssembly, IsOverride, BodyHash) "
                    + "VALUES ($run,$idx,$sid,$kind,$name,$ns,$containing,$mods,$tk,$sig,$file,$line,$endline,$asm,$ovr,$bh);",
                [
                    "$run",
                    "$idx",
                    "$sid",
                    "$kind",
                    "$name",
                    "$ns",
                    "$containing",
                    "$mods",
                    "$tk",
                    "$sig",
                    "$file",
                    "$line",
                    "$endline",
                    "$asm",
                    "$ovr",
                    "$bh",
                ],
                symbols,
                (p, s, i) =>
                {
                    p[0].Value = runId;
                    p[1].Value = i;
                    p[2].Value = s.SymbolId;
                    p[3].Value = s.Kind;
                    p[4].Value = s.Name;
                    p[5].Value = s.Namespace;
                    p[6].Value = (object?)s.ContainingSymbolId ?? DBNull.Value;
                    p[7].Value = s.Modifiers;
                    p[8].Value = s.TypeKind;
                    p[9].Value = s.Signature;
                    p[10].Value = s.FilePath;
                    p[11].Value = s.Line;
                    p[12].Value = s.EndLine;
                    p[13].Value = s.DefiningAssembly;
                    p[14].Value = s.IsOverride ? 1 : 0;
                    p[15].Value = s.BodyHash;
                },
                alreadySaved: saved,
                total: total,
                progress,
                cancellationToken
            );

            saved += InsertRows(
                connection,
                transaction,
                "INSERT INTO reference_facts (RunId, ReferenceFactIndex, TargetSymbolId, RefKind, EnclosingSymbolId, TargetAssembly, "
                    + "TargetInSource, FilePath, Line, ReceiverType, FirstArgumentTemplate, FirstArgumentType, EnclosingLoopKind, "
                    + "EnclosingLoopDetail, EnclosingInvocations, EnclosingCatchTypes, TypeArguments, FirstArgumentName, DelegateConsumer, "
                    + "EnclosingScopes, ArgumentTemplates, ArgumentNames, DeclaringTypeArgBinding, MethodTypeArgBinding, NonVirtual) "
                    + "VALUES ($run,$idx,$target,$kind,$enc,$tasm,$insrc,$file,$line,$recv,$fat,$fatype,$elk,$eld,$einv,$ect,$ta,$fan,$dc,"
                    + "$es,$at,$an,$dtab,$mtab,$nonvirtual);",
                [
                    "$run",
                    "$idx",
                    "$target",
                    "$kind",
                    "$enc",
                    "$tasm",
                    "$insrc",
                    "$file",
                    "$line",
                    "$recv",
                    "$fat",
                    "$fatype",
                    "$elk",
                    "$eld",
                    "$einv",
                    "$ect",
                    "$ta",
                    "$fan",
                    "$dc",
                    "$es",
                    "$at",
                    "$an",
                    "$dtab",
                    "$mtab",
                    "$nonvirtual",
                ],
                references,
                (p, r, i) =>
                {
                    p[0].Value = runId;
                    p[1].Value = i;
                    p[2].Value = r.TargetSymbolId;
                    p[3].Value = r.RefKind;
                    p[4].Value = (object?)r.EnclosingSymbolId ?? DBNull.Value;
                    p[5].Value = r.TargetAssembly;
                    p[6].Value = r.TargetInSource ? 1 : 0;
                    p[7].Value = r.FilePath;
                    p[8].Value = r.Line;
                    p[9].Value = (object?)r.ReceiverType ?? DBNull.Value;
                    p[10].Value = (object?)r.FirstArgumentTemplate ?? DBNull.Value;
                    p[11].Value = (object?)r.FirstArgumentType ?? DBNull.Value;
                    p[12].Value = (object?)r.EnclosingLoopKind ?? DBNull.Value;
                    p[13].Value = (object?)r.EnclosingLoopDetail ?? DBNull.Value;
                    p[14].Value = (object?)r.EnclosingInvocations ?? DBNull.Value;
                    p[15].Value = (object?)r.EnclosingCatchTypes ?? DBNull.Value;
                    p[16].Value = (object?)r.TypeArguments ?? DBNull.Value;
                    p[17].Value = (object?)r.FirstArgumentName ?? DBNull.Value;
                    p[18].Value = (object?)r.DelegateConsumer ?? DBNull.Value;
                    p[19].Value = (object?)r.EnclosingScopes ?? DBNull.Value;
                    p[20].Value = (object?)r.ArgumentTemplates ?? DBNull.Value;
                    p[21].Value = (object?)r.ArgumentNames ?? DBNull.Value;
                    p[22].Value = (object?)r.DeclaringTypeArgBinding ?? DBNull.Value;
                    p[23].Value = (object?)r.MethodTypeArgBinding ?? DBNull.Value;
                    p[24].Value = r.NonVirtual ? 1 : 0;
                },
                alreadySaved: saved,
                total: total,
                progress,
                cancellationToken
            );

            saved += InsertRows(
                connection,
                transaction,
                "INSERT INTO type_relation_facts (RunId, TypeRelationFactIndex, TypeSymbolId, RelatedSymbolId, RelationKind) "
                    + "VALUES ($run,$idx,$type,$related,$kind);",
                ["$run", "$idx", "$type", "$related", "$kind"],
                relations,
                (p, t, i) =>
                {
                    p[0].Value = runId;
                    p[1].Value = i;
                    p[2].Value = t.TypeSymbolId;
                    p[3].Value = t.RelatedSymbolId;
                    p[4].Value = t.RelationKind;
                },
                alreadySaved: saved,
                total: total,
                progress,
                cancellationToken
            );

            saved += InsertRows(
                connection,
                transaction,
                "INSERT INTO dispatch_facts (RunId, DispatchFactIndex, SourceMember, TargetMember, Kind) "
                    + "VALUES ($run,$idx,$src,$tgt,$kind);",
                ["$run", "$idx", "$src", "$tgt", "$kind"],
                dispatch,
                (p, d, i) =>
                {
                    p[0].Value = runId;
                    p[1].Value = i;
                    p[2].Value = d.SourceMember;
                    p[3].Value = d.TargetMember;
                    p[4].Value = d.Kind;
                },
                alreadySaved: saved,
                total: total,
                progress,
                cancellationToken
            );

            await transaction.CommitAsync(cancellationToken);
        }

        progress?.Invoke($"Saved {saved}/{total} fact rows");

        if (deferredIndexes.Count > 0)
        {
            progress?.Invoke($"Rebuilding {deferredIndexes.Count} fact index(es)");
            foreach (var sql in deferredIndexes)
            {
                await ExecuteAsync(connection, sql, cancellationToken);
            }
        }
    }

    // One reused prepared INSERT, stepped once per row with param values reset in place. Synchronous
    // ExecuteNonQuery — Microsoft.Data.Sqlite runs the "async" variant synchronously anyway, so awaiting
    // per row would only add a Task allocation. Returns the rows written; reports cumulative progress.
    private static long InsertRows<T>(
        DbConnection connection,
        DbTransaction transaction,
        string insertSql,
        string[] parameterNames,
        IReadOnlyList<T> items,
        Action<DbParameter[], T, int> bind,
        long alreadySaved,
        long total,
        Action<string>? progress,
        CancellationToken cancellationToken
    )
    {
        if (items.Count == 0)
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = insertSql;
        var parameters = new DbParameter[parameterNames.Length];
        for (var i = 0; i < parameterNames.Length; i++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterNames[i];
            command.Parameters.Add(parameter);
            parameters[i] = parameter;
        }

        command.Prepare();

        for (var i = 0; i < items.Count; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            bind(parameters, items[i], i);
            command.ExecuteNonQuery();

            var cumulative = alreadySaved + i + 1;
            if (cumulative % FactProgressInterval == 0)
            {
                progress?.Invoke($"Saved {cumulative}/{total} fact rows");
            }
        }

        return items.Count;
    }

    // Drops (and returns the CREATE statements of) every secondary index on the given tables, so a bulk
    // load doesn't maintain them per row; the caller rebuilds each once afterwards by re-running the
    // returned SQL. PRIMARY-KEY auto-indexes have a NULL `sql` in sqlite_master and are excluded, so the
    // (RunId, *Index) uniqueness stays enforced throughout the load. `tables` are fixed constants.
    private static async Task<List<string>> DropSecondaryIndexesAsync(
        DbConnection connection,
        string[] tables,
        CancellationToken cancellationToken
    )
    {
        var recreateSql = new List<string>();
        var indexNames = new List<string>();
        var inList = string.Join(", ", tables.Select(t => $"'{t}'"));

        await using (var query = connection.CreateCommand())
        {
            query.CommandText = $"SELECT name, sql FROM sqlite_master WHERE type='index' AND sql IS NOT NULL AND tbl_name IN ({inList});";
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                indexNames.Add(reader.GetString(0));
                recreateSql.Add(reader.GetString(1));
            }
        }

        foreach (var name in indexNames)
        {
            await ExecuteAsync(connection, $"DROP INDEX IF EXISTS \"{name}\";", cancellationToken);
        }

        return recreateSql;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSourceFiles(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.SourceFiles.Count; index++)
        {
            var sourceFile = result.SourceFiles[index];
            context.SourceFiles.Add(
                new SourceFileEntity
                {
                    RunId = runId,
                    FileIndex = index,
                    ProjectName = sourceFile.ProjectName,
                    FilePath = sourceFile.FilePath,
                    Status = sourceFile.Status,
                    Confidence = sourceFile.Confidence,
                    Basis = sourceFile.Basis,
                    Reason = sourceFile.Reason,
                    Evidence = sourceFile.Evidence,
                }
            );
        }
    }

    private static void AddDiRegistrations(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.DiRegistrations.Count; index++)
        {
            var registration = result.DiRegistrations[index];
            context.DiRegistrations.Add(
                new DiRegistrationEntity
                {
                    RunId = runId,
                    RegistrationIndex = index,
                    ServiceType = registration.ServiceType,
                    ImplementationType = registration.ImplementationType,
                    Lifetime = registration.Lifetime,
                    RegistrationKind = registration.RegistrationKind,
                    FilePath = registration.FilePath,
                    Line = registration.Line,
                    Confidence = registration.Confidence,
                    Basis = registration.Basis,
                    Reason = registration.Reason,
                    Evidence = registration.Evidence,
                }
            );
        }
    }
}
