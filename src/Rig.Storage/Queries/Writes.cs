using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

public static class Writes
{
    // fastBulkWrite trades crash durability for speed (journal/fsync off). It is the DEFAULT, because
    // it is safe for a single exclusive writer producing a throwaway-until-published DB (rig index,
    // which writes to a temp file and atomically renames on success — a corrupt temp is never
    // published). Callers OPT OUT (set false) for consistency: mine's in-place PARALLEL appends, or a
    // user-requested `--durable` in-place index. progress, when set, reports batched save throughput.
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
                await context.Database.ExecuteSqlRawAsync(pragma, cancellationToken);
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
        return runId;
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

        for (var i = 0; i < symbols.Count; i++)
        {
            var s = symbols[i];
            context.SymbolFacts.Add(
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
            saved++;
            if (++pending >= FactBatchSize)
                await FlushAsync();
        }

        for (var i = 0; i < references.Count; i++)
        {
            var r = references[i];
            context.ReferenceFacts.Add(
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
                }
            );
            saved++;
            if (++pending >= FactBatchSize)
                await FlushAsync();
        }

        for (var i = 0; i < relations.Count; i++)
        {
            var t = relations[i];
            context.TypeRelationFacts.Add(
                new TypeRelationFactEntity
                {
                    RunId = runId,
                    TypeRelationFactIndex = i,
                    TypeSymbolId = t.TypeSymbolId,
                    RelatedSymbolId = t.RelatedSymbolId,
                    RelationKind = t.RelationKind,
                }
            );
            saved++;
            if (++pending >= FactBatchSize)
                await FlushAsync();
        }

        for (var i = 0; i < dispatch.Count; i++)
        {
            var d = dispatch[i];
            context.DispatchFacts.Add(
                new DispatchFactEntity
                {
                    RunId = runId,
                    DispatchFactIndex = i,
                    SourceMember = d.SourceMember,
                    TargetMember = d.TargetMember,
                    Kind = d.Kind,
                }
            );
            saved++;
            if (++pending >= FactBatchSize)
                await FlushAsync();
        }

        if (pending > 0)
            await FlushAsync();
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
