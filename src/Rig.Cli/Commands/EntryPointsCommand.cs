using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Storage.Queries;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;

namespace Rig.Cli.Commands;

// `rig entrypoints` — list the rule-detected entry points (page/action/class-inheritance + promoted async-
// handoff origins), the SAME set derive/callers/impact build, grouped by kind and — when a deployments.json
// is present — attributed to the services that host them. `--format tsv` emits one row per entry point.
//
// TODO(test): cover this command — (1) the listed set equals `rig derive`'s entry-point set (Derived +
// promoted origins, deduped); (2) tsv columns (kind, route, file, line, requires, loaded/active services);
// (3) deployment attribution when deployments.json is present vs absent; (4) --limit/--store honoured.
internal static class EntryPointsCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var rules = CommonOptions.Rules();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var store = CommonOptions.Store();
        var cmd = new Command(name: "entrypoints", description: "List the rule-detected entry points, grouped by kind.")
        {
            rules,
            format,
            limit,
            store,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        format: pr.GetValue(format),
                        limit: pr.GetValue(limit),
                        output: output,
                        workingDirectory: workingDirectory,
                        storeRef: pr.GetValue(store)
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        IReadOnlyList<string> extraRules,
        string? format,
        int? limit,
        TextWriter output,
        string workingDirectory,
        string? storeRef
    )
    {
        var tsv = string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase);
        var max = limit ?? int.MaxValue; // --limit absent => unbounded (this IS the listing)

        var rules = RuleSet.Load(workingDirectory, extraRules);
        await using var context = OpenReadContext(workingDirectory, storeRef);

        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);

        // The full entry-point set: rule-detected EPs + promoted async-handoff origins (what callers
        // --entrypoints / impact seed from), deduped + sorted by (kind, route) for a stable listing.
        var eps = epSet
            .Derived.Concat(epSet.PromotedOrigins)
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line, g.First().Requires))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        var deployments = await LoadDeploymentsAsync(context, workingDirectory);

        // --format tsv: one row per EP — kind, route, file, line, requires, loaded services, active services
        // (the last two comma-joined; empty without deployments.json).
        if (tsv)
        {
            foreach (var e in eps.Take(max))
            {
                var loaded = deployments.ServicesForFile(e.FilePath);
                var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
                output.WriteLine(
                    $"{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}"
                );
            }

            return 0;
        }

        output.WriteLine($"Entry points: {eps.Count}");
        foreach (var kindGroup in eps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(max))
            {
                WriteEntryPointLine(output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
            }

            WriteSampleTruncationNote(output, total: kindGroup.Count(), shown: max, kind: kindGroup.Key);
        }

        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(eps.Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, output);
        }

        return 0;
    }
}
