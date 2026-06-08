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
            EntryPointCount = result.EntryPoints.Count,
            EffectCount = result.Effects.Count,
            DiRegistrationCount = result.DiRegistrations.Count,
            MethodObservationCount = result.MethodObservations.Count,
            InvocationObservationCount = result.InvocationObservations.Count,
        };

        context.Runs.Add(run);
        AddSourceFiles(context, runId, result);
        AddEntryPoints(context, runId, result);
        AddEffects(context, runId, result);
        AddDiRegistrations(context, runId, result);
        AddMethodObservations(context, runId, result);
        AddInvocationObservations(context, runId, result);
        AddCallGraphs(context, runId, result.Effects, result);
        AddFacts(context, runId, result);

        // Populate cross-run symbol index when a project identity is set.
        // Upsert so re-indexing a project updates the run pointer for each symbol.
        if (result.ProjectIdentity is not null)
            UpsertSymbolIndex(context, runId, result.ProjectIdentity, result);

        await context.SaveChangesAsync(cancellationToken);
        return runId;
    }

    private static void AddEntryPoints(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.EntryPoints.Count; index++)
        {
            var entryPoint = result.EntryPoints[index];
            context.EntryPoints.Add(
                new EntryPointEntity
                {
                    RunId = runId,
                    EntryPointIndex = index,
                    Kind = entryPoint.Kind,
                    Method = entryPoint.Method,
                    Route = entryPoint.Route,
                    DisplayName = entryPoint.DisplayName,
                    FilePath = entryPoint.FilePath,
                    Line = entryPoint.Line,
                }
            );
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

    private static void AddEffects(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var effectIndex = 0; effectIndex < result.Effects.Count; effectIndex++)
        {
            var effect = result.Effects[effectIndex];
            context.Effects.Add(
                new EffectEntity
                {
                    RunId = runId,
                    EffectIndex = effectIndex,
                    Provider = effect.Provider,
                    Operation = effect.Operation,
                    Resource = effect.Resource,
                    Method = effect.Method,
                    FilePath = effect.FilePath,
                    Line = effect.Line,
                    Confidence = effect.Confidence,
                    Basis = effect.Basis,
                    Reason = effect.Reason,
                }
            );

            for (var observationIndex = 0; observationIndex < effect.Observations.Count; observationIndex++)
            {
                var observation = effect.Observations[observationIndex];
                context.EffectObservations.Add(
                    new EffectObservationEntity
                    {
                        RunId = runId,
                        EffectIndex = effectIndex,
                        ObservationIndex = observationIndex,
                        Type = observation.Type,
                        Context = observation.Context,
                        Detail = observation.Detail,
                        Confidence = observation.Confidence,
                        Basis = observation.Basis,
                        Reason = observation.Reason,
                    }
                );
            }
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

    private static void AddMethodObservations(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.MethodObservations.Count; index++)
        {
            var observation = result.MethodObservations[index];
            context.MethodObservations.Add(
                new MethodObservationEntity
                {
                    RunId = runId,
                    MethodIndex = index,
                    Symbol = observation.Symbol,
                    DisplayName = observation.DisplayName,
                    FilePath = observation.FilePath,
                    Line = observation.Line,
                    ProjectName = observation.ProjectName,
                }
            );
        }
    }

    private static void AddInvocationObservations(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.InvocationObservations.Count; index++)
        {
            var observation = result.InvocationObservations[index];
            context.InvocationObservations.Add(
                new InvocationObservationEntity
                {
                    RunId = runId,
                    InvocationIndex = index,
                    ContainingMethodSymbol = observation.ContainingMethodSymbol,
                    TargetSymbol = observation.TargetSymbol,
                    TargetDisplayName = observation.TargetDisplayName,
                    FilePath = observation.FilePath,
                    Line = observation.Line,
                    Confidence = observation.Confidence,
                    Basis = observation.Basis,
                    Reason = observation.Reason,
                }
            );
        }
    }

    private static void AddCallGraphs(RigDbContext context, string runId, IReadOnlyList<EffectInfo> effects, AnalysisResult result)
    {
        var effectIndexByIdentity = effects.Select((e, i) => (e, i)).ToDictionary(pair => pair.e, pair => pair.i);

        for (var graphIndex = 0; graphIndex < result.CallGraphs.Count; graphIndex++)
        {
            var graph = result.CallGraphs[graphIndex];
            context.CallGraphs.Add(
                new CallGraphEntity
                {
                    RunId = runId,
                    GraphIndex = graphIndex,
                    EntryPoint = graph.EntryPoint,
                }
            );

            for (var nodeIndex = 0; nodeIndex < graph.Nodes.Count; nodeIndex++)
            {
                var node = graph.Nodes[nodeIndex];
                context.CallGraphNodes.Add(
                    new CallGraphNodeEntity
                    {
                        RunId = runId,
                        GraphIndex = graphIndex,
                        NodeIndex = nodeIndex,
                        Symbol = node.Symbol,
                        FilePath = node.FilePath,
                        Line = node.Line,
                        Confidence = node.Confidence,
                        Basis = node.Basis,
                        Reason = node.Reason,
                    }
                );

                for (var callIndex = 0; callIndex < node.Calls.Count; callIndex++)
                {
                    context.CallGraphNodeCalls.Add(
                        new CallGraphNodeCallEntity
                        {
                            RunId = runId,
                            GraphIndex = graphIndex,
                            NodeIndex = nodeIndex,
                            CallIndex = callIndex,
                            TargetSymbol = node.Calls[callIndex],
                        }
                    );
                }

                for (var boundaryCallIndex = 0; boundaryCallIndex < node.BoundaryCalls.Count; boundaryCallIndex++)
                {
                    var boundaryCall = node.BoundaryCalls[boundaryCallIndex];
                    context.CallGraphBoundaryCalls.Add(
                        new CallGraphBoundaryCallEntity
                        {
                            RunId = runId,
                            GraphIndex = graphIndex,
                            NodeIndex = nodeIndex,
                            BoundaryCallIndex = boundaryCallIndex,
                            Kind = boundaryCall.Kind,
                            Target = boundaryCall.Target,
                            Method = boundaryCall.Method,
                            FilePath = boundaryCall.FilePath,
                            Line = boundaryCall.Line,
                            Confidence = boundaryCall.Confidence,
                            Basis = boundaryCall.Basis,
                            Reason = boundaryCall.Reason,
                        }
                    );
                }

                for (var linkIndex = 0; linkIndex < node.Effects.Count; linkIndex++)
                {
                    if (!effectIndexByIdentity.TryGetValue(node.Effects[linkIndex], out var effectIndex))
                    {
                        continue;
                    }

                    context.CallGraphNodeEffects.Add(
                        new CallGraphNodeEffectEntity
                        {
                            RunId = runId,
                            GraphIndex = graphIndex,
                            NodeIndex = nodeIndex,
                            LinkIndex = linkIndex,
                            EffectIndex = effectIndex,
                        }
                    );
                }
            }
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
            CREATE TABLE IF NOT EXISTS symbol_index (
                ProjectIdentity TEXT NOT NULL,
                Symbol          TEXT NOT NULL,
                RunId           TEXT NOT NULL,
                FilePath        TEXT NOT NULL,
                Line            INTEGER NOT NULL,
                PRIMARY KEY (ProjectIdentity, Symbol)
            );
            """, cancellationToken);

        await context.Database.ExecuteSqlRawAsync("""
            CREATE INDEX IF NOT EXISTS IX_symbol_index_Symbol ON symbol_index(Symbol);
            """, cancellationToken);

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

    private static void UpsertSymbolIndex(RigDbContext context, string runId, string projectIdentity, AnalysisResult result)
    {
        // Index every distinct source method from MethodObservations.
        // Upsert semantics: if a prior run already has this (identity, symbol) we overwrite it
        // with the latest run so the most recently indexed source wins.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obs in result.MethodObservations)
        {
            if (!seen.Add(obs.Symbol))
                continue;

            // ExecuteUpdate / raw SQL would be faster but EF Core doesn't support SQLite UPSERT
            // portably without raw SQL in EFCore 8.  Use Find + Add/Update pattern instead.
            var existing = context.SymbolIndex.Find(projectIdentity, obs.Symbol);
            if (existing is null)
            {
                context.SymbolIndex.Add(new SymbolIndexEntity
                {
                    ProjectIdentity = projectIdentity,
                    Symbol = obs.Symbol,
                    RunId = runId,
                    FilePath = obs.FilePath,
                    Line = obs.Line,
                });
            }
            else
            {
                existing.RunId = runId;
                existing.FilePath = obs.FilePath;
                existing.Line = obs.Line;
            }
        }
    }
}
