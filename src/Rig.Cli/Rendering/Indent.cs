namespace Rig.Cli.Rendering;

// Centralizes the leading-whitespace OFFSETS used across the rendered listings, so indentation lives in
// one place as a computed `new string(' ', n)` rather than scattered bare space-literals (easy to
// miscount, and they drift). Offsets are even — two spaces per nesting level — so every fixed offset is
// `Of(depth)` for some depth; the common ones are memoized as fields and the tree/path walkers call
// `Of(depth)` directly for their variable depth.
internal static class Indent
{
    internal static readonly string L1 = Of(1); //  2 — a top-level list row / rollup row
    internal static readonly string L2 = Of(2); //  4 — a nested detail line under a list row
    internal static readonly string L3 = Of(3); //  6 — a route / per-method detail line
    internal static readonly string L5 = Of(5); // 10 — a location under its route

    // `depth` nesting levels of indent (two spaces each) — the computed offset the tree/path render uses.
    internal static string Of(int depth) => new(' ', depth * 2);
}
