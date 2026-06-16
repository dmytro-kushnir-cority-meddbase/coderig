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

    public static async Task<string> SaveAsync(
        RigDbContext context,
        AnalysisResult result,
        CancellationToken cancellationToken = default,
        bool fastBulkWrite = true,
        Action<string>? progress = null
    )
    {
        var runId = Guid.NewGuid().ToString("n");

        await context.Database.EnsureCreatedAsync(cancellationToken);
        await MigrateAsync(context, cancellationToken);

        if (fastBulkWrite)
        {
            // No rollback journal, no fsync, in-memory temp, 64 MB page cache, single-writer lock.
            // A crash mid-write corrupts this file — acceptable because the caller publishes via
            // atomic rename, so the live store is never the one being written.
            foreach (
                var pragma in new[]
                {
                    "PRAGMA journal_mode=OFF;",
                    "PRAGMA synchronous=OFF;",
                    "PRAGMA temp_store=MEMORY;",
                    "PRAGMA cache_size=-65536;",
                    "PRAGMA locking_mode=EXCLUSIVE;",
                }
            )
            {
                await context.Database.ExecuteSqlRawAsync(pragma, cancellationToken);
            }
        }

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

    private const int FactBatchSize = 20_000;

    // Inserts the symbol/reference/type-relation facts in fixed-size batches, flushing + clearing the
    // change tracker per batch and reporting cumulative progress. One SaveChanges over millions of
    // tracked entities is both slow and memory-heavy; batching keeps both bounded.
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
        var pending = 0;

        async Task FlushAsync()
        {
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
            pending = 0;
            progress?.Invoke($"Saved {saved}/{total} fact rows");
        }

        // The four fact tables share one batching policy (fixed-size flush + tracker clear, cumulative
        // progress); only the per-row entity mapping differs. This local function captures the shared
        // `saved`/`pending`/`total` counters so the control flow lives in exactly one place — the call
        // sites supply just the (item, index) -> entity projection.
        async Task AddAllAsync<TSource, TEntity>(IReadOnlyList<TSource> items, Func<TSource, int, TEntity> map)
            where TEntity : class
        {
            for (var i = 0; i < items.Count; i++)
            {
                context.Add(map(items[i], i));
                saved++;
                if (++pending >= FactBatchSize)
                {
                    await FlushAsync();
                }
            }
        }

        await AddAllAsync(
            symbols,
            (s, i) =>
                new SymbolFactEntity
                {
                    RunId = runId,
                    SymbolFactIndex = i,
                    SymbolId = s.SymbolId,
                    Kind = s.Kind,
                    Name = s.Name,
                    Namespace = s.Namespace,
                    ContainingSymbolId = s.ContainingSymbolId,
                    Modifiers = s.Modifiers,
                    TypeKind = s.TypeKind,
                    Signature = s.Signature,
                    FilePath = s.FilePath,
                    Line = s.Line,
                    DefiningAssembly = s.DefiningAssembly,
                    IsOverride = s.IsOverride,
                }
        );

        await AddAllAsync(
            references,
            (r, i) =>
                new ReferenceFactEntity
                {
                    RunId = runId,
                    ReferenceFactIndex = i,
                    TargetSymbolId = r.TargetSymbolId,
                    RefKind = r.RefKind,
                    EnclosingSymbolId = r.EnclosingSymbolId,
                    TargetAssembly = r.TargetAssembly,
                    TargetInSource = r.TargetInSource,
                    FilePath = r.FilePath,
                    Line = r.Line,
                    ReceiverType = r.ReceiverType,
                    FirstArgumentTemplate = r.FirstArgumentTemplate,
                    FirstArgumentType = r.FirstArgumentType,
                    EnclosingLoopKind = r.EnclosingLoopKind,
                    EnclosingLoopDetail = r.EnclosingLoopDetail,
                    EnclosingInvocations = r.EnclosingInvocations,
                    EnclosingCatchTypes = r.EnclosingCatchTypes,
                    TypeArguments = r.TypeArguments,
                    FirstArgumentName = r.FirstArgumentName,
                    DelegateConsumer = r.DelegateConsumer,
                    EnclosingScopes = r.EnclosingScopes,
                    ArgumentTemplates = r.ArgumentTemplates,
                    ArgumentNames = r.ArgumentNames,
                    DeclaringTypeArgBinding = r.DeclaringTypeArgBinding,
                    MethodTypeArgBinding = r.MethodTypeArgBinding,
                }
        );

        await AddAllAsync(
            relations,
            (t, i) =>
                new TypeRelationFactEntity
                {
                    RunId = runId,
                    TypeRelationFactIndex = i,
                    TypeSymbolId = t.TypeSymbolId,
                    RelatedSymbolId = t.RelatedSymbolId,
                    RelationKind = t.RelationKind,
                }
        );

        await AddAllAsync(
            dispatch,
            (d, i) =>
                new DispatchFactEntity
                {
                    RunId = runId,
                    DispatchFactIndex = i,
                    SourceMember = d.SourceMember,
                    TargetMember = d.TargetMember,
                    Kind = d.Kind,
                }
        );

        if (pending > 0)
        {
            await FlushAsync();
        }
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

    // Additive migrations for databases created before new columns/tables were introduced.
    // EnsureCreatedAsync only creates tables in a brand-new DB — it never alters existing ones.
    private static async Task MigrateAsync(RigDbContext context, CancellationToken cancellationToken)
    {
        await context
            .Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE reference_facts ADD COLUMN IF NOT EXISTS TypeArguments TEXT;
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken); // ignore if already exists

        await context
            .Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE reference_facts ADD COLUMN IF NOT EXISTS FirstArgumentName TEXT;
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken);

        await context
            .Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE reference_facts ADD COLUMN IF NOT EXISTS DelegateConsumer TEXT;
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken);

        await context
            .Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE reference_facts ADD COLUMN IF NOT EXISTS EnclosingScopes TEXT;
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken);

        await context
            .Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE runs ADD COLUMN IF NOT EXISTS ProjectIdentity TEXT;
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken); // ignore if already exists

        await context
            .Database.ExecuteSqlRawAsync(
                """
                ALTER TABLE runs ADD COLUMN IF NOT EXISTS SourceProjectPath TEXT;
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken);

        await context
            .Database.ExecuteSqlRawAsync(
                """
                CREATE INDEX IF NOT EXISTS IX_runs_ProjectIdentity ON runs(ProjectIdentity);
                """,
                cancellationToken
            )
            .ContinueWith(_ => { }, cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS symbol_facts (
                RunId              TEXT NOT NULL,
                SymbolFactIndex    INTEGER NOT NULL,
                SymbolId           TEXT NOT NULL,
                Kind               TEXT NOT NULL,
                Name               TEXT NOT NULL,
                Namespace          TEXT NOT NULL,
                ContainingSymbolId TEXT,
                Modifiers          TEXT NOT NULL,
                TypeKind           TEXT NOT NULL,
                Signature          TEXT NOT NULL,
                FilePath           TEXT NOT NULL,
                Line               INTEGER NOT NULL,
                DefiningAssembly   TEXT NOT NULL,
                IsOverride         INTEGER NOT NULL,
                PRIMARY KEY (RunId, SymbolFactIndex)
            );
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_symbol_facts_SymbolId ON symbol_facts(SymbolId);",
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_symbol_facts_Name ON symbol_facts(Name);",
            cancellationToken
        );

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS reference_facts (
                RunId              TEXT NOT NULL,
                ReferenceFactIndex INTEGER NOT NULL,
                TargetSymbolId     TEXT NOT NULL,
                RefKind            TEXT NOT NULL,
                EnclosingSymbolId  TEXT,
                TargetAssembly     TEXT NOT NULL,
                TargetInSource     INTEGER NOT NULL,
                FilePath           TEXT NOT NULL,
                Line               INTEGER NOT NULL,
                ReceiverType       TEXT,
                FirstArgumentTemplate TEXT,
                FirstArgumentType  TEXT,
                EnclosingLoopKind  TEXT,
                EnclosingLoopDetail TEXT,
                EnclosingInvocations TEXT,
                EnclosingCatchTypes TEXT,
                TypeArguments      TEXT,
                FirstArgumentName  TEXT,
                DelegateConsumer   TEXT,
                EnclosingScopes    TEXT,
                ArgumentTemplates  TEXT,
                ArgumentNames      TEXT,
                DeclaringTypeArgBinding TEXT,
                MethodTypeArgBinding TEXT,
                PRIMARY KEY (RunId, ReferenceFactIndex)
            );
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_reference_facts_TargetSymbolId ON reference_facts(TargetSymbolId);",
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_reference_facts_EnclosingSymbolId ON reference_facts(EnclosingSymbolId);",
            cancellationToken
        );

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS type_relation_facts (
                RunId                 TEXT NOT NULL,
                TypeRelationFactIndex INTEGER NOT NULL,
                TypeSymbolId          TEXT NOT NULL,
                RelatedSymbolId       TEXT NOT NULL,
                RelationKind          TEXT NOT NULL,
                PRIMARY KEY (RunId, TypeRelationFactIndex)
            );
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_type_relation_facts_TypeSymbolId ON type_relation_facts(TypeSymbolId);",
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_type_relation_facts_RelatedSymbolId ON type_relation_facts(RelatedSymbolId);",
            cancellationToken
        );

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS dispatch_facts (
                RunId             TEXT NOT NULL,
                DispatchFactIndex INTEGER NOT NULL,
                SourceMember      TEXT NOT NULL,
                TargetMember      TEXT NOT NULL,
                Kind              TEXT NOT NULL,
                PRIMARY KEY (RunId, DispatchFactIndex)
            );
            """,
            cancellationToken
        );
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_dispatch_facts_SourceMember ON dispatch_facts(SourceMember);",
            cancellationToken
        );
    }
}
