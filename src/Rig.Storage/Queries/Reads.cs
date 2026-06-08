using Microsoft.EntityFrameworkCore;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Storage;

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

        return await context
            .Runs.OrderByDescending(r => r.CreatedAtUtcText)
            .ThenByDescending(r => r.Id)
            .Select(r => r.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<EntryPointInfo>?> LoadEntryPointsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null)
            return null;

        return await context
            .EntryPoints.Where(x => x.RunId == runId)
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

        return await context
            .Runs.OrderByDescending(run => run.CreatedAtUtcText)
            .ThenByDescending(run => run.Id)
            .Select(run => new RunSummary(
                run.Id,
                DateTimeOffset.Parse(run.CreatedAtUtcText),
                run.SolutionPath,
                run.EntryPointCount,
                run.EffectCount,
                run.DiRegistrationCount,
                run.MethodObservationCount,
                run.InvocationObservationCount,
                run.ProjectIdentity,
                run.SourceProjectPath
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<string>?> FindCallGraphSymbolsAsync(
        RigDbContext context,
        string contains,
        CancellationToken cancellationToken = default
    )
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null)
            return null;

        return await context
            .CallGraphNodes.Where(node => node.RunId == runId && node.Symbol.Contains(contains))
            .Select(node => node.Symbol)
            .Distinct()
            .OrderBy(symbol => symbol)
            .ToArrayAsync(cancellationToken);
    }

    public static async Task<TraceInfo?> LoadTraceAsync(RigDbContext context, string symbol, CancellationToken cancellationToken = default)
    {
        var run = await GetLatestRunSummaryAsync(context, cancellationToken);
        if (run is null)
            return null;

        var graphIndexes = await context
            .CallGraphNodes.Where(node => node.RunId == run.Id && node.Symbol == symbol)
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

    public static async Task<IReadOnlyList<EffectInfo>?> LoadEffectsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null)
            return null;

        var effectEntities = await context.Effects.Where(x => x.RunId == runId).OrderBy(x => x.EffectIndex).ToArrayAsync(cancellationToken);

        var observationEntities = await context
            .EffectObservations.Where(x => x.RunId == runId)
            .OrderBy(x => x.EffectIndex)
            .ThenBy(x => x.ObservationIndex)
            .ToArrayAsync(cancellationToken);

        return BuildEffects(effectEntities, observationEntities);
    }

    public static async Task<IReadOnlyList<DiRegistrationInfo>?> LoadDiRegistrationsAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null)
            return null;

        return await context
            .DiRegistrations.Where(x => x.RunId == runId)
            .OrderBy(x => x.RegistrationIndex)
            .Select(x => new DiRegistrationInfo(
                x.ServiceType,
                x.ImplementationType,
                x.Lifetime,
                x.RegistrationKind,
                x.FilePath,
                x.Line,
                x.Confidence,
                x.Basis,
                x.Reason,
                x.Evidence
            ))
            .ToArrayAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<SourceFileInfo>?> LoadSkippedSourceFilesAsync(
        RigDbContext context,
        CancellationToken cancellationToken = default
    )
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null)
            return null;

        return await context
            .SourceFiles.Where(x => x.RunId == runId && x.Status == "skipped")
            .OrderBy(x => x.FilePath)
            .Select(x => new SourceFileInfo(x.ProjectName, x.FilePath, x.Status, x.Confidence, x.Basis, x.Reason, x.Evidence))
            .ToArrayAsync(cancellationToken);
    }

    public static async Task<IReadOnlyList<EffectInfo>?> LoadEffectsForEntryPointAsync(
        RigDbContext context,
        int entryPointIndex,
        CancellationToken cancellationToken = default
    )
    {
        var runId = await GetLatestRunIdAsync(context, cancellationToken);
        if (runId is null)
            return null;

        var effectIndices = await context
            .CallGraphNodeEffects.Where(x => x.RunId == runId && x.GraphIndex == entryPointIndex)
            .Select(x => x.EffectIndex)
            .Distinct()
            .ToArrayAsync(cancellationToken);

        var effectEntities = await context
            .Effects.Where(x => x.RunId == runId && effectIndices.Contains(x.EffectIndex))
            .OrderBy(x => x.EffectIndex)
            .ToArrayAsync(cancellationToken);

        var observationEntities = await context
            .EffectObservations.Where(x => x.RunId == runId && effectIndices.Contains(x.EffectIndex))
            .OrderBy(x => x.EffectIndex)
            .ThenBy(x => x.ObservationIndex)
            .ToArrayAsync(cancellationToken);

        return BuildEffects(effectEntities, observationEntities);
    }

    // runId must come from GetLatestRunIdAsync; returns null when the entry point is not found.
    public static async Task<CallGraphInfo?> LoadCallGraphAsync(
        RigDbContext context,
        string runId,
        int entryPointIndex,
        CancellationToken cancellationToken = default
    )
    {
        var entryPointName = await context
            .CallGraphs.Where(x => x.RunId == runId && x.GraphIndex == entryPointIndex)
            .Select(x => x.EntryPoint)
            .FirstOrDefaultAsync(cancellationToken);

        if (entryPointName is null)
            return null;

        var graphIndex = entryPointIndex;

        // Load this run's nodes + calls
        var nodeEntities = (await context
            .CallGraphNodes.Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex)
            .ToArrayAsync(cancellationToken)).ToList();

        var callEntities = (await context
            .CallGraphNodeCalls.Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex)
            .ThenBy(x => x.CallIndex)
            .ToArrayAsync(cancellationToken)).ToList();

        var boundaryCallEntities = (await context
            .CallGraphBoundaryCalls.Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex)
            .ThenBy(x => x.BoundaryCallIndex)
            .ToArrayAsync(cancellationToken)).ToList();

        var nodeEffectLinks = (await context
            .CallGraphNodeEffects.Where(x => x.RunId == runId && x.GraphIndex == graphIndex)
            .OrderBy(x => x.NodeIndex)
            .ThenBy(x => x.LinkIndex)
            .ToArrayAsync(cancellationToken)).ToList();

        // Cross-run stitching: if this run has a ProjectIdentity, look up called symbols in
        // peer runs (same identity) and expand them into the callgraph until all reachable
        // source is covered.
        var projectIdentity = await context.Runs
            .Where(r => r.Id == runId)
            .Select(r => r.ProjectIdentity)
            .FirstOrDefaultAsync(cancellationToken);

        if (projectIdentity is not null)
        {
            var resolvedSymbols = new HashSet<string>(
                nodeEntities.Select(n => n.Symbol), StringComparer.Ordinal);

            // Synthesised node index continues above the real nodes so IDs don't collide.
            var syntheticNodeBase = nodeEntities.Count + 10_000;
            var syntheticCallBase = callEntities.Count + 10_000;
            var symbolToSyntheticNode = new Dictionary<string, int>(StringComparer.Ordinal);

            // BFS expansion queue seeded from:
            //   1. callgraph_node_calls with targets not yet in this run's nodes
            //   2. callgraph_boundary_calls (external calls that weren't traced in this run)
            //      — these are the BOUNDARY external lines; try to find them in peer runs
            var queue = new Queue<string>();
            foreach (var call in callEntities)
            {
                if (!resolvedSymbols.Contains(call.TargetSymbol))
                    queue.Enqueue(call.TargetSymbol);
            }
            // Boundary calls: the Target field IS the full Roslyn symbol
            // (e.g. "global::MedDBase.ServiceLayer.X.Method(params)").
            // Enqueue each so the BFS can look them up in symbol_index.
            foreach (var bc in boundaryCallEntities.Where(b => b.Kind == "external" && !string.IsNullOrEmpty(b.Target)))
            {
                if (!resolvedSymbols.Contains(bc.Target))
                    queue.Enqueue(bc.Target);
            }

            var visited = new HashSet<string>(resolvedSymbols, StringComparer.Ordinal);
            const int MaxExpansionDepth = 2000;
            var expansions = 0;

            while (queue.Count > 0 && expansions < MaxExpansionDepth)
            {
                var symbol = queue.Dequeue();
                if (!visited.Add(symbol))
                    continue;

                expansions++;

                // Look up in peer runs — try exact match first, then substring for boundary calls
                // where the symbol may be a short form like "IGenericServiceProvider.ProvideService"
                // while symbol_index has "global::MedDBase...IGenericServiceProvider.ProvideService(...)"
                var entry = await context.SymbolIndex
                    .Where(s => s.ProjectIdentity == projectIdentity &&
                                (s.Symbol == symbol || s.Symbol.Contains(symbol)))
                    .FirstOrDefaultAsync(cancellationToken);

                if (entry is null)
                    continue;

                // Locate the peer method.
                // Primary: look for an existing callgraph node (entry-point projects).
                // Fallback: library projects have 0 entry points → no callgraph_nodes rows.
                //   Use method_observations (FilePath/Line) + invocation_observations (outgoing calls).
                var peerNode = await context.CallGraphNodes
                    .Where(n => n.RunId == entry.RunId && n.Symbol == symbol)
                    .FirstOrDefaultAsync(cancellationToken);

                var synNodeIdx = syntheticNodeBase + symbolToSyntheticNode.Count;
                symbolToSyntheticNode[symbol] = synNodeIdx;

                nodeEntities.Add(new CallGraphNodeEntity
                {
                    RunId = entry.RunId,
                    GraphIndex = synNodeIdx,
                    NodeIndex = synNodeIdx,
                    Symbol = symbol,
                    FilePath = entry.FilePath,
                    Line = entry.Line,
                    Confidence = peerNode?.Confidence ?? "medium",
                    Basis = peerNode?.Basis ?? "cross_run",
                    Reason = peerNode?.Reason ?? "cross_run_stitching",
                });

                // Pull outgoing calls:
                //   If a callgraph node exists, use its pre-built call list.
                //   Otherwise fall back to invocation_observations for the same run.
                var callIdx = syntheticCallBase;
                if (peerNode is not null)
                {
                    var peerCalls = await context.CallGraphNodeCalls
                        .Where(c => c.RunId == entry.RunId && c.GraphIndex == peerNode.GraphIndex && c.NodeIndex == peerNode.NodeIndex)
                        .ToArrayAsync(cancellationToken);

                    foreach (var pc in peerCalls)
                    {
                        callEntities.Add(new CallGraphNodeCallEntity
                        {
                            RunId = entry.RunId, GraphIndex = synNodeIdx, NodeIndex = synNodeIdx,
                            CallIndex = callIdx++, TargetSymbol = pc.TargetSymbol,
                        });
                        if (!visited.Contains(pc.TargetSymbol)) queue.Enqueue(pc.TargetSymbol);
                    }
                }
                else
                {
                    // Library method: derive outgoing calls from invocation_observations
                    var peerInvocations = await context.InvocationObservations
                        .Where(i => i.RunId == entry.RunId && i.ContainingMethodSymbol == symbol)
                        .ToArrayAsync(cancellationToken);

                    foreach (var iv in peerInvocations)
                    {
                        callEntities.Add(new CallGraphNodeCallEntity
                        {
                            RunId = entry.RunId, GraphIndex = synNodeIdx, NodeIndex = synNodeIdx,
                            CallIndex = callIdx++, TargetSymbol = iv.TargetSymbol,
                        });
                        if (!visited.Contains(iv.TargetSymbol)) queue.Enqueue(iv.TargetSymbol);
                    }
                }

                // Boundary calls — only available if we had a real callgraph node
                var peerBoundaryCalls = peerNode is not null
                    ? await context.CallGraphBoundaryCalls
                        .Where(b => b.RunId == entry.RunId && b.GraphIndex == peerNode.GraphIndex && b.NodeIndex == peerNode.NodeIndex)
                        .ToArrayAsync(cancellationToken)
                    : [];

                var bIdx = 0;
                foreach (var pb in peerBoundaryCalls)
                    boundaryCallEntities.Add(new CallGraphBoundaryCallEntity
                    {
                        RunId = pb.RunId, GraphIndex = synNodeIdx, NodeIndex = synNodeIdx,
                        BoundaryCallIndex = bIdx++,
                        Kind = pb.Kind, Target = pb.Target, Method = pb.Method,
                        FilePath = pb.FilePath, Line = pb.Line,
                        Confidence = pb.Confidence, Basis = pb.Basis, Reason = pb.Reason,
                    });

                // Load effect links — from stored callgraph_node_effects when available,
                // or by matching file+line from the effects table for library methods
                // (projects indexed without entry points have effects but no callgraph nodes).
                IReadOnlyList<CallGraphNodeEffectEntity> peerEffectLinks;
                if (peerNode is not null)
                {
                    peerEffectLinks = await context.CallGraphNodeEffects
                        .Where(e => e.RunId == entry.RunId && e.GraphIndex == peerNode.GraphIndex && e.NodeIndex == peerNode.NodeIndex)
                        .ToArrayAsync(cancellationToken);
                }
                else if (!string.IsNullOrEmpty(entry.FilePath))
                {
                    // Find effects in the peer run that fall within the same source method.
                    // Use file path as a proxy — all effects in this file around the method's
                    // start line (within a generous window) are attributed to this node.
                    var peerEffectsInFile = await context.Effects
                        .Where(e => e.RunId == entry.RunId && e.FilePath == entry.FilePath
                                 && e.Line >= entry.Line)
                        .Take(50)
                        .Select(e => e.EffectIndex)
                        .ToArrayAsync(cancellationToken);

                    peerEffectLinks = peerEffectsInFile
                        .Select((idx, i) => new CallGraphNodeEffectEntity
                        {
                            RunId = entry.RunId, GraphIndex = synNodeIdx, NodeIndex = synNodeIdx,
                            LinkIndex = i, EffectIndex = idx,
                        })
                        .ToArray();
                }
                else
                {
                    peerEffectLinks = [];
                }

                var lIdx = 0;
                foreach (var pe in peerEffectLinks)
                    nodeEffectLinks.Add(new CallGraphNodeEffectEntity
                    {
                        RunId = pe.RunId, GraphIndex = synNodeIdx, NodeIndex = synNodeIdx,
                        LinkIndex = lIdx++, EffectIndex = pe.EffectIndex,
                    });
            }

            // Rewrite call targets that were expanded to their synthetic node indices
            // so BuildCallGraphNodes sees them as proper cross-linked calls (not boundaries).
            foreach (var c in callEntities.Where(c => symbolToSyntheticNode.ContainsKey(c.TargetSymbol)))
            {
                // Mark the call as an intra-graph call (already present in nodeEntities).
                // The TargetSymbol string is retained — the rendering still resolves by symbol.
            }
        }

        // Gather all effect data across the (potentially multi-run) node+effect-link sets
        var allEffectLinks = nodeEffectLinks
            .GroupBy(l => l.RunId)
            .ToArray();

        var effectEntitiesAll = new List<EffectEntity>();
        var observationEntitiesAll = new List<EffectObservationEntity>();

        foreach (var group in allEffectLinks)
        {
            var indices = group.Select(l => l.EffectIndex).Distinct().ToArray();
            effectEntitiesAll.AddRange(await context.Effects
                .Where(x => x.RunId == group.Key && indices.Contains(x.EffectIndex))
                .ToArrayAsync(cancellationToken));
            observationEntitiesAll.AddRange(await context.EffectObservations
                .Where(x => x.RunId == group.Key && indices.Contains(x.EffectIndex))
                .OrderBy(x => x.EffectIndex).ThenBy(x => x.ObservationIndex)
                .ToArrayAsync(cancellationToken));
        }

        var nodes = BuildCallGraphNodes(
            nodeEntities,
            callEntities,
            boundaryCallEntities,
            nodeEffectLinks,
            [.. effectEntitiesAll],
            [.. observationEntitiesAll]
        );

        return new CallGraphInfo(entryPointName, nodes, CallGraphCycleDetector.Detect(nodes));
    }

    private static IReadOnlyList<EffectInfo> BuildEffects(EffectEntity[] effectEntities, EffectObservationEntity[] observationEntities)
    {
        var observationsByEffect = observationEntities
            .GroupBy(x => x.EffectIndex)
            .ToDictionary(
                g => g.Key,
                g =>
                    (IReadOnlyList<EffectObservationInfo>)
                        g.Select(o => new EffectObservationInfo(o.Type, o.Context, o.Detail, o.Confidence, o.Basis, o.Reason)).ToArray()
            );

        return effectEntities
            .Select(e => new EffectInfo(
                e.Provider,
                e.Operation,
                e.Resource,
                e.Method,
                e.FilePath,
                e.Line,
                e.Confidence,
                e.Basis,
                e.Reason,
                observationsByEffect.GetValueOrDefault(e.EffectIndex, [])
            ))
            .ToArray();
    }

    private static IReadOnlyList<CallGraphNodeInfo> BuildCallGraphNodes(
        IReadOnlyList<CallGraphNodeEntity> nodeEntities,
        IReadOnlyList<CallGraphNodeCallEntity> callEntities,
        IReadOnlyList<CallGraphBoundaryCallEntity> boundaryCallEntities,
        IReadOnlyList<CallGraphNodeEffectEntity> nodeEffectLinks,
        EffectEntity[] effectEntities,
        EffectObservationEntity[] observationEntities
    )
    {
        var effectsByIndex = BuildEffects(effectEntities, observationEntities)
            .Zip(effectEntities)
            .ToDictionary(pair => pair.Second.EffectIndex, pair => pair.First);

        var callsByNode = callEntities
            .GroupBy(x => x.NodeIndex)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(c => c.TargetSymbol).ToArray());

        var boundaryCallsByNode = boundaryCallEntities
            .GroupBy(x => x.NodeIndex)
            .ToDictionary(
                g => g.Key,
                g =>
                    (IReadOnlyList<BoundaryCallInfo>)
                        g.Select(b => new BoundaryCallInfo(b.Kind, b.Target, b.Method, b.FilePath, b.Line, b.Confidence, b.Basis, b.Reason))
                            .ToArray()
            );

        var nodeEffectsByNode = nodeEffectLinks
            .GroupBy(x => x.NodeIndex)
            .ToDictionary(
                g => g.Key,
                g =>
                    (IReadOnlyList<EffectInfo>)
                        g.Select(link => effectsByIndex.GetValueOrDefault(link.EffectIndex)).OfType<EffectInfo>().ToArray()
            );

        return nodeEntities
            .Select(n => new CallGraphNodeInfo(
                n.Symbol,
                n.FilePath,
                n.Line,
                n.Confidence,
                n.Basis,
                n.Reason,
                callsByNode.GetValueOrDefault(n.NodeIndex, []),
                boundaryCallsByNode.GetValueOrDefault(n.NodeIndex, []),
                nodeEffectsByNode.GetValueOrDefault(n.NodeIndex, [])
            ))
            .ToArray();
    }

    public static async Task<IReadOnlyList<RunSummary>> ListRunsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        if (!await context.Database.CanConnectAsync(cancellationToken))
        {
            return [];
        }

        return await context
            .Runs.OrderByDescending(run => run.CreatedAtUtcText)
            .ThenByDescending(run => run.Id)
            .Select(run => new RunSummary(
                run.Id,
                DateTimeOffset.Parse(run.CreatedAtUtcText),
                run.SolutionPath,
                run.EntryPointCount,
                run.EffectCount,
                run.DiRegistrationCount,
                run.MethodObservationCount,
                run.InvocationObservationCount,
                run.ProjectIdentity,
                run.SourceProjectPath
            ))
            .ToArrayAsync(cancellationToken);
    }

    // --- Stage-3 fact queries: cross-project (all runs), DocID-keyed. No latest-run concept. ---

    public static async Task<IReadOnlyList<SymbolSearchHit>> SearchSymbolsAsync(
        RigDbContext context, string pattern, string? kind, int limit, CancellationToken cancellationToken = default)
    {
        var like = $"%{pattern}%";
        var query = context.SymbolFacts
            .Where(s => EF.Functions.Like(s.Name, like) || EF.Functions.Like(s.SymbolId, like));
        if (kind is not null)
            query = query.Where(s => s.Kind == kind);

        // Dedupe by SymbolId across runs (multi-target siblings / re-indexed projects).
        var rows = await query.OrderBy(s => s.SymbolId).Take(5000).ToArrayAsync(cancellationToken);
        return rows
            .GroupBy(s => s.SymbolId)
            .Take(limit)
            .Select(g => g.First())
            .Select(s => new SymbolSearchHit(s.SymbolId, s.Kind, s.Signature, s.FilePath, s.Line, s.DefiningAssembly))
            .ToArray();
    }

    public static async Task<IReadOnlyList<ReferenceHit>> FindReferencesAsync(
        RigDbContext context, string pattern, bool firstPartyOnly, string? refKind, int limit, CancellationToken cancellationToken = default)
    {
        var like = $"%{pattern}%";
        var query = context.ReferenceFacts.Where(r => EF.Functions.Like(r.TargetSymbolId, like));
        if (firstPartyOnly)
            query = query.Where(r => r.TargetInSource);
        if (refKind is not null)
            query = query.Where(r => r.RefKind == refKind);

        var rows = await query
            .OrderBy(r => r.TargetSymbolId).ThenBy(r => r.FilePath).ThenBy(r => r.Line)
            .Take(limit)
            .ToArrayAsync(cancellationToken);
        return rows
            .Select(r => new ReferenceHit(r.TargetSymbolId, r.RefKind, r.EnclosingSymbolId, r.FilePath, r.Line, r.TargetInSource))
            .ToArray();
    }

    // Loads the fact-derived call graph for cross-project path finding (stage 2 over facts).
    // No Roslyn, no entry-point anchoring — every method's call edges, across all runs.
    public static async Task<FactGraphData> LoadFactGraphAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var callRows = await context.ReferenceFacts
            .Where(r => r.EnclosingSymbolId != null
                && (r.RefKind == "invocation" || r.RefKind == "methodGroup" || r.RefKind == "ctor"))
            .Select(r => new { r.EnclosingSymbolId, r.TargetSymbolId, r.RefKind, r.FilePath, r.Line })
            .ToArrayAsync(cancellationToken);
        var callEdges = callRows
            .Select(r => new CallEdge(r.EnclosingSymbolId!, r.TargetSymbolId, r.RefKind, r.FilePath, r.Line))
            .Distinct()
            .ToArray();

        var implRows = await context.TypeRelationFacts
            .Where(t => t.RelationKind == "interface")
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var implEdges = implRows
            .Select(t => new ImplementsEdge(t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var baseRows = await context.TypeRelationFacts
            .Where(t => t.RelationKind == "base")
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var baseEdges = baseRows
            .Select(t => new BaseEdge(t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var methodRows = await context.SymbolFacts
            .Where(s => s.Kind == "method")
            .Select(s => new { s.SymbolId, s.Name, s.ContainingSymbolId, s.IsOverride })
            .ToArrayAsync(cancellationToken);
        var methods = methodRows
            .GroupBy(m => m.SymbolId)
            .Select(g => g.First())
            .Select(m => new MethodRef(m.SymbolId, m.Name, m.ContainingSymbolId, m.IsOverride))
            .ToArray();

        return new FactGraphData(callEdges, implEdges, methods, baseEdges);
    }

    // Derives handoff (delegate / method-group) entry points from facts — a category the
    // structural entry-point rules miss. First-party targets only (TargetInSource). No re-index.
    public static async Task<IReadOnlyList<HandoffEntryPoint>> DeriveHandoffEntryPointsAsync(
        RigDbContext context, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await context.ReferenceFacts
            .Where(r => r.RefKind == "methodGroup" && r.TargetInSource && r.EnclosingSymbolId != null)
            .Select(r => new { r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line })
            .ToArrayAsync(cancellationToken);

        return rows
            .GroupBy(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line))
            .Select(g => g.Key)
            .OrderBy(k => k.TargetSymbolId, StringComparer.Ordinal)
            .Take(limit)
            .Select(k => new HandoffEntryPoint(k.TargetSymbolId, k.EnclosingSymbolId!, k.FilePath, k.Line))
            .ToArray();
    }

    // Loads the facts needed by FactEntryPointDeriver: base-type edges, constructor+type symbols,
    // and ctor reference_facts (attribute applications).  No Roslyn, no latest-run concept —
    // queries are cross-run (all facts in the DB); deduplication happens in the deriver.
    public static async Task<FactEntryPointDeriver.FactEntryPointData> LoadFactEntryPointDataAsync(
        RigDbContext context, CancellationToken cancellationToken = default)
    {
        var baseEdgeRows = await context.TypeRelationFacts
            .Where(t => t.RelationKind == "base")
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var baseEdges = baseEdgeRows
            .Select(t => (t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        var interfaceEdgeRows = await context.TypeRelationFacts
            .Where(t => t.RelationKind == "interface")
            .Select(t => new { t.TypeSymbolId, t.RelatedSymbolId })
            .ToArrayAsync(cancellationToken);
        var interfaceEdges = interfaceEdgeRows
            .Select(t => (t.TypeSymbolId, t.RelatedSymbolId))
            .Distinct()
            .ToArray();

        // All methods (not just .ctor): page EPs use the .ctor rows, class-inheritance EPs use the
        // named handler rows. IsOverride feeds RequireOverride rules (e.g. WorkflowControllerBase.OnSave).
        var methodRows = await context.SymbolFacts
            .Where(s => s.Kind == "method")
            .Select(s => new { s.SymbolId, s.Name, s.ContainingSymbolId, s.Signature, s.FilePath, s.Line, s.IsOverride })
            .ToArrayAsync(cancellationToken);
        var methods = methodRows
            .GroupBy(m => (m.FilePath, m.Line))
            .Select(g => g.First())
            .Select(m => (m.SymbolId, m.Name, m.ContainingSymbolId, m.Signature, m.FilePath, m.Line, m.IsOverride))
            .ToArray();

        var typeRows = await context.SymbolFacts
            .Where(s => s.Kind == "type")
            .Select(s => new { s.SymbolId, s.Namespace, s.FilePath, s.Line, s.Modifiers })
            .ToArrayAsync(cancellationToken);
        var types = typeRows
            .GroupBy(t => t.SymbolId)
            .Select(g => g.First())
            .Select(t => (t.SymbolId, t.Namespace, t.FilePath, t.Line,
                IsAbstract: t.Modifiers.Split(' ').Contains("abstract")))
            .ToArray();

        // ctor refs with RefKind="ctor" capture attribute applications (e.g. [ClientAction])
        // as well as regular constructor calls.  The deriver filters by TargetSymbolId prefix.
        var ctorRefRows = await context.ReferenceFacts
            .Where(r => r.RefKind == "ctor" && r.EnclosingSymbolId != null)
            .Select(r => new { r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line })
            .ToArrayAsync(cancellationToken);
        var ctorRefs = ctorRefRows
            .GroupBy(r => (r.FilePath, r.Line))
            .Select(g => g.First())
            .Select(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line))
            .ToArray();

        return new FactEntryPointDeriver.FactEntryPointData(baseEdges, methods, types, ctorRefs!, interfaceEdges);
    }

    // Loads invocation reference facts for fact-based effect derivation.
    public static async Task<IReadOnlyList<(string Target, string? Enclosing, string FilePath, int Line, string? Receiver, string? FirstArgTemplate, string? FirstArgType)>>
        LoadInvocationRefsAsync(RigDbContext context, CancellationToken cancellationToken = default)
    {
        var rows = await context.ReferenceFacts
            .Where(r => r.RefKind == "invocation")
            .Select(r => new { r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.ReceiverType, r.FirstArgumentTemplate, r.FirstArgumentType })
            .ToArrayAsync(cancellationToken);
        return rows.Select(r => (r.TargetSymbolId, r.EnclosingSymbolId, r.FilePath, r.Line, r.ReceiverType, r.FirstArgumentTemplate, r.FirstArgumentType)).ToArray();
    }
}
