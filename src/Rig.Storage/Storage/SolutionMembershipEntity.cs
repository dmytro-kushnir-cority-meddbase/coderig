namespace Rig.Storage.Storage;

// Solution↔assembly membership: which assemblies each solution contains. A solution is a VIEW over the
// shared assembly universe (see docs/multi-solution-storage.md), so the same assembly appears here under
// every solution that references it. `--solution <path>` filters queries to one solution's assemblies;
// the default query spans every stored assembly.
public sealed class SolutionMembershipEntity
{
    public string SolutionPath { get; set; } = "";

    public string AssemblyName { get; set; } = "";
}
