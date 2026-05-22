using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rig.Analysis;
using Rig.Storage;

namespace Rig.Cli;

internal sealed class RunStore(string workingDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string storeDirectory = Path.Combine(workingDirectory, ".rig");

    public async Task<string> SaveAsync(
        string solutionPath,
        AnalysisResult result,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(storeDirectory);

        var runId = Guid.NewGuid().ToString("n");
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsurePrototypeSchemaAsync(context, cancellationToken);

        var run = new RunEntity
        {
            Id = runId,
            CreatedAtUtcText = DateTimeOffset.UtcNow.ToString("O"),
            SolutionPath = Path.GetFullPath(solutionPath),
            EntryPointCount = result.EntryPoints.Count,
            EffectCount = result.Effects.Count,
            DiRegistrationCount = result.DiRegistrations.Count,
            MethodObservationCount = result.MethodObservations.Count,
            InvocationObservationCount = result.InvocationObservations.Count,
            AnalysisResultJson = JsonSerializer.Serialize(result, JsonOptions)
        };

        context.Runs.Add(run);
        AddSourceFiles(context, runId, result);
        AddEntryPoints(context, runId, result);
        AddEffects(context, runId, result);
        AddDiRegistrations(context, runId, result);
        AddMethodObservations(context, runId, result);
        AddInvocationObservations(context, runId, result);
        AddCallGraphs(context, runId, result);

        await context.SaveChangesAsync(cancellationToken);
        return runId;
    }

    public async Task<AnalysisResult?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return null;
        }

        var run = await context.Runs
            .OrderByDescending(run => run.CreatedAtUtcText)
            .ThenByDescending(run => run.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return run is null
            ? null
            : JsonSerializer.Deserialize<AnalysisResult>(run.AnalysisResultJson, JsonOptions);
    }

    public async Task<IReadOnlyList<RunSummary>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var context = CreateContext();
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return [];
        }

        return await context.Runs
            .OrderByDescending(run => run.CreatedAtUtcText)
            .ThenByDescending(run => run.Id)
            .Select(run => new RunSummary(
                run.Id,
                DateTimeOffset.Parse(run.CreatedAtUtcText),
                run.SolutionPath,
                run.EntryPointCount,
                run.EffectCount,
                run.DiRegistrationCount,
                run.MethodObservationCount,
                run.InvocationObservationCount))
            .ToArrayAsync(cancellationToken);
    }

    private RigDbContext CreateContext()
    {
        return new RigDbContext(Path.Combine(storeDirectory, "rig.db"));
    }

    private static async Task EnsurePrototypeSchemaAsync(
        RigDbContext context,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS source_files (
                RunId TEXT NOT NULL,
                FileIndex INTEGER NOT NULL,
                ProjectName TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                Status TEXT NOT NULL,
                Confidence TEXT NOT NULL DEFAULT '',
                Basis TEXT NOT NULL DEFAULT '',
                Reason TEXT NOT NULL DEFAULT '',
                Evidence TEXT NOT NULL DEFAULT '',
                CONSTRAINT PK_source_files PRIMARY KEY (RunId, FileIndex)
            );
            """,
            cancellationToken);

        await EnsureSourceFilesColumnAsync(context, "Confidence", cancellationToken);
        await EnsureSourceFilesColumnAsync(context, "Basis", cancellationToken);
        await EnsureSourceFilesColumnAsync(context, "Evidence", cancellationToken);
        await EnsureRunsColumnAsync(context, "DiRegistrationCount", cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_source_files_RunId_FilePath ON source_files (RunId, FilePath);",
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_source_files_RunId_Status ON source_files (RunId, Status);",
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS di_registrations (
                RunId TEXT NOT NULL,
                RegistrationIndex INTEGER NOT NULL,
                ServiceType TEXT NOT NULL,
                ImplementationType TEXT NULL,
                Lifetime TEXT NOT NULL,
                RegistrationKind TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                Line INTEGER NOT NULL,
                Confidence TEXT NOT NULL,
                Basis TEXT NOT NULL,
                Reason TEXT NOT NULL,
                Evidence TEXT NOT NULL,
                CONSTRAINT PK_di_registrations PRIMARY KEY (RunId, RegistrationIndex)
            );
            """,
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_di_registrations_RunId_ServiceType ON di_registrations (RunId, ServiceType);",
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_di_registrations_RunId_ImplementationType ON di_registrations (RunId, ImplementationType);",
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS callgraph_boundary_calls (
                RunId TEXT NOT NULL,
                GraphIndex INTEGER NOT NULL,
                NodeIndex INTEGER NOT NULL,
                BoundaryCallIndex INTEGER NOT NULL,
                Kind TEXT NOT NULL,
                Target TEXT NOT NULL,
                Method TEXT NOT NULL,
                FilePath TEXT NOT NULL,
                Line INTEGER NOT NULL,
                Confidence TEXT NOT NULL,
                Basis TEXT NOT NULL,
                Reason TEXT NOT NULL,
                CONSTRAINT PK_callgraph_boundary_calls PRIMARY KEY (RunId, GraphIndex, NodeIndex, BoundaryCallIndex)
            );
            """,
            cancellationToken);

        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_callgraph_boundary_calls_RunId_Kind ON callgraph_boundary_calls (RunId, Kind);",
            cancellationToken);
    }

    private static async Task EnsureSourceFilesColumnAsync(
        RigDbContext context,
        string columnName,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(source_files);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        var sql = columnName switch
        {
            "Confidence" => "ALTER TABLE source_files ADD COLUMN Confidence TEXT NOT NULL DEFAULT '';",
            "Basis" => "ALTER TABLE source_files ADD COLUMN Basis TEXT NOT NULL DEFAULT '';",
            "Evidence" => "ALTER TABLE source_files ADD COLUMN Evidence TEXT NOT NULL DEFAULT '';",
            _ => throw new InvalidOperationException($"Unsupported source_files column: {columnName}")
        };

        await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static async Task EnsureRunsColumnAsync(
        RigDbContext context,
        string columnName,
        CancellationToken cancellationToken)
    {
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(runs);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }
        finally
        {
            await connection.CloseAsync();
        }

        var sql = columnName switch
        {
            "DiRegistrationCount" => "ALTER TABLE runs ADD COLUMN DiRegistrationCount INTEGER NOT NULL DEFAULT 0;",
            _ => throw new InvalidOperationException($"Unsupported runs column: {columnName}")
        };

        await context.Database.ExecuteSqlRawAsync(sql, cancellationToken);
    }

    private static void AddEntryPoints(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.EntryPoints.Count; index++)
        {
            var entryPoint = result.EntryPoints[index];
            context.EntryPoints.Add(new EntryPointEntity
            {
                RunId = runId,
                EntryPointIndex = index,
                Kind = entryPoint.Kind,
                Method = entryPoint.Method,
                Route = entryPoint.Route,
                DisplayName = entryPoint.DisplayName,
                FilePath = entryPoint.FilePath,
                Line = entryPoint.Line
            });
        }
    }

    private static void AddSourceFiles(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.SourceFiles.Count; index++)
        {
            var sourceFile = result.SourceFiles[index];
            context.SourceFiles.Add(new SourceFileEntity
            {
                RunId = runId,
                FileIndex = index,
                ProjectName = sourceFile.ProjectName,
                FilePath = sourceFile.FilePath,
                Status = sourceFile.Status,
                Confidence = sourceFile.Confidence,
                Basis = sourceFile.Basis,
                Reason = sourceFile.Reason,
                Evidence = sourceFile.Evidence
            });
        }
    }

    private static void AddEffects(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var effectIndex = 0; effectIndex < result.Effects.Count; effectIndex++)
        {
            var effect = result.Effects[effectIndex];
            context.Effects.Add(new EffectEntity
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
                Reason = effect.Reason
            });

            for (var observationIndex = 0; observationIndex < effect.Observations.Count; observationIndex++)
            {
                var observation = effect.Observations[observationIndex];
                context.EffectObservations.Add(new EffectObservationEntity
                {
                    RunId = runId,
                    EffectIndex = effectIndex,
                    ObservationIndex = observationIndex,
                    Type = observation.Type,
                    Context = observation.Context,
                    Detail = observation.Detail,
                    Confidence = observation.Confidence,
                    Basis = observation.Basis,
                    Reason = observation.Reason
                });
            }
        }
    }

    private static void AddDiRegistrations(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.DiRegistrations.Count; index++)
        {
            var registration = result.DiRegistrations[index];
            context.DiRegistrations.Add(new DiRegistrationEntity
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
                Evidence = registration.Evidence
            });
        }
    }

    private static void AddMethodObservations(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.MethodObservations.Count; index++)
        {
            var observation = result.MethodObservations[index];
            context.MethodObservations.Add(new MethodObservationEntity
            {
                RunId = runId,
                MethodIndex = index,
                Symbol = observation.Symbol,
                DisplayName = observation.DisplayName,
                FilePath = observation.FilePath,
                Line = observation.Line,
                ProjectName = observation.ProjectName
            });
        }
    }

    private static void AddInvocationObservations(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var index = 0; index < result.InvocationObservations.Count; index++)
        {
            var observation = result.InvocationObservations[index];
            context.InvocationObservations.Add(new InvocationObservationEntity
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
                Reason = observation.Reason
            });
        }
    }

    private static void AddCallGraphs(RigDbContext context, string runId, AnalysisResult result)
    {
        for (var graphIndex = 0; graphIndex < result.CallGraphs.Count; graphIndex++)
        {
            var graph = result.CallGraphs[graphIndex];
            context.CallGraphs.Add(new CallGraphEntity
            {
                RunId = runId,
                GraphIndex = graphIndex,
                EntryPoint = graph.EntryPoint
            });

            for (var nodeIndex = 0; nodeIndex < graph.Nodes.Count; nodeIndex++)
            {
                var node = graph.Nodes[nodeIndex];
                context.CallGraphNodes.Add(new CallGraphNodeEntity
                {
                    RunId = runId,
                    GraphIndex = graphIndex,
                    NodeIndex = nodeIndex,
                    Symbol = node.Symbol,
                    FilePath = node.FilePath,
                    Line = node.Line,
                    Confidence = node.Confidence,
                    Basis = node.Basis,
                    Reason = node.Reason
                });

                for (var callIndex = 0; callIndex < node.Calls.Count; callIndex++)
                {
                    context.CallGraphNodeCalls.Add(new CallGraphNodeCallEntity
                    {
                        RunId = runId,
                        GraphIndex = graphIndex,
                        NodeIndex = nodeIndex,
                        CallIndex = callIndex,
                        TargetSymbol = node.Calls[callIndex]
                    });
                }

                for (var boundaryCallIndex = 0; boundaryCallIndex < node.BoundaryCalls.Count; boundaryCallIndex++)
                {
                    var boundaryCall = node.BoundaryCalls[boundaryCallIndex];
                    context.CallGraphBoundaryCalls.Add(new CallGraphBoundaryCallEntity
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
                        Reason = boundaryCall.Reason
                    });
                }
            }
        }
    }
}

internal sealed record RunSummary(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string SolutionPath,
    int EntryPointCount,
    int EffectCount,
    int DiRegistrationCount,
    int MethodObservationCount,
    int InvocationObservationCount);
