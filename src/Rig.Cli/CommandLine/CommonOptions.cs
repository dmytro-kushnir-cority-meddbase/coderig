using System.CommandLine;
using Rig.Domain.Functions;

namespace Rig.Cli.CommandLine;

// Shared option/argument factories so every command declares the same flag the same way — one home for
// --rules, --async, --depth, --only/--exclude, --format, --limit, etc. Each factory returns a FRESH
// instance (System.CommandLine binds a value per symbol), so a command's Build() keeps the references it
// reads back in its action. This is where the hand-rolled GetOption/MaxDepthOf/ParseList loops used to
// live, now expressed once, declaratively.
internal static class CommonOptions
{
    internal static Argument<string> Pattern(string name, string description) => new(name) { Description = description };

    // --rules <path>... (repeatable): each value is resolved to a full path, matching the old loop.
    internal static Option<string[]> Rules() =>
        new("--rules")
        {
            Description = "Extra analysis-rule JSON file(s) to layer on (repeatable).",
            CustomParser = r => r.Tokens.Select(t => Path.GetFullPath(t.Value)).ToArray(),
        };

    internal static Option<bool> Async() =>
        new("--async") { Description = "Also walk async handoff edges (scheduled/cross-thread), tagged ⤳." };

    internal static Option<bool> Raw() => new("--raw") { Description = "Bypass graph shaping (factory/cut/context rules)." };

    // --maxdepth / --depth (alias): unbounded when absent (the action substitutes int.MaxValue).
    internal static Option<int?> Depth() => new("--maxdepth", "--depth") { Description = "Max traversal depth (default: unbounded)." };

    internal static Option<string[]> Only() => FilterList("--only", "Keep only these effects (provider or provider:operation).");

    internal static Option<string[]> Exclude() => FilterList("--exclude", "Drop these effects (e.g. --exclude throw).");

    // A repeatable list option whose value is split on commas OR whitespace (also ';' / tab) with empties
    // trimmed — so `--exclude throw`, `--exclude throw,llblgen:read`, `--exclude "throw cache"`, and
    // repeated flags all parse identically. The case-insensitive set is built by FilterSet at read time.
    private static Option<string[]> FilterList(string name, string description) =>
        new(name)
        {
            Description = description,
            CustomParser = r =>
                r.Tokens.SelectMany(t =>
                        t.Value.Split([',', ' ', '\t', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    )
                    .ToArray(),
        };

    internal static Option<string?> Format() => new("--format") { Description = "Output format; `tsv` for machine-readable rows." };

    internal static Option<string?> Kind() => new("--kind") { Description = "Filter by symbol/reference kind." };

    internal static Option<int> Limit(int defaultValue) =>
        new("--limit") { Description = $"Max rows to show (default {defaultValue}).", DefaultValueFactory = _ => defaultValue };

    internal static Option<bool> NoCache() => new("--no-cache") { Description = "Bypass the query cache." };

    internal static Option<bool> Time() => new("--time") { Description = "Print per-phase timings to stderr." };

    internal static Option<bool> Files() => new("--files") { Description = "Append each node's source location." };

    internal static Option<bool> Signatures() => new("--signatures", "--sig") { Description = "Show compact parameter signatures." };

    // --- value readers (the invariant translations every command shares) ---

    // Traversal DEFAULTS to SYNC-CUT: handoff edges aren't crossed unless --async opts in.
    internal static FactPathFinder.TraversalMode Mode(bool async) =>
        async ? FactPathFinder.TraversalMode.AsyncInclude : FactPathFinder.TraversalMode.SyncCut;

    // --maxdepth/--depth absent => unbounded (int.MaxValue); the closure + node cap + cycle dedup still terminate.
    internal static int DepthOrUnbounded(int? depth) => depth ?? int.MaxValue;

    // The case-insensitive effect-filter set from a parsed --only/--exclude value (null when the flag was absent).
    internal static HashSet<string> FilterSet(string[]? tokens) => new(tokens ?? [], StringComparer.OrdinalIgnoreCase);

    // --rules is null when absent; callers want an empty list.
    internal static IReadOnlyList<string> RulesOf(string[]? rules) => rules ?? [];
}
