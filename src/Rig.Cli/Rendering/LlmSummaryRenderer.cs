using System.Globalization;
using Rig.Domain.Data;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Rendering;

// Renders a call-tree forest as a compact, flat TSV optimised for LLM token budgets.
// One row per included node (projection-dependent — see LlmProjection), DFS pre-order (source order,
// same walk the tree renderer uses).
//
// Header + columns:  depth  parent  name  arity  calls  effects  flags
//   depth   – 0-based nesting depth.
//   parent  – TypeName.MethodName of the direct caller; empty for roots.
//   name    – TypeName.MethodName — no namespace, no parameter types.
//   arity   – parameter count (overload disambiguation without listing types).
//   calls   – number of call sites from the parent (TraceNode.CallSites).
//   effects – deduplicated + counted: "io:read ×3, efcore:read ×2"; single: "io:read"; empty: "".
//   flags   – pipe-separated subset of "cycle", "x-phase", "elided", "lambda"; empty when none.
//
// Lambda rows (DocID containing "~λ") are suppressed.
// Compiler-generated type names ("<>c", "d__N") are suppressed.
// X-phase (Truncated) nodes ARE emitted with their effects and flags=x-phase — making redundant
// loads first-class, greppable rows is the whole point of the format.
internal static class LlmSummaryRenderer
{
    // Projection axis — orthogonal to format. Chosen by the caller based on presence of --full/--effects.
    internal enum LlmProjection
    {
        // Default (no --full / --effects): effectful-paths — only paths that reach an effect, but with
        // the ancestor spine kept. Every emitted non-root row's parent resolves to a row already emitted
        // at a shallower depth (reconstructable). Mirrors the terminal default tree prune logic.
        EffectfulPaths,

        // --full: every reachable node (mirrors terminal --full).
        Full,

        // --effects: flat list of only the effect-bearing nodes (mirrors terminal --effects flat view).
        EffectsFlat,
    }

    internal const string Header = "depth\tparent\tname\tarity\tcalls\teffects\tflags";

    // effectsByMethod: SymbolId -> list of effect strings in the same form as the tree renderer produces
    // (e.g. "io:read ×3"). Structured differently from the tree-display form: the LLM renderer re-aggregates
    // the raw provider:operation strings rather than inheriting the emoji+text rendering.
    // rawEffectsByMethod: SymbolId -> list of "provider:operation" strings (raw, one per occurrence).
    // The caller supplies BOTH because the tree renderer works with pre-formatted display strings while the
    // LLM renderer needs to aggregate provider:operation counts itself (no emoji, no resource names).
    //
    // For EffectfulPaths projection: the spine-keeping prune requires a SubtreeHasEffect check, which needs
    // the same keying as rawEffectsByMethod (presence in the dict = node has an effect).
    internal static void Render(
        IReadOnlyList<TraceNode> roots,
        // Raw provider:operation per occurrence per enclosing symbol, for LLM aggregation.
        IReadOnlyDictionary<string, List<string>> rawEffectsByMethod,
        LlmProjection projection,
        TextWriter output
    )
    {
        output.WriteLine(Header);
        foreach (var root in roots)
        {
            // EffectfulPaths: prune roots with no downstream effect (same as the terminal default).
            if (projection == LlmProjection.EffectfulPaths && !SubtreeHasEffect(root, rawEffectsByMethod))
            {
                continue;
            }

            WalkNode(node: root, depth: 0, parentName: "", rawEffectsByMethod: rawEffectsByMethod, projection: projection, output: output);
        }
    }

    // Checks whether a SymbolId is a lambda or compiler-generated node that should be suppressed.
    internal static bool IsSuppressed(string symbolId)
    {
        // Lambda nodes: DocID contains ~λ
        if (symbolId.Contains("~λ", StringComparison.Ordinal))
        {
            return true;
        }

        // Compiler-generated types: <>c (anonymous class), d__N (state machine), <>c__DisplayClassN
        // Detect them via the declaring-type segment of the DocID.
        // DocID form: M:Namespace.TypeName.MethodName(params) or M:TypeName.MethodName(params)
        // The type segment is everything between "M:" and the last dot before "(" (ShortName handles this).
        // We look for the type-segment containing "<>" patterns.
        var paren = symbolId.IndexOf('(');
        var head = paren >= 0 ? symbolId.AsSpan(0, paren) : symbolId.AsSpan();
        var lastDot = head.LastIndexOf('.');
        var typeSegment = lastDot >= 0 ? head.Slice(0, lastDot) : head;
        // The type may itself be qualified; take the last segment.
        var prevDot = typeSegment.LastIndexOf('.');
        var simpleName = prevDot >= 0 ? typeSegment.Slice(prevDot + 1) : typeSegment;

        if (
            simpleName.StartsWith("<>c", StringComparison.Ordinal)
            || simpleName.StartsWith("<>d__", StringComparison.Ordinal)
            || simpleName.Contains("d__", StringComparison.Ordinal)
        )
        {
            return true;
        }

        return false;
    }

