using Rig.Cli.CommandLine;
using Rig.Cli.EntryPoints;
using Rig.Storage.Queries;
using static Rig.Cli.Graph.TraversalGraphLoader;

namespace Rig.Cli.Services;

// The reusable assembly-reference analysis behind `rig refs --unused` / `--usage`, lifted out of
// FactCommands so BOTH the CLI and the in-process web host (Web/) run the SAME orchestration — one open of
// the read context, one DependencyGraph parse, one set of store reads, one call into the pure
// UnusedReferenceAnalyzer. No duplicated codepath to drift (the recurring effect-detection footgun).
//
// Deliberately public + primitives-in (workingDirectory/storeRef, not the internal WorkspaceLocation) so the
// contract survives a later lift to a standalone Rig.Web project — mirrors ReachesQueryService. The optional
// declaring/target substring FILTER is NOT applied here: both callers (the CLI render + the web endpoint)
// apply it themselves, exactly where they do today, so this returns the full unfiltered result.
public static class UnusedRefsQueryService
{
    // The candidate-prunable diff. SolutionAvailable is false when the indexed solution's .csproj files
    // can't be found (re-index, or run from the store's directory) — the caller renders its own message.
    public sealed record UnusedResult(bool SolutionAvailable, IReadOnlyList<(string DeclaringAsm, string UnusedAsm)> Candidates);

    // `rig refs --unused`: diff the DECLARED <ProjectReference> graph (parsed from the solution's .csproj
    // files, no MSBuild) against the OBSERVED first-party usage edges (mined from facts), yielding the
    // declared assembly edges with zero symbol usage. Mirrors FactCommands.RunUnusedRefsAsync's data path.
    public static async Task<UnusedResult> UnusedAsync(string workingDirectory, string? storeRef = null)
    {
        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );

        var solutionPath = await EntryPointContext.PrimaryDeploymentSolutionPathAsync(context);
        if (solutionPath is null || !File.Exists(solutionPath))
        {
            return new UnusedResult(SolutionAvailable: false, Candidates: []);
        }

        var declared = await Rig.Cli.DependencyGraph.BuildAsync(solutionPath);
        var files = await Reads.LoadFileAssembliesAsync(context);
        var usageRows = await Reads.LoadAssemblyUsageEdgesAsync(context);

        var csprojToAsm = UnusedReferenceAnalyzer.BuildCsprojToAssembly(csprojPaths: declared.Keys.ToList(), files: files);
        var usageEdges = new HashSet<(string, string)>(usageRows.Select(u => (u.UsingAsm, u.UsedAsm)));
        var candidates = UnusedReferenceAnalyzer.FindUnused(
            declaredCsprojGraph: declared,
            csprojToAsm: csprojToAsm,
            usageEdges: usageEdges
        );

        return new UnusedResult(SolutionAvailable: true, Candidates: candidates);
    }

    // `rig refs --usage`: inbound first-party reference count per assembly, ascending (least-used first —
    // the SQL orders it). Mirrors FactCommands.RunUsageRefsAsync's data path.
    public static async Task<IReadOnlyList<(string Assembly, int Refs, int FromMethods)>> UsageAsync(
        string workingDirectory,
        string? storeRef = null
    )
    {
        await using var context = await OpenReadContextGatedAsync(
            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeRef)
        );
        return await Reads.LoadAssemblyUsageCountsAsync(context);
    }
}
