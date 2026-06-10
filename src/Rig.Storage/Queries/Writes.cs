using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Storage.Storage;

namespace Rig.Storage.Queries;

public static class Writes
{
    public static async Task<string> SaveAsync(RigDbContext context, AnalysisResult result, CancellationToken cancellationToken = default)
    {
        var runId = Guid.NewGuid().ToString("n");

        await context.Database.EnsureCreatedAsync(cancellationToken);
        await MigrateAsync(context, cancellationToken);

        var run = new RunEntity
        {
            Id = runId,
            CreatedAtUtcText = DateTimeOffset.UtcNow.ToString("O"),
            SolutionPath = Path.GetFullPath(result.SolutionPath),
            ProjectIdentity = result.ProjectIdentity,
            SourceProjectPath = result.SourceProjectPath is not null
                ? Path.GetFullPath(result.SourceProjectPath)
                : null,
            SymbolCount = result.Symbols?.Count ?? 0,
            ReferenceCount = result.References?.Count ?? 0,
            DiRegistrationCount = result.DiRegistrations.Count,
        };

        context.Runs.Add(run);
        AddSourceFiles(context, runId, result);
        AddDiRegistrations(context, runId, result);
        AddFacts(context, runId, result);

        await context.SaveChangesAsync(cancellationToken);
        return runId;
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

    private static void AddFacts(RigDbContext context, string runId, AnalysisResult result)
    {
        var symbols = result.Symbols ?? [];
        for (var i = 0; i < symbols.Count; i++)
        {
            var s = symbols[i];
            context.SymbolFacts.Add(new SymbolFactEntity
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
            });
        }

        var references = result.References ?? [];
        for (var i = 0; i < references.Count; i++)
        {
            var r = references[i];
            context.ReferenceFacts.Add(new ReferenceFactEntity
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
            });
        }

        var relations = result.TypeRelations ?? [];
        for (var i = 0; i < relations.Count; i++)
        {
            var t = relations[i];
            context.TypeRelationFacts.Add(new TypeRelationFactEntity
            {
                RunId = runId,
                TypeRelationFactIndex = i,
                TypeSymbolId = t.TypeSymbolId,
                RelatedSymbolId = t.RelatedSymbolId,
                RelationKind = t.RelationKind,
            });
        }
    }

    // Additive migrations for databases created before new columns/tables were introduced.
    // EnsureCreatedAsync only creates tables in a brand-new DB — it never alters existing ones.
    private static async Task MigrateAsync(RigDbContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync("""
            ALTER TABLE runs ADD COLUMN IF NOT EXISTS ProjectIdentity TEXT;
            """, cancellationToken).ContinueWith(_ => { }, cancellationToken); // ignore if already exists

        await context.Database.ExecuteSqlRawAsync("""
            ALTER TABLE runs ADD COLUMN IF NOT EXISTS SourceProjectPath TEXT;
            """, cancellationToken).ContinueWith(_ => { }, cancellationToken);

        await context.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_runs_ProjectIdentity ON runs(ProjectIdentity);
            """, cancellationToken).ContinueWith(_ => { }, cancellationToken);

        await context.Database.ExecuteSqlRawAsync("""
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
            """, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_symbol_facts_SymbolId ON symbol_facts(SymbolId);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_symbol_facts_Name ON symbol_facts(Name);", cancellationToken);

        await context.Database.ExecuteSqlRawAsync("""
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
                PRIMARY KEY (RunId, ReferenceFactIndex)
            );
            """, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_reference_facts_TargetSymbolId ON reference_facts(TargetSymbolId);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_reference_facts_EnclosingSymbolId ON reference_facts(EnclosingSymbolId);", cancellationToken);

        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS type_relation_facts (
                RunId                 TEXT NOT NULL,
                TypeRelationFactIndex INTEGER NOT NULL,
                TypeSymbolId          TEXT NOT NULL,
                RelatedSymbolId       TEXT NOT NULL,
                RelationKind          TEXT NOT NULL,
                PRIMARY KEY (RunId, TypeRelationFactIndex)
            );
            """, cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_type_relation_facts_TypeSymbolId ON type_relation_facts(TypeSymbolId);", cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_type_relation_facts_RelatedSymbolId ON type_relation_facts(RelatedSymbolId);", cancellationToken);
    }
}
