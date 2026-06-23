namespace Rig.Analysis.Inventory;

// What the design-time-build cache holds for a project (the sidecar payload): the input fingerprint the
// build output was produced under, plus that output. Kept separate from "does it still match" so the match
// is a pure decision, not a side effect of loading.
internal sealed record StoredBuild(string Fingerprint, ProjectBuildInfo Info);

// PURE CORE of the build cache: given the freshly-computed input fingerprint and whatever the sidecar holds
// (if anything), decide HIT (replay the cached build output, skip the design-time build) or MISS (rebuild,
// then store under the new fingerprint). No IO, no clock — the correctness-bearing choice, isolated so it is
// exhaustively unit-testable and reused verbatim by --verify-build-cache. The imperative shell (Gather/Load/
// build/Store in SolutionSourceLoader) only feeds it inputs and acts on its verdict.
internal abstract record BuildCacheDecision
{
    private BuildCacheDecision() { }

    // The fingerprint matched a stored sidecar — replay Info without building.
    internal sealed record Hit(ProjectBuildInfo Info) : BuildCacheDecision;

    // No sidecar, or its fingerprint is stale — build, then Store under Fingerprint.
    internal sealed record Miss(string Fingerprint) : BuildCacheDecision;

    // A sidecar HITS only when it exists AND its stored fingerprint equals the current one; absent or stale
    // is a MISS carrying the current fingerprint to store after the rebuild.
    public static BuildCacheDecision Decide(string currentFingerprint, StoredBuild? stored) =>
        stored is not null && string.Equals(stored.Fingerprint, currentFingerprint, StringComparison.Ordinal)
            ? new Hit(stored.Info)
            : new Miss(currentFingerprint);
}
