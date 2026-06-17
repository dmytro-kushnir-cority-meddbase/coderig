namespace Rig.Storage.Storage;

public sealed class RunEntity
{
    public string Id { get; set; } = "";

    public string CreatedAtUtcText { get; set; } = "";

    public string SolutionPath { get; set; } = "";

    public int SymbolCount { get; set; }

    public int ReferenceCount { get; set; }

    public int DiRegistrationCount { get; set; }

    // Groups incremental per-project runs of the same solution (provenance for cross-project facts).
    public string? ProjectIdentity { get; set; }

    // The specific .csproj path indexed in this run (null for solution-level runs).
    public string? SourceProjectPath { get; set; }

    // Source-control provenance captured at index time (null when the source is not a git work tree, or
    // git was unavailable). SourceDirty = uncommitted edits were present, so this store is NOT at a clean
    // commit. The enabling primitive for commit-addressable stores — see
    // docs/design-impact-behavioral-diff.md §4.5.
    public string? SourceCommit { get; set; }

    public string? SourceBranch { get; set; }

    public bool SourceDirty { get; set; }
}
