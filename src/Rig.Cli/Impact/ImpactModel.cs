namespace Rig.Cli.Impact;

// The source-control provenance of a store, condensed for the header: a short commit (12-char sha) +
// branch when the store carries them, else a fallback label (the store-ref the user passed). Label is
// ALWAYS non-empty so the header can name which side it is even on a pre-stamping store.
internal sealed record StoreProvenance(string? Branch, string? ShortCommit, string Fallback)
{
    // The header label for this side: "<branch> (<short>)" when both are known, "<branch>" / "(<short>)"
    // when only one is, else the fallback store-ref. The diff-summary uses the same short form.
    public string Label =>
        (Branch, ShortCommit) switch
        {
            ({ Length: > 0 } b, { Length: > 0 } c) => $"{b} ({c})",
            ({ Length: > 0 } b, _) => b,
            (_, { Length: > 0 } c) => $"({c})",
            _ => Fallback,
        };

    // The compact label the diff-summary line leads with: the short commit when known, else the branch,
    // else the fallback store-ref.
    public string ShortLabel =>
        ShortCommit is { Length: > 0 } c ? c
        : Branch is { Length: > 0 } b ? b
        : Fallback;
}

// A derived entry point at a source site, with its deployment requirements — the unit the impact output
// lists and groups. Kind = action/http/page/…; Route = display route; Requires = deployment gates.
internal sealed record EntryPointRef(string Kind, string Route, string FilePath, int Line, IReadOnlyList<string>? Requires);

// The PROVEN store-vs-store diff: the entry-point set diff, the entry points whose reachable EFFECT set
// changed (PerEp — the behavioral signal), and the entry points whose reachable TREE changed (AffectedEps —
// structural). All three are derived purely from the two indexed stores.
internal sealed record ImpactDiff(EpDiff? Ep, IReadOnlyList<EpReachDelta> AffectedEps, IReadOnlyList<EpFootprintDelta> PerEp);

internal sealed record EpDiff(IReadOnlyList<(string Kind, string Route)> Added, IReadOnlyList<(string Kind, string Route)> Removed);

// The reach MULTIPLICITY + loop context of one effect key from one EP (Feature 1). Count is the number
// of distinct reachable effect-bearing enclosing nodes that produce this key (a derivable proxy for "how
// many times" — a higher count means the effect is produced from more reachable sites). InLoop is true
// when ANY of those producing nodes is reached under an enclosing loop somewhere on its call path (the
// BFS-shortest-path NearestLoopKind, the same loop context the tree's 🔁 uses). This is the per-key
// cardinality + loop flag the bare reachable-SET dedup throws away.
internal sealed record EffectReach(int Count, bool InLoop);

// One effect that is AMPLIFIED on an EP (Feature 1): its key is present on BOTH stores (so the set-diff
// says "unchanged"), but it is now produced MORE (BranchCount > BaseCount) and/or has MOVED INTO A LOOP
// (BranchInLoop && !BaseInLoop). A FLAG FOR REVIEW, not a verdict — the static signal can't distinguish a
// harmless hot-cache 2nd read from a real extra cold DB call, so the rendering says "review".
internal sealed record EpEffectAmplified(
    string Provider,
    string Operation,
    string Resource,
    string Enclosing,
    int BaseCount,
    int BranchCount,
    bool BaseInLoop,
    bool BranchInLoop
);

// One entry point whose reachable-effect FOOTPRINT differs between the two stores, with the per-EP
// added/removed effects (set membership) AND the amplified effects (Feature 1 — same key, produced more
// or now in a loop). EffectKey = (provider, operation, resource, param-free enclosing method). An EP is
// listed when Added/Removed OR Amplified is non-empty (the behavioral set = set-changed ∪ amplified).
internal sealed record EpFootprintDelta(
    string Kind,
    string Route,
    // The EP's source site — carried so the card can render the FQN (FqnForCard), same as the structural
    // list. Empty/0 when the EP's site is unknown (then the card shows the route).
    string FilePath,
    int Line,
    int BranchEffects,
    int BaseEffects,
    IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> Added,
    IReadOnlyList<(string Provider, string Operation, string Resource, string Enclosing)> Removed,
    IReadOnlyList<EpEffectAmplified> Amplified,
    // FR-1e: true when the BRANCH reach still carries a `shared_state` effect (a mutation of an
    // inherently-shared cell — ConcurrentDictionary/Atom/ImmutableInterlocked/static-field-write). This
    // is the bit the Added/Removed lists can't carry: a shared mutation present on BOTH sides is absent
    // from the set-diff, yet it's exactly what makes a lock/guard ADD or REMOVE on this EP race-relevant.
    // Defaults false so existing constructions/tests are unaffected.
    bool SharedMutationOnPath = false,
    // HAZARD DELTA: the hazard findings (race_window / lazy_init_race / n_plus_1 / unserializable_payload —
    // see HazardKinds) this EP's reach GAINED (HazardsAdded — head-only) or LOST (HazardsRemoved —
    // base-only) between the two stores. A hazard finding is keyed on (Type, Cell, param-free Enclosing)
    // and carries its confidence tier. This is the hazard mirror of the effect Added/Removed lists — a
    // refactor that opened a race_window on a path, or a fix that closed one. Defaulted empty so existing
    // constructions/tests (and OLD cache blobs) are unaffected.
    IReadOnlyList<HazardFinding>? HazardsAdded = null,
    IReadOnlyList<HazardFinding>? HazardsRemoved = null
)
{
    // Normalized non-null views so callers (ordering, rendering, the cache codec) never NRE on the
    // defaulted-null hazard lists (a delta with no hazard change, or an OLD cache blob / effect-only test).
    public IReadOnlyList<HazardFinding> HazardsAddedOrEmpty => HazardsAdded ?? [];
    public IReadOnlyList<HazardFinding> HazardsRemovedOrEmpty => HazardsRemoved ?? [];
}

