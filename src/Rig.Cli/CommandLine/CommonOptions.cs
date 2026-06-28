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

    // --include-delivery: with --async, ALSO cross the imprecise publish→consumer delivery FAN-OUT edges
    // (an event raise / actor tell joined to EVERY same-symbol subscriber, no instance identity). Off by
    // default because that join over-approximates — it links unrelated callers to unrelated handlers
    // (see docs/FIX-event-raise-overapproximation.md). No effect without --async.
    internal static Option<bool> IncludeDelivery() =>
        new("--include-delivery")
        {
            Description =
                "With --async, also cross imprecise delivery fan-out edges (event_raise/actor_tell to all subscribers). Over-approximate.",
        };

    internal static Option<bool> Raw() => new("--raw") { Description = "Bypass graph shaping (factory/cut/context rules)." };

    // --maxdepth / --depth (alias): unbounded when absent (the action substitutes int.MaxValue).
    internal static Option<int?> Depth() => new("--maxdepth", "--depth") { Description = "Max traversal depth (default: unbounded)." };

    internal static Option<string[]> Only() =>
        FilterList(name: "--only", description: "Keep only these effects (provider or provider:operation).");

    internal static Option<string[]> Exclude() => FilterList(name: "--exclude", description: "Drop these effects (e.g. --exclude throw).");

    // --exclude-namespace <prefix>... (repeatable): drop hazard findings whose enclosing DocID namespace
    // starts with the given prefix (case-insensitive). Filters HAZARD output only — effects are unaffected.
    // Useful to suppress framework/vendored noise (e.g. --exclude-namespace Echo.Process --exclude-namespace System.).
    internal static Option<string[]> ExcludeNamespace() =>
        new("--exclude-namespace")
        {
            Description =
                "Drop hazard findings whose enclosing method namespace starts with this prefix (repeatable; case-insensitive). Filters hazards only — effects are unaffected. Example: --exclude-namespace Echo.Process --exclude-namespace System.",
            CustomParser = r => r.Tokens.Select(t => t.Value).ToArray(),
        };

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

    internal static Option<string?> Format(string? description = null, string[]? allowedValues = null)
    {
        var opt = new Option<string?>("--format") { Description = description ?? "Output format; `tsv` for machine-readable rows." };
        if (allowedValues is not null)
        {
            opt.AcceptOnlyFromAmong(allowedValues);
        }

        return opt;
    }

    // --store <ref> (aliases --commit/--at): read from a specific per-commit store instead of the latest
    // index. The ref is a store-id or a commit sha (full or short) — resolved by StoreLayout.DbPathForRef.
    internal static Option<string?> Store() =>
        new("--store", "--commit", "--at")
        {
            Description = "Read from a specific indexed store (commit sha/short-sha or store-id); default is the latest index.",
        };

    internal static Option<string?> Kind() => new("--kind") { Description = "Filter by symbol/reference kind." };

    internal static Option<int> Limit(int defaultValue) =>
        new("--limit") { Description = $"Max rows to show (default {defaultValue}).", DefaultValueFactory = _ => defaultValue };

    // Tier-1 `--limit` with NO fixed default — absent means UNBOUNDED (the action substitutes int.MaxValue).
    // Distinct from Limit(n), which symbols/refs use for their sensible fixed cap; the flood-prone traversal
    // listings (reaches/callers) default to showing everything and truncate only when a limit is given.
    internal static Option<int?> Limit() => new("--limit") { Description = "Max rows in flood-prone listings (default: unbounded)." };

    internal static Option<bool> NoCache() => new("--no-cache") { Description = "Bypass the query cache." };

    // --no-gate: disable the shared_state:read write-pairing gate. By default a static-field read effect is
    // emitted only when its cell is ALSO written somewhere (so it can pair with a write for the race_window
    // TOCTOU hazard); --no-gate emits every static-field read, including never-written const/enum cells.
    internal static Option<bool> NoGate() =>
        new("--no-gate")
        {
            Description =
                "Disable the shared_state:read write-pairing gate — emit every static-field read, including never-written const/enum cells (default: gate on).",
        };

    internal static Option<bool> Time() => new("--time") { Description = "Print per-phase timings to stderr." };

    internal static Option<bool> Files() => new("--files") { Description = "Append each node's source location." };

    internal static Option<bool> Signatures() => new("--signatures", "--sig") { Description = "Show compact parameter signatures." };

    // --- value readers (the invariant translations every command shares) ---

    // Traversal DEFAULTS to SYNC-CUT: handoff edges aren't crossed unless --async opts in. Under --async the
    // default is AsyncExact (cross sound handoffs but NOT imprecise delivery fan-out); --include-delivery
    // escalates to AsyncInclude (cross the fan-out too — the over-approximate superset). --include-delivery
    // without --async is a no-op (stays SyncCut).
    internal static FactPathFinder.TraversalMode Mode(bool async, bool includeDelivery = false) =>
        async
            ? (includeDelivery ? FactPathFinder.TraversalMode.AsyncInclude : FactPathFinder.TraversalMode.AsyncExact)
            : FactPathFinder.TraversalMode.SyncCut;

    // --maxdepth/--depth absent => unbounded (int.MaxValue); the closure + node cap + cycle dedup still terminate.
    internal static int DepthOrUnbounded(int? depth) => depth ?? int.MaxValue;

    // --format token readers: matched case-insensitively. These replace the hand-rolled
    // `string.Equals(format, "<fmt>", StringComparison.OrdinalIgnoreCase)` that was repeated at every
    // command's read site (and in `tree`'s cross-flag validator). `llm`/`llm-ids` are only meaningful for
    // `tree`; the helpers live here so the one spelling serves all callers.
    internal static bool IsTsv(string? format) => string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLlm(string? format) => string.Equals(format, "llm", StringComparison.OrdinalIgnoreCase);

    internal static bool IsLlmIds(string? format) => string.Equals(format, "llm-ids", StringComparison.OrdinalIgnoreCase);

    // The case-insensitive effect-filter set from a parsed --only/--exclude value (null when the flag was absent).
    internal static HashSet<string> FilterSet(string[]? tokens) => new(tokens ?? [], StringComparer.OrdinalIgnoreCase);

    // Returns the parsed --exclude-namespace prefixes as a list (empty when the flag was absent).
    internal static IReadOnlyList<string> NamespacePrefixes(string[]? tokens) => tokens ?? [];

    // Returns true when the enclosing DocID matches any of the given namespace prefixes. Matching strips the
    // leading "M:" kind prefix (and any other single-char kind prefix) and compares the namespace portion of
    // the remainder against each prefix, case-insensitively. An empty prefix list never matches (pass-through).
    internal static bool MatchesExcludedNamespace(string enclosing, IReadOnlyList<string> excludedPrefixes)
    {
        if (excludedPrefixes.Count == 0)
        {
            return false;
        }

        // Strip the "M:" (or other) kind prefix.
        var id = enclosing.Length > 2 && enclosing[1] == ':' ? enclosing[2..] : enclosing;
        foreach (var prefix in excludedPrefixes)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    // --rules is null when absent; callers want an empty list.
    internal static IReadOnlyList<string> RulesOf(string[]? rules) => rules ?? [];
}
