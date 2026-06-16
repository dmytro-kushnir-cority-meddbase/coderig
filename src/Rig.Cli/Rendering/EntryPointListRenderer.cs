using Rig.Cli.Deployments;
using Rig.Domain.Data;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Rendering;

// Renders the entry-point LISTINGS that derive / callers --entrypoints print: the per-EP line (route +
// deployment chip + location), the per-service rollup table, and the small shared bits (the sample-
// truncation note, the resource-span hazard tag). EntryPointRenderer owns the single-EP chip; this owns
// the multi-EP listing/rollup built on top of it.
internal static class EntryPointListRenderer
{
    // The two-line "custom" EP listing line (Format A) when deployment data exists; the plain
    // route + location otherwise. The kind is supplied by the caller's group header, so it's not
    // repeated on the line — only the ▶ marker, route, deployment chip, then the indented location.
    internal static void WriteEntryPointLine(
        TextWriter output,
        DeploymentMap deployments,
        string route,
        string filePath,
        int line,
        IReadOnlyList<string>? requires = null
    )
    {
        if (deployments.IsEmpty)
        {
            output.WriteLine($"      {route}  {ShortenPath(filePath)}:{line}");
            return;
        }
        output.WriteLine($"      {EntryPointRenderer.Marker} {route}  {EntryPointRenderer.DeployTag(deployments, filePath, requires)}");
        output.WriteLine($"          {ShortenPath(filePath)}:{line}");
    }

    // The per-kind listing is a SAMPLE (readability). Say so when truncated, so a grep over this output is
    // never silently a false negative — the full set is in `--format tsv` (or raise --limit).
    internal static void WriteSampleTruncationNote(TextWriter output, int total, int shown, string kind)
    {
        if (total > shown)
            output.WriteLine($"      … +{total - shown} more {kind} (sample shown; `rig derive --format tsv` lists all)");
    }

    // Per-service rollup of entry points: total + per-kind breakdown, in deployments.json order.
    // An EP counts in every service it is ACTIVE-IN (loaded AND capability-gated in) — so a gated
    // actor counts only in the host(s) that `provides` its required token, not in every host that
    // merely links it. A service that LOADS an EP but is gated out of it is still listed, with a
    // `· N linked-inactive` tail (and a 0 active count when it activates none) — so the "loaded here,
    // doesn't run here" signal is visible in the rollup, not just on each EP line. EPs whose owning
    // project is in no service closure (tests/tools) fall into "(unattributed)".
    internal static void WriteServiceSummary(
        IEnumerable<(string Kind, string? FilePath, IReadOnlyList<string>? Requires)> eps,
        DeploymentMap deployments,
        TextWriter output
    )
    {
        var byService = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var inactive = new Dictionary<string, int>(StringComparer.Ordinal); // loaded but gated out
        var unattributed = 0;
        foreach (var (kind, filePath, requires) in eps)
        {
            var loaded = deployments.ServicesForFile(filePath);
            if (loaded.Count == 0)
            {
                unattributed++;
                continue;
            }
            var active = deployments.ActiveServices(loaded, requires);
            foreach (var s in active)
            {
                if (!byService.TryGetValue(s, out var kinds))
                    byService[s] = kinds = new Dictionary<string, int>(StringComparer.Ordinal);
                kinds[kind] = kinds.GetValueOrDefault(kind) + 1;
                totals[s] = totals.GetValueOrDefault(s) + 1;
            }
            // Services that link the EP's code but are gated out of activating it.
            foreach (var s in loaded)
                if (!active.Contains(s))
                    inactive[s] = inactive.GetValueOrDefault(s) + 1;
        }

        output.WriteLine();
        output.WriteLine("Entry points per deployed service (active-in; `· N linked-inactive` = loaded but gated out of that host):");
        foreach (var svc in deployments.Services)
        {
            var total = totals.GetValueOrDefault(svc.Name);
            var inactiveCount = inactive.GetValueOrDefault(svc.Name);
            if (total == 0 && inactiveCount == 0)
                continue;
            var breakdown =
                total == 0
                    ? ""
                    : string.Join(
                        " ",
                        byService[svc.Name]
                            .OrderByDescending(k => k.Value)
                            .ThenBy(k => k.Key, StringComparer.Ordinal)
                            .Select(k => $"{k.Key}={k.Value}")
                    );
            var inactiveTail = inactiveCount > 0 ? $"   · {inactiveCount} linked-inactive" : "";
            var label = svc.Kind is null ? svc.Name : $"{svc.Name} ({svc.Kind})";
            output.WriteLine($"  {label, -46} {total, 6}   {breakdown}{inactiveTail}");
        }
        if (unattributed > 0)
            output.WriteLine($"  {"(unattributed — tests/tools/no service)", -46} {unattributed, 6}");
    }

    // A resource-span hazard tag for an effect (P2b ordering/nesting): a network/IO/external effect
    // that fires while a transaction or lock is held ("transaction spans a network call" / "lock held
    // across IO"). Empty when the effect carries no span observation.
    internal static string SpanTag(DerivedEffect effect)
    {
        var span = (effect.Observations ?? []).FirstOrDefault(o => o.Type is "transaction_spans_effect" or "lock_held_across_effect");
        if (span is null)
            return "";
        return span.Type == "transaction_spans_effect" ? "  ⚠ inside-open-tx" : "  ⚠ lock-held-across";
    }
}
