namespace Rig.Domain.Data;

public record RunSummary(
    string Id,
    DateTimeOffset CreatedAtUtc,
    string SolutionPath,
    int SymbolCount,
    int ReferenceCount,
    int DiRegistrationCount,
    string? ProjectIdentity = null,
    string? SourceProjectPath = null,
    // Source-control provenance at index time (null on stores indexed before commit-stamping, or a
    // non-git source). SourceDirty = the working tree had uncommitted edits, so the store is NOT at a
    // clean commit. See docs/design-impact-behavioral-diff.md §4.5.
    string? SourceCommit = null,
    string? SourceBranch = null,
    bool SourceDirty = false
);
