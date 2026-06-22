using System.Globalization;
using Rig.Domain.Data;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Rendering;

// Renders a call-tree forest as a compact, flat TSV optimised for LLM token budgets.
// One row per included node (projection-dependent — see LlmProjection), DFS pre-order (source order,
// same walk the tree renderer uses).
//
// Two header shapes, depending on projection:
//
//   Reconstructable projections (EffectfulPaths, Full):
//     depth  name  arity  calls  effects  flags   (6 columns — NO parent)
//   The rows are DFS pre-order with a 0-based depth counter, so the parent of any row is the
//   nearest preceding row at depth-1 — fully derivable. Omitting the parent column eliminates
//   the redundancy, shrinks token count, and avoids ambiguity when two methods share a short name
//   (depth+order is the unambiguous link; a name-based parent column would be wrong on collisions).
//   No id column is needed because there are no dangling refs: every row's parent is guaranteed to
//   already appear in the output (spine-kept for EffectfulPaths; all nodes for Full).
//
//   Flat projection (EffectsFlat):
//     depth  parent  name  arity  calls  effects  flags   (7 columns — WITH parent name)
//   The flat view has gaps — intermediate spine rows are pruned — so depth+order cannot recover the
//   parent. A surrogate id would dangle (the parent row may not appear at all), so the parent
//   name is used instead. It stays informative even when the parent row is absent.
//
//   depth   – 0-based nesting depth in the original tree.
//   parent  – (EffectsFlat only) TypeName.MethodName of the direct caller; empty for roots.
//   name    – TypeName.MethodName — no namespace, no parameter types, no Roslyn arity markers.
//   arity   – parameter count (overload disambiguation without listing types).
//   calls   – number of call sites from the parent (TraceNode.CallSites).
//   effects – deduplicated + counted: "io:read*3,efcore:read*2"; single: "io:read"; empty: "".
//             ASCII only, whitespace-free: count suffix is "*N", distinct effects joined by ",".
//   flags   – pipe-separated subset of "cycle", "seen", "elided", "lambda"; empty when none.
//             "seen" = Truncated node (already expanded elsewhere or depth/budget cap hit).
//             Note: the TraceNode.Truncated flag conflates two causes — (a) already expanded
//             elsewhere (genuine re-reach) and (b) depth/budget cap; they are not distinguishable
//             from the current model without a TruncationCause field on TraceNode.
//
// Lambda rows (DocID containing "~λ") are suppressed by default (see SuppressSet).
// Compiler-generated type names ("<>c", "d__N") are suppressed by default.
// Constructor rows (.#ctor/.#cctor) are suppressed by default.
// Suppressed nodes are walked through at the parent's depth/parent; their own direct effects
// (if any) are rolled up onto the nearest non-suppressed ancestor's row — no effect is lost.
// Use SuppressSet.None to disable all suppression.
//
// Seen (Truncated) nodes ARE emitted with their effects and flags=seen — making redundant
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

    // Which node kinds to suppress (row omitted; children walked through at parent depth; direct effects
    // rolled up to nearest non-suppressed ancestor). Comma-separated when specified via --suppress.
    [Flags]
    internal enum SuppressSet
    {
        None = 0,
        Lambdas = 1,
        Ctors = 2,

        // Default for --format llm: suppress both lambdas and ctors.
        Default = Lambdas | Ctors,
    }

    // Reconstructable projections (EffectfulPaths, Full) omit the parent column — depth+order suffice.
    // EffectsFlat keeps it because the parent row may be absent (gappy flat view).
    internal static string Header(LlmProjection projection) =>
        projection == LlmProjection.EffectsFlat
            ? "depth\tparent\tname\tarity\tcalls\teffects\tflags"
            : "depth\tname\tarity\tcalls\teffects\tflags";

    // effectsByMethod: SymbolId -> list of effect strings in the same form as the tree renderer produces
    // (e.g. "io:read*3"). Structured differently from the tree-display form: the LLM renderer re-aggregates
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
        TextWriter output,
        SuppressSet suppress = SuppressSet.Default
    )
    {
        output.WriteLine(Header(projection));
        foreach (var root in roots)
        {
            // EffectfulPaths: prune roots with no downstream effect (same as the terminal default).
            if (projection == LlmProjection.EffectfulPaths && !SubtreeHasEffect(root, rawEffectsByMethod))
            {
                continue;
            }

            WalkNode(
                node: root,
                depth: 0,
                parentName: "",
                rawEffectsByMethod: rawEffectsByMethod,
                projection: projection,
                output: output,
                suppress: suppress
            );
        }
    }

    // Parse --suppress value: comma-separated list of "ctors","lambdas","none".
    // "none" overrides everything → SuppressSet.None. Unknown tokens are silently skipped.
    internal static SuppressSet ParseSuppressSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return SuppressSet.Default;
        }

        var result = SuppressSet.None;
        foreach (var token in value.Split(','))
        {
            var t = token.Trim();
            if (string.Equals(t, "none", StringComparison.OrdinalIgnoreCase))
            {
                return SuppressSet.None;
            }

            if (string.Equals(t, "lambdas", StringComparison.OrdinalIgnoreCase))
            {
                result |= SuppressSet.Lambdas;
            }
            else if (string.Equals(t, "ctors", StringComparison.OrdinalIgnoreCase))
            {
                result |= SuppressSet.Ctors;
            }
        }

        return result;
    }

    // Checks whether a SymbolId is a lambda or compiler-generated node that should be suppressed
    // under the given SuppressSet.
    internal static bool IsSuppressed(string symbolId, SuppressSet suppress = SuppressSet.Default)
    {
        if (suppress == SuppressSet.None)
        {
            return false;
        }

        if ((suppress & SuppressSet.Lambdas) != 0)
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
        }

        if ((suppress & SuppressSet.Ctors) != 0)
        {
            // Constructor nodes: DocID method segment is .#ctor or .#cctor
            if (IsCtorSymbol(symbolId))
            {
                return true;
            }
        }

        return false;
    }

    // True when the DocID method segment is .#ctor or .#cctor (constructor / static constructor).
    internal static bool IsCtorSymbol(string symbolId)
    {
        // Strip parameter list for the check.
        var paren = symbolId.IndexOf('(');
        var head = paren >= 0 ? symbolId.AsSpan(0, paren) : symbolId.AsSpan();
        return head.EndsWith(".#ctor", StringComparison.Ordinal) || head.EndsWith(".#cctor", StringComparison.Ordinal);
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

    // Format the raw effects list: aggregate by "provider:operation", emit "provider:operation*N"
    // (N > 1) or "provider:operation" (N == 1), comma-joined (no space) in first-seen order.
    // Result is ASCII-only and whitespace-free: "*" instead of " ×", "," instead of ", ".
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

        var parts = order.Select(e => counts[e] > 1 ? $"{e}*{counts[e].ToString(CultureInfo.InvariantCulture)}" : e);
        return string.Join(",", parts);
    }

    // Strip Roslyn DocID arity markers from a SHORT name (after namespace stripping).
    // Roslyn uses `N (type arity) and ``N (method arity). These appear as trailing suffixes on
    // name segments: "Concat``1" -> "Concat", "Foo`2.Bar``1" -> "Foo.Bar".
    // The arity column already carries the parameter count; the marker is noise for LLM consumption.
    // Only the trailing ``N / `N marker on each dot-segment is stripped — other characters untouched.
    // E.g. "Construct`2.New``1" -> "Construct.New" (both type and method arity markers stripped).
    internal static string StripArityMarkers(string name)
    {
        if (name.IndexOf('`') < 0)
        {
            return name;
        }

        // Process segment by segment (split on '.', strip trailing `N / ``N from each).
        var sb = new System.Text.StringBuilder(name.Length);
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            var isEnd = i == name.Length;
            var isDot = !isEnd && name[i] == '.';
            if (!isEnd && !isDot)
            {
                continue;
            }

            // Segment is name[start..i].
            var segEnd = i;
            // Walk back past digits, then past one or two backticks.
            var j = segEnd;
            while (j > start && char.IsDigit(name[j - 1]))
            {
                j--;
            }

            // Must have consumed at least one digit to strip.
            if (j < segEnd)
            {
                // Now expect one or two backticks just before the digits.
                if (j > start + 1 && name[j - 1] == '`' && name[j - 2] == '`')
                {
                    segEnd = j - 2; // strip ``N
                }
                else if (j > start && name[j - 1] == '`')
                {
                    segEnd = j - 1; // strip `N
                }
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            sb.Append(name, start, segEnd - start);
            start = i + 1; // skip the dot
        }

        return sb.ToString();
    }

    // The LLM-format short name: TypeName.MethodName — no namespace, no parameter types, no arity markers.
    // Uses SymbolNameFormatter.ShortName which takes the last two namespace-segments, then strips
    // Roslyn generic arity markers (e.g. `N / ``N) from the result.
    internal static string LlmName(string symbolId) => StripArityMarkers(ShortName(symbolId));

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

    // Collect all direct effects from a suppressed node and its suppressed-chain descendants,
    // appending into `acc`. Used to roll up effects from suppressed nodes onto the nearest
    // non-suppressed ancestor's row before that row is emitted.
    // A suppressed Truncated node contributes its own effects but not its children (same rule
    // as non-suppressed Truncated nodes — the subtree is not expanded).
    private static void CollectSuppressedEffects(
        TraceNode node,
        IReadOnlyDictionary<string, List<string>> rawEffectsByMethod,
        SuppressSet suppress,
        List<string> acc
    )
    {
        // This node's own direct effects.
        if (rawEffectsByMethod.TryGetValue(node.SymbolId, out var ownEffects))
        {
            acc.AddRange(ownEffects);
        }

        if (node.Truncated)
        {
            return; // do not expand children of a truncated node
        }

        // Recurse only into suppressed children (non-suppressed children are handled by the main walk).
        foreach (var child in node.Children)
        {
            if (IsSuppressed(child.SymbolId, suppress))
            {
                CollectSuppressedEffects(child, rawEffectsByMethod, suppress, acc);
            }
        }
    }

    private static void WalkNode(
        TraceNode node,
        int depth,
        string parentName,
        IReadOnlyDictionary<string, List<string>> rawEffectsByMethod,
        LlmProjection projection,
        TextWriter output,
        SuppressSet suppress
    )
    {
        // Determine whether this node is suppressed.
        if (IsSuppressed(node.SymbolId, suppress))
        {
            // Suppressed node is transparent: walk its non-truncated children at the same depth
            // and parentName. (Truncated suppressed nodes have no children to walk.)
            if (node.Truncated)
            {
                return;
            }

            foreach (var child in node.Children)
            {
                // EffectfulPaths: only descend into children whose subtree reaches an effect (spine prune).
                if (projection == LlmProjection.EffectfulPaths && !SubtreeHasEffect(child, rawEffectsByMethod))
                {
                    continue;
                }

                WalkNode(
                    node: child,
                    depth: depth,
                    parentName: parentName,
                    rawEffectsByMethod: rawEffectsByMethod,
                    projection: projection,
                    output: output,
                    suppress: suppress
                );
            }

            return;
        }

        // Non-suppressed node: collect effects from any immediately-suppressed direct children
        // (roll-up) BEFORE emitting this row, so the row can include those effects.
        // Only collect from suppressed children (non-suppressed children emit their own rows).
        List<string>? rolledUp = null;
        if (suppress != SuppressSet.None)
        {
            foreach (var child in node.Children)
            {
                if (IsSuppressed(child.SymbolId, suppress))
                {
                    rolledUp ??= new List<string>();
                    CollectSuppressedEffects(child, rawEffectsByMethod, suppress, rolledUp);
                }
            }
        }

        var hasEffect = rawEffectsByMethod.ContainsKey(node.SymbolId);

        // EffectsFlat: only emit rows for nodes that carry an effect (mirror --effects node selection).
        // Also emit if there are rolled-up effects from suppressed children (so effects are not lost).
        // EffectfulPaths: emit this node because the caller already checked the subtree has an effect
        //   (so the spine is always present), but we don't re-check here — the child walk below prunes.
        // Full: emit every node unconditionally.
        var hasRolledUp = rolledUp is { Count: > 0 };
        if (projection != LlmProjection.EffectsFlat || hasEffect || hasRolledUp)
        {
            var name = LlmName(node.SymbolId);
            var arity = ParseArity(node.SymbolId).ToString(CultureInfo.InvariantCulture);
            var calls = node.CallSites.ToString(CultureInfo.InvariantCulture);

            // Merge this node's own effects with any rolled-up effects from suppressed children.
            List<string> rawEffects;
            if (hasEffect && hasRolledUp)
            {
                var ownEffects = rawEffectsByMethod[node.SymbolId];
                rawEffects = new List<string>(capacity: ownEffects.Count + rolledUp!.Count);
                rawEffects.AddRange(ownEffects);
                rawEffects.AddRange(rolledUp!);
            }
            else if (hasEffect)
            {
                rawEffects = rawEffectsByMethod[node.SymbolId];
            }
            else if (hasRolledUp)
            {
                rawEffects = rolledUp!;
            }
            else
            {
                rawEffects = [];
            }

            var effectsStr = FormatEffects(rawEffects);

            // Flags: "seen" for a Truncated node (the "⋯elided" marker in the tree renderer).
            // NOTE: TraceNode.Truncated conflates two distinct causes:
            //   (a) already expanded elsewhere (genuine re-reach redundancy signal), and
            //   (b) depth/budget cap was hit (not redundancy).
            // These are not distinguishable from the current model; splitting would require a
            // TruncationCause field on TraceNode and plumbing through BuildTree. Until then, "seen"
            // is used as the single flag (more accurate than "x-phase" for the common case (a),
            // and avoids the misleading cross-phase implication for case (b)).
            var flags = BuildFlags(node);

            // Emit the row: fixed column count, no trailing tab.
            // Each projection has a fixed column count (paths/full = 6, effects = 7); empty fields
            // are empty strings. The row ends at the last column — no trailing tab, even when the
            // last column (flags) is empty.
            if (projection == LlmProjection.EffectsFlat)
            {
                // 7-column row: depth parent name arity calls effects flags
                EmitRow(output, depth.ToString(CultureInfo.InvariantCulture), parentName, name, arity, calls, effectsStr, flags);
            }
            else
            {
                // 6-column row: depth name arity calls effects flags
                EmitRow(output, depth.ToString(CultureInfo.InvariantCulture), name, arity, calls, effectsStr, flags);
            }
        }

        // seen (Truncated) nodes do NOT expand their children (the tree already chose not to).
        if (node.Truncated)
        {
            return;
        }

        var childName = LlmName(node.SymbolId);
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
                output: output,
                suppress: suppress
            );
        }
    }

    // Emit a TSV row with NO trailing tab: fields are joined by '\t' without a trailing separator.
    // Variadic params — number of fields determines the projection column count.
    private static void EmitRow(TextWriter output, params string[] fields)
    {
        output.WriteLine(string.Join("\t", fields));
    }

    private static string BuildFlags(TraceNode node)
    {
        // "seen": a Truncated node — the "⋯elided" marker in the tree renderer.
        // Covers both "already-seen" (re-reach) and depth/budget-cap truncation; the cause is not
        // distinguishable from the current model (TraceNode.Truncated is a single bool).
        var parts = new List<string>();
        if (node.Truncated)
        {
            parts.Add("seen");
        }

        if (node.EdgeKind == "cycle")
        {
            parts.Add("cycle");
        }

        return parts.Count > 0 ? string.Join("|", parts) : "";
    }
}
