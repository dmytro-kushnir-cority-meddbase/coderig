using Rig.Domain.Data;

namespace Rig.Domain.Functions;

// Resolves the external-virtual-override orphan (docs/backlog.md): a call binding to an EXTERNAL convenience
// overload (e.g. `EntityBase.Save()`) is dropped by the TargetInSource graph filter, so the first-party
// override that the convenience method trampolines into (inside the external DLL) is never reached. A
// `redirectRules` entry rewrites such a call to the external VIRTUAL hatch it trampolines to and the
// projection KEEPS that edge past the filter; existing receiver-narrowed dispatch then fans the kept virtual
// node to the first-party override. The redirect mapping is authored from the decompiled trampoline bodies.
//
// Applied DURING projection (reference-fact -> CallEdge), NOT as a post-pass on the loaded graph: the
// orphaned edge is gone after the TargetInSource filter, so the redirect must run at the seam where the
// external target is still visible. Both the SQL materializer / EF-fallback (Reads) and the test projection
// (FactProjection) call Redirect so they agree by construction (mirrors HandoffClassifier).
public static class RedirectClassifier
{
    // The redirect-target DocID if `targetDocId` matches a rule (a convenience overload of a redirected
    // method), else null. Never redirects the virtual target overload to itself. Match is on the
    // signature-stripped DocID (declaring-type + method name), so every convenience overload — Save(),
    // Save(bool), Save(IPredicate) — maps to the one virtual hatch with a single rule.
    public static string? Redirect(string targetDocId, IReadOnlyList<FactRedirectRule>? rules)
    {
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        var stripped = StripSignature(targetDocId);
        foreach (var rule in rules)
        {
            if (
                string.Equals(stripped, rule.Method, StringComparison.Ordinal)
                && !string.Equals(targetDocId, rule.RedirectTo, StringComparison.Ordinal)
            )
            {
                return rule.RedirectTo;
            }
        }

        return null;
    }

    private static string StripSignature(string docId)
    {
        var paren = docId.IndexOf('(', StringComparison.Ordinal);
        return paren < 0 ? docId : docId[..paren];
    }
}
