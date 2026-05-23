using Microsoft.EntityFrameworkCore;
using Rig.Analysis;

namespace Rig.Storage.Queries;


public static class Reads
{
    // Returns null when the DB doesn't exist or has no runs yet.
    public static async Task<string?> GetLatestRunIdAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return null;
        }

        return await context.Runs
            .OrderByDescending(r => r.CreatedAtUtcText)
            .ThenByDescending(r => r.Id)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<EntryPointInfo>?> LoadEntryPointsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        return await context.EntryPoints
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.EntryPointIndex)
            .Select(x => new EntryPointInfo(x.Kind, x.Method, x.Route, x.DisplayName, x.FilePath, x.Line))
            .ToArrayAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<EffectInfo>?> LoadEffectsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        var effectEntities = await context.Effects
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.EffectIndex)
            .ToArrayAsync(cancellationToken);

        var observationEntities = await context.EffectObservations
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.EffectIndex).ThenBy(x => x.ObservationIndex)
            .ToArrayAsync(cancellationToken);

        return BuildEffects(effectEntities, observationEntities);
    }

    public static async Task<IReadOnlyList<DiRegistrationInfo>?> LoadDiRegistrationsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        return await context.DiRegistrations
            .Where(x => x.RunId == runId)
            .OrderBy(x => x.RegistrationIndex)
            .Select(x => new DiRegistrationInfo(x.ServiceType, x.ImplementationType, x.Lifetime, x.RegistrationKind, x.FilePath, x.Line, x.Confidence, x.Basis, x.Reason, x.Evidence))
            .ToArrayAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<SourceFileInfo>?> LoadSkippedSourceFilesAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        return await context.SourceFiles
            .Where(x => x.RunId == runId && x.Status == "skipped")
            .OrderBy(x => x.FilePath)
            .Select(x => new SourceFileInfo(x.ProjectName, x.FilePath, x.Status, x.Confidence, x.Basis, x.Reason, x.Evidence))
            .ToArrayAsync(cancellationToken);
    }

    // runId must come from GetLatestRunIdAsync; returns null when the entry point is not found.
    public static async Task<CallGraphInfo?> LoadCallGraphAsync(RigDbContext context, string runId, string entryPoint, CancellationToken cancellationToken = default)
    {
        var graphEntity = await context.CallGraphs
            .Where(x => x.RunId == runId && x.EntryPoint == entryPoint)
            .FirstOrDefaultAsync(cancellationToken);

        if (graphEntity is null) return null;

        var graphIndex = graphEntity.GraphIndex;

        var nodeEntities = await context.CallGraphNodes
            .Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex)
            .ToArrayAsync(cancellationToken);

        var callEntities = await context.CallGraphNodeCalls
            .Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex).ThenBy(x => x.CallIndex)
            .ToArrayAsync(cancellationToken);

        var boundaryCallEntities = await context.CallGraphBoundaryCalls
            .Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex).ThenBy(x => x.BoundaryCallIndex)
            .ToArrayAsync(cancellationToken);

        var nodeEffectLinks = await context.CallGraphNodeEffects
            .Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex).ThenBy(x => x.LinkIndex)
            .ToArrayAsync(cancellationToken);

        var effectIndices = nodeEffectLinks.Select(l => l.EffectIndex).Distinct().ToArray();

        var effectEntities = await context.Effects
            .Where(x => x.RunId == runId && effectIndices.Contains(x.EffectIndex))
            .ToArrayAsync(cancellationToken);

        var observationEntities = await context.EffectObservations
            .Where(x => x.RunId == runId && effectIndices.Contains(x.EffectIndex))
            .OrderBy(x => x.EffectIndex).ThenBy(x => x.ObservationIndex)
            .ToArrayAsync(cancellationToken);

        var effectsByIndex = BuildEffects(effectEntities, observationEntities)
            .Zip(effectEntities)
            .ToDictionary(pair => pair.Second.EffectIndex, pair => pair.First);

        var callsByNode = callEntities
            .GroupBy(x => x.NodeIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.TargetSymbol).ToArray());

        var boundaryCallsByNode = boundaryCallEntities
            .GroupBy(x => x.NodeIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<BoundaryCallInfo>)g
                .Select(b => new BoundaryCallInfo(b.Kind, b.Target, b.Method, b.FilePath, b.Line, b.Confidence, b.Basis, b.Reason))
                .ToArray());

        var nodeEffectsByNode = nodeEffectLinks
            .GroupBy(x => x.NodeIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EffectInfo>)g
                .Select(link => effectsByIndex.GetValueOrDefault(link.EffectIndex))
                .OfType<EffectInfo>()
                .ToArray());

        var nodes = nodeEntities
            .Select(n => new CallGraphNodeInfo(
                n.Symbol, n.FilePath, n.Line, n.Confidence, n.Basis, n.Reason,
                callsByNode.GetValueOrDefault(n.NodeIndex, []),
                boundaryCallsByNode.GetValueOrDefault(n.NodeIndex, []),
                nodeEffectsByNode.GetValueOrDefault(n.NodeIndex, [])))
            .ToArray();

        return new CallGraphInfo(graphEntity.EntryPoint, nodes);
    }

    public static async Task<AnalysisResult?> LoadLatestAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        var run = await context.Runs
            .Where(r => r.Id == runId)
            .FirstAsync(cancellationToken);

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

        var effects = BuildEffects(effectEntities, observationEntities);

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

        return new AnalysisResult(run.SolutionPath, sourceFiles, entryPoints, effects.ToArray(), diRegistrations, callGraphs, [], []);
    }

    private static IReadOnlyList<EffectInfo> BuildEffects(EffectEntity[] effectEntities, EffectObservationEntity[] observationEntities)
    {
        var observationsByEffect = observationEntities
            .GroupBy(x => x.EffectIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EffectObservationInfo>)g
                .Select(o => new EffectObservationInfo(o.Type, o.Context, o.Detail, o.Confidence, o.Basis, o.Reason))
                .ToArray());

        return effectEntities
            .Select(e => new EffectInfo(
                e.Provider, e.Operation, e.Resource, e.Method,
                e.FilePath, e.Line, e.Confidence, e.Basis, e.Reason,
                observationsByEffect.GetValueOrDefault(e.EffectIndex, [])))
            .ToArray();
    }

    public static async Task<IReadOnlyList<RunSummary>> ListRunsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
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
}