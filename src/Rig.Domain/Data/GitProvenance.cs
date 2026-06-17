namespace Rig.Domain.Data;

// Source-control provenance of an index: the commit / branch / dirty-state of the source repo at the
// moment it was indexed. Captured by `rig index` (the git shell-out lives in the CLI) and stamped onto
// the run, so a store is addressable by commit and a diff can assert coherence (store commit == diff
// base). Best-effort: a non-git source, or absent git, yields `None` — indexing must never fail because
// git is unavailable. See docs/design-impact-behavioral-diff.md (§4.5, the enabling primitive).
public sealed record GitProvenance(string? Commit, string? Branch, bool Dirty)
{
    public static readonly GitProvenance None = new(Commit: null, Branch: null, Dirty: false);
}
