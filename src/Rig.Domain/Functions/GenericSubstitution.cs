using System.Text;
using System.Text.Json;

namespace Rig.Domain.Functions;

// Pure string/type logic for static monomorphization (Phase 0 of docs/design-dispatch-precision.md).
//
// A generic method `M<TEntity, Tv>` reached with concrete type args has body call-edges whose
// `ReceiverType` is a type-parameter TOKEN (e.g. "TEntity"). To monomorphize the body of such a method
// for one concrete call site, we substitute each source type-param name with the concrete type the caller
// bound. This class is JUST that substitution: functions over strings. No graph code, no Roslyn, no storage.
//
// Inputs come from the fact store as-is:
//   - the ORDERED source type-param names are mined from `symbol_facts.Signature`
//     (`…SaveServices<TEntity, Tv>(…)` -> ["TEntity","Tv"]) via ParseTypeParameterNames;
//   - the call-site binding (`MethodTypeArgBinding` / `DeclaringTypeArgBinding`) is a JSON array of
//     `<kind>:<type>` strings (`["C:Foo","C:int"]`) parsed via ParseBinding (the `C:` kind marker stripped);
//   - Substitute then rewrites a single `ReceiverType` token-wise (whole-identifier only).
public static class GenericSubstitution
{
    // Extract the ORDERED type-parameter names from a `symbol_facts.Signature`. They are the balanced
    // `<...>` group immediately BEFORE the first TOP-LEVEL `(` (the method name's type-params). The
    // parameter list (after `(`) also contains `<...>` (e.g. `EntityCollectionBase<TEntity>`) — those are
    // NOT picked up. A non-generic signature (no `<...>` before the first top-level `(`) -> empty list.
    public static IReadOnlyList<string> ParseTypeParameterNames(string? signature)
    {
        if (string.IsNullOrEmpty(signature))
        {
            return Array.Empty<string>();
        }

        // Find the first top-level `(` — the start of the parameter list. Angle-bracket depth must be 0 so
        // we don't mistake a `(` nested inside a type-param constraint/tuple within `<...>` (defensive).
        var depth = 0;
        var parenIndex = -1;
        for (var i = 0; i < signature.Length; i++)
        {
            var c = signature[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
            }
            else if (c == '(' && depth == 0)
            {
                parenIndex = i;
                break;
            }
        }

        // Everything we care about is before the first top-level `(`. (If there's no `(` at all, scan the
        // whole string — still only the method-name type-params can appear, with no parameter list.)
        var head = parenIndex >= 0 ? signature[..parenIndex] : signature;

        // The method-name type-param group is the LAST balanced `<...>` in the head (the one immediately
        // before `(`). Walk from the end: find the closing `>` at the very end of the head (after trimming),
        // then match its opening `<`.
        var end = head.Length - 1;
        while (end >= 0 && char.IsWhiteSpace(head[end]))
        {
            end--;
        }

        if (end < 0 || head[end] != '>')
        {
            return Array.Empty<string>();
        }

        // Match the `<` that opens the group ending at `end`.
        var d = 0;
        var open = -1;
        for (var i = end; i >= 0; i--)
        {
            var c = head[i];
            if (c == '>')
            {
                d++;
            }
            else if (c == '<')
            {
                d--;
                if (d == 0)
                {
                    open = i;
                    break;
                }
            }
        }

        if (open < 0)
        {
            return Array.Empty<string>();
        }

        var inner = head.Substring(open + 1, end - open - 1);
        return SplitTopLevel(inner);
    }

    // Parse the JSON-array binding form `["C:Foo","C:int"]` into `["Foo","int"]`. Uses System.Text.Json
    // because a concrete type element can contain commas inside generic args (`Dictionary<int,string>`), so
    // a naive comma-split is wrong. The leading `<kind>:` marker (up to and including the first `:`) is
    // stripped from each element — type names never contain `:`. Null/empty/invalid -> empty list (no throw).
    public static IReadOnlyList<string> ParseBinding(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return Array.Empty<string>();
        }

        string[]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<string[]>(binding);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        if (raw is null)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(raw.Length);
        foreach (var element in raw)
        {
            if (element is null)
            {
                continue;
            }

            var colon = element.IndexOf(':');
            result.Add(colon >= 0 ? element[(colon + 1)..] : element);
        }

