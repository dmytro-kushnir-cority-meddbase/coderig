using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Stage-2 detector: `static_init_capture`. A config / mutable value that is READ into a STATIC FIELD
// INITIALIZER is evaluated ONCE at CLR type-init and then frozen for the process lifetime — the field
// never re-reads the source, so a settings/feature-flag change is "wrong until app restart". There is no
// invalidation surface at all (distinct from FR-7 / cache_coherence, which is a MISSING invalidation of a
// cache that DOES have one). The shape on the fact store is exactly:
//
//   effect  shared_state  read  P:…Settings.ClinicalFormAutoComplete  F:…ConceptView.ClinicalFormConcept  …:141
//           └ provider     └ op  └ RESOURCE (the mutable source)        └ ENCLOSING (the field) ──────────┘
//
// i.e. an effect whose RESOURCE matches a rule-declared mutable-source pattern AND whose ENCLOSING symbol
// is a STATIC field (`F:` id present in the caller-supplied staticFieldIds set). The deriver flags exactly
// that. It is pure — no I/O, input not mutated; the static-ness join lives in the caller (DeriveCommand),
// which builds staticFieldIds from symbol_facts.Modifiers, mirroring how FactCorrelationDeriver gets its
// data handed in.
//
// v1 is DIRECT enclosure only: the Settings read must sit literally inside the static field's initializer
// (the effect's EnclosingSymbolId IS the field). The transitive case — a static field initializer that
// CALLS a helper which reads Settings — is a documented v2 follow-up (it needs a forward-reach join like
// FactCorrelationDeriver does) and is deliberately NOT built here.

// The policy for the static_init_capture instance: which RESOURCE values count as a "mutable source"
// (config / Settings.* / feature flag). Patterns are matched as SUBSTRINGS of the effect's ResourceType —
// project-specific, declared in rule data (e.g. "MedDBase.Configuration.Settings."), never hardcoded. An
// effect whose resource contains ANY of these is taint-in-scope; everything else is dropped.
public sealed record StaticInitCaptureSpec(IReadOnlyList<string> MutableSourcePatterns);

// One config/feature read frozen in a static field initializer. Method = the enclosing STATIC field's
// DocID (the `F:` id); ResourceKey = the mutable source that was read (the effect's ResourceType);
// FilePath/Line are the read effect's source location.
public sealed record StaticInitCaptureFinding(string Method, string ResourceKey, string FilePath, int Line);

public static class FactStaticInitCaptureDeriver
{
    // Returns every effect that READS a rule-declared mutable source DIRECTLY inside a STATIC field
    // initializer. An effect qualifies iff: (1) its ResourceType matches a mutable-source pattern, (2) its
    // EnclosingSymbolId is an `F:` field id, and (3) that field id is in staticFieldIds (the caller's
    // static-field universe from symbol_facts.Modifiers). Findings are de-duped and stable-sorted by
    // (Method ordinal, Line) for deterministic output. Pure — input not mutated.
    public static IReadOnlyList<StaticInitCaptureFinding> Derive(
        IReadOnlyList<DerivedEffect> effects,
        StaticInitCaptureSpec spec,
        IReadOnlySet<string> staticFieldIds
    )
    {
        var findings = new List<StaticInitCaptureFinding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in effects)
        {
            var enclosing = e.EnclosingSymbolId;
            if (enclosing is null || !enclosing.StartsWith("F:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!staticFieldIds.Contains(enclosing))
            {
                continue;
            }

            if (!MatchesMutableSource(e.ResourceType, spec.MutableSourcePatterns))
            {
                continue;
            }

            var finding = new StaticInitCaptureFinding(Method: enclosing, ResourceKey: e.ResourceType, FilePath: e.FilePath, Line: e.Line);

            if (seen.Add(finding.Method + " " + finding.ResourceKey + " " + finding.FilePath + " " + finding.Line))
            {
                findings.Add(finding);
            }
        }

        findings.Sort(
            (a, b) =>
            {
                var byMethod = string.CompareOrdinal(a.Method, b.Method);
                return byMethod != 0 ? byMethod : a.Line.CompareTo(b.Line);
            }
        );
        return findings;
    }

    // True iff `resourceType` contains any mutable-source pattern (ordinal substring). An empty pattern is
    // ignored (it would match everything). No patterns => nothing is in scope (the rule is opt-in; an empty
    // mutableSources list never fires).
    private static bool MatchesMutableSource(string resourceType, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (pattern.Length > 0 && resourceType.Contains(pattern, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