// One hazard finding on an EP's reach: a hazard observation (race_window / lazy_init_race / n_plus_1 /
// unserializable_payload) on a reachable effect. Keyed on (Type, Cell, param-free Enclosing) so it diffs
// line/signature-insensitively, exactly as the effect key does — Cell is the observation's Context (the
// shared cell for race_window, the loop identifier for n_plus_1, …), Enclosing the param-free producing
// method. Confidence is the disclosed tier (high/medium/low); it rides along for rendering but is DELIBERATELY
// EXCLUDED from equality/hash (overridden below) so the diff identity is exactly (Type, Cell, Enclosing) — a
// confidence change on the SAME finding is not a gain/loss, and the set-union/Distinct dedup keys on identity.
internal sealed record HazardFinding(string Type, string Cell, string Enclosing, string Confidence)
{
    public bool Equals(HazardFinding? other) =>
        other is not null
        && string.Equals(Type, other.Type, StringComparison.Ordinal)
        && string.Equals(Cell, other.Cell, StringComparison.Ordinal)
        && string.Equals(Enclosing, other.Enclosing, StringComparison.Ordinal);

    public override int GetHashCode() => HashCode.Combine(Type, Cell, Enclosing);
}

// One entry point whose REACHABLE SYMBOL SET (its full forward-reach tree, structural — not effects)
// differs between the two stores. This is the line-number-insensitive "two trees, diffed" signal: an EP
// is affected iff what it reaches changed — a new/removed/renamed method anywhere in its reach (incl. the
// obj→sql kind of migration the effect-set diff collapses).
//
// The symbol moves are bucketed by PARAM-FREE STEM (StripParams), so a signature/overload change reads as
// ONE change, not an add+remove pair: AddedStems = stems only in the added set, RemovedStems = stems only
// in the removed set, ChangedStems = stems present on BOTH sides (a signature change — e.g. a ctor whose
// params moved). Added/Removed keep the RAW first-party method DocIDs that belong to a genuinely added /
// removed stem (NOT the signature-changed ones) so tooling (tsv) still has the exact ids; ChangedStems
// carries the param-free stems for the `~` rows. DistinctStemDelta is the dedup'd magnitude (added ∪
// removed ∪ changed stems) that ranks the list (Task 2) — a 30-overload swap counts as 1, not 30.
//
// InPlace (Phase 2) is the orthogonal IN-PLACE signal: reachable method DocIDs whose declaration BODY hash
// differs base↔branch even though they stayed in the reach set (a changed constant/literal — no call-
// structure move). An EP can be affected by ONLY this (empty stem buckets, non-empty InPlace) — e.g. a
// reachable method's body changed but nothing was added/removed/re-signed. InPlaceCount is the full count;
// InPlace carries a few sample DocIDs for display.
internal sealed record EpReachDelta(
    string Kind,
    string Route,
    string FilePath,
    int Line,
    IReadOnlyList<string>? Requires,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> AddedStems,
    IReadOnlyList<string> RemovedStems,
    IReadOnlyList<string> ChangedStems,
    int DistinctStemDelta,
    int InPlaceCount = 0,
    IReadOnlyList<string>? InPlace = null
);

// The stem-bucketed partition of one EP's added/removed reachable DocID sets: a symbol present in BOTH
// sets under the same param-free stem is a SIGNATURE CHANGE (Changed), not an add+remove. Added/Removed
// keep only the raw DocIDs whose stem is genuinely one-sided; AddedStems/RemovedStems/ChangedStems are the
// param-free stems for display + counting. Pure + internal so the bucketing is unit-testable in isolation.
internal sealed record StemBuckets(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> AddedStems,
    IReadOnlyList<string> RemovedStems,
    IReadOnlyList<string> ChangedStems
);

// Why an entry point's reachable TREE changed, when its EFFECT set did NOT — the cause buckets for the
// structural-only breadcrumb. RecordShape: the moved members are DOMINATED by data-shape changes — a record
// gained/lost a field, so every reaching EP sees the new field accessors + the ctor signature move. This is
// the dominant noise, and it dominates even when a handful of real methods moved alongside (e.g. a deleted
// settings type's deserializer), because the CAUSE is still the one data-shape change. CtorSig: the move is
// purely constructor signatures (the data-shape change seen only at the ctor). InPlace: a reachable body
// changed with no structural move. Other: real method-level reach churn is a MEANINGFUL fraction — these are
// the genuine migration/refactor sites a reviewer should look at (a migration can move reach with no net-new
// effect kind, so it lands here, not in the noise).
internal enum StructuralCause
{
    RecordShape,
    CtorSig,
    InPlace,
    Other,
}