        return result;
    }

    // True iff `binding` is a non-empty JSON array AND EVERY element's kind marker is `C:` (concrete) —
    // i.e. each element starts with "C:". A binding with any `M:`/`T:` (forwarded type-parameter) or `?`
    // (unresolved) element is NOT fully concrete. Null/empty/invalid -> false. Reuses the same JSON parse
    // as ParseBinding (System.Text.Json) so commas inside generic args don't confuse element counting.
    public static bool IsFullyConcrete(string? binding)
    {
        if (string.IsNullOrWhiteSpace(binding))
        {
            return false;
        }

        string[]? raw;
        try
        {
            raw = JsonSerializer.Deserialize<string[]>(binding);
        }
        catch (JsonException)
        {
            return false;
        }

        if (raw is null || raw.Length == 0)
        {
            return false;
        }

        foreach (var element in raw)
        {
            if (element is null || !element.StartsWith("C:", StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    // Replace each WHOLE-IDENTIFIER occurrence of a type-param name in `receiverType` with its concrete
    // from `binding` (matched by index). Returns the substituted string, or `receiverType` UNCHANGED when
    // nothing matches / inputs don't line up. Matching is whole-token only (bounded by start/end or a
    // non-identifier char; C# identifier chars = letters, digits, `_`), so `TEntityCache` and namespace
    // segments that merely contain a param name as a substring are NOT touched. A param whose index >=
    // binding.Count (arity mismatch) leaves that token unchanged (no throw, no out-of-range).
    public static string Substitute(string receiverType, IReadOnlyList<string> typeParameterNames, IReadOnlyList<string> binding)
    {
        if (string.IsNullOrEmpty(receiverType) || typeParameterNames.Count == 0)
        {
            return receiverType;
        }

        // Map each param NAME to its concrete (only those with an in-range binding can substitute).
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < typeParameterNames.Count; i++)
        {
            var name = typeParameterNames[i];
            if (string.IsNullOrEmpty(name) || i >= binding.Count)
            {
                continue;
            }

            // First declaration of a name wins (shouldn't collide in practice).
            map.TryAdd(name, binding[i]);
        }

        // Delegate the whole-token rewrite to the map overload (single substitution implementation).
        return Substitute(receiverType, map);
    }

    // Map overload of the above (Phase 2): replace each WHOLE-IDENTIFIER occurrence of a type-param NAME
    // in `receiverType` with its concrete from `map` directly, no positional zip. Same whole-token rewrite
    // (C# identifier chars only, so `TEntityCache`/namespace segments containing a param name as a substring
    // are NOT touched). Returns `receiverType` UNCHANGED when the map is empty or nothing matches. The
    // monomorphizer builds a merged method+declaring-type param map once and substitutes every body
    // receiver against it through here.
    public static string Substitute(string receiverType, IReadOnlyDictionary<string, string> map)
    {
        if (string.IsNullOrEmpty(receiverType) || map.Count == 0)
        {
            return receiverType;
        }

        var builder = new StringBuilder(receiverType.Length);
        var pos = 0;
        var changed = false;
        while (pos < receiverType.Length)
        {
            if (IsIdentifierChar(receiverType[pos]))
            {
                var start = pos;
                while (pos < receiverType.Length && IsIdentifierChar(receiverType[pos]))
                {
                    pos++;
                }

                var token = receiverType[start..pos];
                if (map.TryGetValue(token, out var concrete))
                {
                    builder.Append(concrete);
                    changed = true;
                }
                else
                {
                    builder.Append(token);
                }
            }
            else
            {
                builder.Append(receiverType[pos]);
                pos++;
            }
        }

        return changed ? builder.ToString() : receiverType;
    }

    // Split a balanced type-argument list on TOP-LEVEL commas only (commas inside nested `<...>` are kept
    // with their element), trimming each piece. Empty pieces are dropped.
    private static IReadOnlyList<string> SplitTopLevel(string inner)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                AddTrimmed(parts, inner[start..i]);
                start = i + 1;
            }
        }

        AddTrimmed(parts, inner[start..]);
        return parts;
    }

    private static void AddTrimmed(List<string> parts, string piece)
    {
        var trimmed = piece.Trim();
        if (trimmed.Length > 0)
        {
            parts.Add(trimmed);
        }
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
