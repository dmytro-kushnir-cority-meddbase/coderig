using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Rig.Analysis;
using Rig.Storage;

namespace Rig.Cli;

internal sealed class RunStore(string workingDirectory)
{
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
        };

        context.Runs.Add(run);
        AddSourceFiles(context, runId, result);
        AddEntryPoints(context, runId, result);
        AddEffects(context, runId, result);
        AddDiRegistrations(context, runId, result);
        AddMethodObservations(context, runId, result);
        AddInvocationObservations(context, runId, result);
        AddCallGraphs(context, runId, result.Effects, result);

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
            .OrderByDescending(r => r.CreatedAtUtcText)
            .ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (run is null)
        {
            return null;
        }

        var runId = run.Id;

        var sourceFiles = await context.SourceFiles
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.FileIndex)
            .Select(x => new SourceFileInfo(x.ProjectName, x.FilePath, x.Status, x.Confidence, x.Basis, x.Reason, x.Evidence))
            .ToArrayAsync(cancellationToken);

        var entryPoints = await context.EntryPoints
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.EntryPointIndex)
            .Select(x => new EntryPointInfo(x.Kind, x.Method, x.Route, x.DisplayName, x.FilePath, x.Line))
            .ToArrayAsync(cancellationToken);

        var effectEntities = await context.Effects
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.EffectIndex)
            .ToArrayAsync(cancellationToken);

        var observationEntities = await context.EffectObservations
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.EffectIndex).ThenBy(x => x.ObservationIndex)
            .ToArrayAsync(cancellationToken);

        var observationsByEffect = observationEntities
            .GroupBy(x => x.EffectIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EffectObservationInfo>)g
                .Select(o => new EffectObservationInfo(o.Type, o.Context, o.Detail, o.Confidence, o.Basis, o.Reason))
                .ToArray());

        var effects = effectEntities
            .Select(e => new EffectInfo(
                e.Provider, e.Operation, e.Resource, e.Method,
                e.FilePath, e.Line, e.Confidence, e.Basis, e.Reason,
                observationsByEffect.GetValueOrDefault(e.EffectIndex, [])))
            .ToArray();

        var effectsByIndex = effectEntities
            .Zip(effects)
            .ToDictionary(pair => pair.First.EffectIndex, pair => pair.Second);

        var diRegistrations = await context.DiRegistrations
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.RegistrationIndex)
            .Select(x => new DiRegistrationInfo(x.ServiceType, x.ImplementationType, x.Lifetime, x.RegistrationKind, x.FilePath, x.Line, x.Confidence, x.Basis, x.Reason, x.Evidence))
            .ToArrayAsync(cancellationToken);

        var graphEntities = await context.CallGraphs
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.GraphIndex)
            .ToArrayAsync(cancellationToken);

        var nodeEntities = await context.CallGraphNodes
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.GraphIndex).ThenBy(x => x.NodeIndex)
            .ToArrayAsync(cancellationToken);

        var callEntities = await context.CallGraphNodeCalls
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.GraphIndex).ThenBy(x => x.NodeIndex).ThenBy(x => x.CallIndex)
            .ToArrayAsync(cancellationToken);

        var boundaryCallEntities = await context.CallGraphBoundaryCalls
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.GraphIndex).ThenBy(x => x.NodeIndex).ThenBy(x => x.BoundaryCallIndex)
            .ToArrayAsync(cancellationToken);

        var nodeEffectLinks = await context.CallGraphNodeEffects
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.GraphIndex).ThenBy(x => x.NodeIndex).ThenBy(x => x.LinkIndex)
            .ToArrayAsync(cancellationToken);

        var callsByNode = callEntities
            .GroupBy(x => (x.GraphIndex, x.NodeIndex))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.TargetSymbol).ToArray());

        var boundaryCallsByNode = boundaryCallEntities
            .GroupBy(x => (x.GraphIndex, x.NodeIndex))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BoundaryCallInfo>)g
                .Select(b => new BoundaryCallInfo(b.Kind, b.Target, b.Method, b.FilePath, b.Line, b.Confidence, b.Basis, b.Reason))
                .ToArray());

        var nodeEffectsByNode = nodeEffectLinks
            .GroupBy(x => (x.GraphIndex, x.NodeIndex))
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EffectInfo>)g
                .Select(link => effectsByIndex.GetValueOrDefault(link.EffectIndex))
                .OfType<EffectInfo>()
                .ToArray());

        var nodesByGraph = nodeEntities
            .GroupBy(x => x.GraphIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CallGraphNodeInfo>)g
                .Select(n => new CallGraphNodeInfo(
                    n.Symbol, n.FilePath, n.Line, n.Confidence, n.Basis, n.Reason,
                    callsByNode.GetValueOrDefault((n.GraphIndex, n.NodeIndex), []),
                    boundaryCallsByNode.GetValueOrDefault((n.GraphIndex, n.NodeIndex), []),
                    nodeEffectsByNode.GetValueOrDefault((n.GraphIndex, n.NodeIndex), [])))
                .ToArray());

        var callGraphs = graphEntities
            .Select(g => new CallGraphInfo(g.EntryPoint, nodesByGraph.GetValueOrDefault(g.GraphIndex, [])))
            .ToArray();

        return new AnalysisResult(sourceFiles, entryPoints, effects, diRegistrations, callGraphs, [], []);
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

    private static void AddCallGraphs(RigDbContext context, string runId, IReadOnlyList<EffectInfo> effects, AnalysisResult result)
    {
        var effectIndexByIdentity = effects
            .Select((e, i) => (e, i))
            .ToDictionary(pair => pair.e, pair => pair.i);

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

                for (var linkIndex = 0; linkIndex < node.Effects.Count; linkIndex++)
                {
                    if (!effectIndexByIdentity.TryGetValue(node.Effects[linkIndex], out var effectIndex))
                    {
                        continue;
                    }

                    context.CallGraphNodeEffects.Add(new CallGraphNodeEffectEntity
                    {
                        RunId = runId,
                        GraphIndex = graphIndex,
                        NodeIndex = nodeIndex,
                        LinkIndex = linkIndex,
                        EffectIndex = effectIndex
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
