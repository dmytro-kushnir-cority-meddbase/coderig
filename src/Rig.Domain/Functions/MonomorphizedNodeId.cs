namespace Rig.Domain.Functions;

// Phase 2 of static monomorphization (docs/design-dispatch-precision.md): the deterministic SYNTHETIC ID
// for one monomorphized instantiation of a generic method. Mirrors the lambda-id precedent
// (`{containerMemberId}~λ{ordinal}`, recognized via the `~λ` marker in TreeRenderer): a monomorphized node
// is `{baseMethodId}~mono⟨{declJoin};{methJoin}⟩`, where the two binding lists are the parsed, concrete
// declaring-type and method type args (the GenericInstantiation bindings).
//
// The VISIBLE `~mono` marker survives so Phase 3 display-collapse can recognize a monomorphized node and
// fold it back to its base method for rendering (like the lambda `~λ` collapse). Binding elements are
// joined with the UNIT SEPARATOR (U+001F) — a control char that cannot occur in a DocID or a type name —
// so the id round-trips unambiguously (BaseOf strips from the `~mono` marker onward) and two distinct
// bindings always yield distinct ids while the same binding yields the same id.
public static class MonomorphizedNodeId
{
    // The visible marker that tags a monomorphized node id (analogous to `~λ` for lambdas).
    public const string Marker = "~mono";

    // The unit separator joins binding elements: a control char absent from DocIDs and type names, so it
    // never collides with content inside a binding element.
    private const char ElementSeparator = '';

    // Deterministic synthetic id for the instantiation of `baseMethodId` with the given concrete declaring-
    // type and method bindings: `{baseMethodId}~mono⟨{declJoin};{methJoin}⟩`. Empty bindings render as an
    // empty join (a non-generic declaring type or a non-generic method contributes nothing on its side).
    public static string For(string baseMethodId, IReadOnlyList<string> declaringBinding, IReadOnlyList<string> methodBinding) =>
        baseMethodId + Marker + "⟨" + Join(declaringBinding) + ";" + Join(methodBinding) + "⟩";

    // True when `id` is a monomorphized node id (carries the `~mono` marker).
    public static bool IsMonomorphized(string id) => id.IndexOf(Marker, StringComparison.Ordinal) >= 0;

    // The base (generic) method id of a monomorphized node — everything before the `~mono` marker. Returns
    // `id` unchanged when it is not a monomorphized id (no marker present).
    public static string BaseOf(string id)
    {
        var marker = id.IndexOf(Marker, StringComparison.Ordinal);
        return marker < 0 ? id : id.Substring(startIndex: 0, length: marker);
    }

    private static string Join(IReadOnlyList<string> binding) => string.Join(ElementSeparator.ToString(), binding);
}