    // Parse the parameter count from a DocID. Returns 0 for no-arg or no parameter list.
    internal static int ParseArity(string symbolId)
    {
        var open = symbolId.IndexOf('(');
        if (open < 0)
        {
            return 0;
        }

        var close = symbolId.LastIndexOf(')');
        if (close <= open)
        {
            return 0;
        }

        var inner = symbolId.AsSpan(open + 1, close - open - 1).Trim();
        if (inner.IsEmpty)
        {
            return 0;
        }

        // Count top-level commas (depth-0) — each comma separates one parameter.
        var count = 1;
        var depth = 0;
        foreach (var c in inner)
        {
            if (c is '(' or '{' or '[' or '<')
            {
                depth++;
            }
            else if (c is ')' or '}' or ']' or '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                count++;
            }
        }

        return count;
    }

    // Format the raw effects list: aggregate by "provider:operation", emit "provider:operation ×N"
    // (N > 1) or "provider:operation" (N == 1), comma-separated in first-seen order.
    internal static string FormatEffects(IReadOnlyList<string> rawEffects)
    {
        if (rawEffects.Count == 0)
        {
            return "";
        }

        // Preserve first-seen insertion order, count occurrences.
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in rawEffects)
        {
            if (counts.TryGetValue(e, out var n))
            {
                counts[e] = n + 1;
            }
            else
            {
                order.Add(e);
                counts[e] = 1;
            }
        }

        var parts = order.Select(e => counts[e] > 1 ? $"{e} ×{counts[e].ToString(CultureInfo.InvariantCulture)}" : e);
        return string.Join(", ", parts);
    }

    // The LLM-format short name: TypeName.MethodName — no namespace, no parameter types.
    // Uses SymbolNameFormatter.ShortName which takes the last two namespace-segments.
    internal static string LlmName(string symbolId) => ShortName(symbolId);

    // True when this node directly has an effect OR any descendant does (mirrors SubtreeHasEffect in
    // TreeRenderer — duplicated here to keep the renderer self-contained without a cross-type dependency).
    private static bool SubtreeHasEffect(TraceNode node, IReadOnlyDictionary<string, List<string>> rawEffectsByMethod)
    {
        if (rawEffectsByMethod.ContainsKey(node.SymbolId))
        {
            return true;
        }

        foreach (var c in node.Children)
        {
            if (SubtreeHasEffect(c, rawEffectsByMethod))
            {
                return true;
            }
        }

        return false;
    }

    private static void WalkNode(
        TraceNode node,
        int depth,
        string parentName,
        IReadOnlyDictionary<string, List<string>> rawEffectsByMethod,
        LlmProjection projection,
        TextWriter output
    )
    {
        // Suppress lambdas and compiler-generated types.
        if (IsSuppressed(node.SymbolId))
        {
            // Still walk children; they may be non-suppressed (e.g. lambda wrapping a named method).
            foreach (var child in node.Children)
            {
                WalkNode(
                    node: child,
                    depth: depth,
                    parentName: parentName,
                    rawEffectsByMethod: rawEffectsByMethod,
                    projection: projection,
                    output: output
                );
            }

            return;
        }

        var hasEffect = rawEffectsByMethod.ContainsKey(node.SymbolId);

        // EffectsFlat: only emit rows for nodes that carry an effect (mirror --effects node selection).
        // EffectfulPaths: emit this node because the caller already checked the subtree has an effect
        //   (so the spine is always present), but we don't re-check here — the child walk below prunes.
        // Full: emit every node unconditionally.
        if (projection != LlmProjection.EffectsFlat || hasEffect)
        {
            var name = LlmName(node.SymbolId);
            var arity = ParseArity(node.SymbolId).ToString(CultureInfo.InvariantCulture);
            var calls = node.CallSites.ToString(CultureInfo.InvariantCulture);
            var rawEffects = hasEffect ? rawEffectsByMethod[node.SymbolId] : [];
            var effectsStr = FormatEffects(rawEffects);

            // Flags: x-phase (Truncated = seen/elided marker), cycle is a special edge kind.
            var flags = BuildFlags(node);

            output.WriteLine(
                $"{depth.ToString(CultureInfo.InvariantCulture)}\t{parentName}\t{name}\t{arity}\t{calls}\t{effectsStr}\t{flags}"
            );
        }

        // x-phase (Truncated) nodes do NOT expand their children (the tree already chose not to).
        if (node.Truncated)
        {
            return;
        }

        var childName = IsSuppressed(node.SymbolId) ? parentName : LlmName(node.SymbolId);
        foreach (var child in node.Children)
        {
            // EffectfulPaths: only descend into children whose subtree reaches an effect (spine prune).
            if (projection == LlmProjection.EffectfulPaths && !SubtreeHasEffect(child, rawEffectsByMethod))
            {
                continue;
            }

            WalkNode(
                node: child,
                depth: depth + 1,
                parentName: childName,
                rawEffectsByMethod: rawEffectsByMethod,
                projection: projection,
                output: output
            );
        }
    }

    private static string BuildFlags(TraceNode node)
    {
        // x-phase: a Truncated node — the "⋯elided" marker in the tree renderer.
        // This covers both "already-seen" and depth-cap truncation.
        var parts = new List<string>();
        if (node.Truncated)
        {
            parts.Add("x-phase");
        }

        if (node.EdgeKind == "cycle")
        {
            parts.Add("cycle");
        }

        return parts.Count > 0 ? string.Join("|", parts) : "";
    }
}
