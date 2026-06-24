namespace Rig.Domain.Functions;

// How to canonicalize an effect's already-resolved resource string into a comparable IDENTITY (a
// "resource key"), so two effects that refer to the SAME underlying thing but resolve to different
// strings compare equal. The motivating case (FR-7 cache coherence): a bulk write's receiver type
// "AccountEntityCollection" and the matching invalidation's declaring type "AccountCache" must both
// canonicalize to "Account". The two sides of a correlation each declare their OWN spec (the strategy
// that produced the resource lives on the effect rule; only the normalization is per-side here).
//
// This is the convention-specific layer lifted OUT of bespoke detector code into one reusable place: the
// FR-7 deriver's hand-rolled SimpleTypeName + suffix-stripping (CacheOwner / EntityFromReceiver /
// CollectionSuffixes) collapse into NormalizeSpec data, and any future correlation detector (dual_write,
// …) reuses ResourceKey.Of instead of growing its own pairing heuristic.
public sealed record NormalizeSpec(bool SimpleTypeName = false, IReadOnlyList<string>? StripSuffix = null)
{
    // No canonicalization — the resolved resource is already a comparable identity (e.g. a DbSet name).
    public static readonly NormalizeSpec Identity = new();
}

public static class ResourceKey
{
    // Canonicalize `resolved` (an effect's resolved ResourceType) per `spec`:
    //   1. optionally reduce to the SIMPLE type name (drop a DocID prefix, generic/`{…}` decoration, and
    //      namespace) — "T:N.AccountEntityCollection{T}" -> "AccountEntityCollection";
    //   2. optionally strip the LONGEST matching suffix (so "EntityCollection" wins over "Collection"),
    //      provided a non-empty stem remains ("AccountCache" -[Cache]-> "Account").
    // Returns the input unchanged for an identity spec or a no-match suffix; null for null/empty input or
    // when stripping would leave an empty stem (treated like an unresolved resource).
    public static string? Of(string? resolved, NormalizeSpec spec)
    {
        if (string.IsNullOrEmpty(resolved))
        {
            return null;
        }

        var value = spec.SimpleTypeName ? SimpleName(resolved!) : resolved!;

        if (spec.StripSuffix is { Count: > 0 } suffixes)
        {
            string? best = null;
            foreach (var s in suffixes)
            {
                // Strict length guard (value.Length > s.Length): never strip to an empty stem, mirroring
                // the FR-7 deriver. Longest match wins so the ordering of the suffix list is irrelevant.
                if (
                    s.Length > 0
                    && value.Length > s.Length
                    && value.EndsWith(s, StringComparison.Ordinal)
                    && (best is null || s.Length > best.Length)
                )
                {
                    best = s;
                }
            }

            if (best is not null)
            {
                value = value[..^best.Length];
            }
        }

        return value.Length == 0 ? null : value;
    }

    // The simple type name of a (possibly generic / DocID-decorated) type string. Ported verbatim from the
    // FR-7 deriver's SimpleTypeName so the upcoming correlation-deriver replacement is behavior-identical:
    // strip a leading single-char DocID prefix ("T:"/"M:"/…), drop a generic suffix (first of '<','`','{'),
    // then take the last '.'-segment. "N.Foo.Account{T}" -> "Account"; "T:MedDBase.CompanyEntity" -> "CompanyEntity".
    private static string SimpleName(string type)
    {
        if (type.Length > 2 && type[1] == ':')
        {
            type = type[2..];
        }

        var cut = type.Length;
        foreach (var marker in new[] { '<', '`', '{' })
        {
            var idx = type.IndexOf(marker);
            if (idx >= 0 && idx < cut)
            {
                cut = idx;
            }
        }
        var bare = type[..cut];

        var dot = bare.LastIndexOf('.');
        return dot >= 0 ? bare[(dot + 1)..] : bare;
    }
}
