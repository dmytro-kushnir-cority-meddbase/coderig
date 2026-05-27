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

    public static async Task<RunSummary?> GetLatestRunSummaryAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return null;
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
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<string>?> FindCallGraphSymbolsAsync(
        RigDbContext context,
        string contains,
        CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        return await context.CallGraphNodes
            .Where(node => node.RunId == runId && node.Symbol.Contains(contains))
            .Select(node => node.Symbol)
            .Distinct()
            .OrderBy(symbol => symbol)
            .ToArrayAsync(cancellationToken);
    }

    public static async Task<TraceInfo?> LoadTraceAsync(
        RigDbContext context,
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var run = await GetLatestRunSummaryAsync(context, cancellationToken);
        if (run is null) return null;

        var graphIndexes = await context.CallGraphNodes
            .Where(node => node.RunId == run.Id && node.Symbol == symbol)
            .Select(node => node.GraphIndex)
            .Distinct()
            .OrderBy(graphIndex => graphIndex)
            .ToArrayAsync(cancellationToken);

        var graphs = new List<TraceCallGraphInfo>(graphIndexes.Length);
        foreach (var graphIndex in graphIndexes)
        {
            var graph = await LoadCallGraphAsync(context, run.Id, graphIndex, cancellationToken);
            if (graph is not null)
            {
                graphs.Add(new TraceCallGraphInfo(graphIndex, graph));
            }
        }

        return new TraceInfo(symbol, run, graphs);
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

    public static async Task<IReadOnlyList<EffectInfo>?> LoadEffectsForEntryPointAsync(RigDbContext context, int entryPointIndex, CancellationToken cancellationToken = default)
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null) return null;

        var effectIndices = await context.CallGraphNodeEffects
            .Where(x => x.RunId == runId && x.GraphIndex == entryPointIndex)
            .Select(x => x.EffectIndex)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        var effectEntities = await context.Effects
            .Where(x => x.RunId == runId && effectIndices.Contains(x.EffectIndex))
            .OrderBy(x => x.EffectIndex)
            .ToArrayAsync(cancellationToken);

        var observationEntities = await context.EffectObservations
            .Where(x => x.RunId == runId && effectIndices.Contains(x.EffectIndex))
            .OrderBy(x => x.EffectIndex).ThenBy(x => x.ObservationIndex)
            .ToArrayAsync(cancellationToken);

        return BuildEffects(effectEntities, observationEntities);
    }

    // runId must come from GetLatestRunIdAsync; returns null when the entry point is not found.
    public static async Task<CallGraphInfo?> LoadCallGraphAsync(RigDbContext context, string runId, int entryPointIndex, CancellationToken cancellationToken = default)
    {
        var entryPointName = await context.CallGraphs
            .Where(x => x.RunId == runId && x.GraphIndex == entryPointIndex)
            .Select(x => x.EntryPoint)
            .FirstOrDefaultAsync(cancellationToken);

        if (entryPointName is null) return null;

        var graphIndex = entryPointIndex;

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

        var nodes = BuildCallGraphNodes(
            nodeEntities,
            callEntities,
            boundaryCallEntities,
            nodeEffectLinks,
            effectEntities,
            observationEntities);

        return new CallGraphInfo(entryPointName, nodes, CallGraphCycleDetector.Detect(nodes));
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

    private static IReadOnlyList<CallGraphNodeInfo> BuildCallGraphNodes(
        IReadOnlyList<CallGraphNodeEntity> nodeEntities,
        IReadOnlyList<CallGraphNodeCallEntity> callEntities,
        IReadOnlyList<CallGraphBoundaryCallEntity> boundaryCallEntities,
        IReadOnlyList<CallGraphNodeEffectEntity> nodeEffectLinks,
        EffectEntity[] effectEntities,
        EffectObservationEntity[] observationEntities)
    {
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

        return nodeEntities
            .Select(n => new CallGraphNodeInfo(
                n.Symbol, n.FilePath, n.Line, n.Confidence, n.Basis, n.Reason,
                callsByNode.GetValueOrDefault(n.NodeIndex, []),
                boundaryCallsByNode.GetValueOrDefault(n.NodeIndex, []),
                nodeEffectsByNode.GetValueOrDefault(n.NodeIndex, [])))
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
