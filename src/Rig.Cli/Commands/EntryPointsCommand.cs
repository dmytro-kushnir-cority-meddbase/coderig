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
                        new Options(
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Format: pr.GetValue(format),
                            Limit: pr.GetValue(limit)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, pr.GetValue(store)))
                    )
            )
        );
        return cmd;
    }

    // Bound option values for `rig entrypoints`. IO wiring (output, error, workingDirectory, storeRef)
    // lives in CommandIo; only the command-specific user options live here.
    private sealed record Options(IReadOnlyList<string> ExtraRules, string? Format, int? Limit);

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var max = opts.Limit ?? int.MaxValue; // --limit absent => unbounded (this IS the listing)

        var rules = RuleSetLoader.Load(io.WorkspaceLocation.WorkingDirectory, opts.ExtraRules);
        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation);

        var epData = await Reads.LoadFactEntryPointDataAsync(context);
        var epSet = await DeriveEntryPointsAsync(context, epData, rules);

        // (file,line) -> handler DocID, so each EP line/row can carry the queryable FQN beside its slash route.
        var docIdBySite = MethodDocIdBySite(epData);

        // The full entry-point set: rule-detected EPs + promoted async-handoff origins (what callers
        // --entrypoints / impact seed from), deduped + sorted by (kind, route) for a stable listing.
        var eps = epSet
            .Derived.Concat(epSet.PromotedOrigins)
            .GroupBy(e => (e.Kind, e.Route, e.FilePath, e.Line))
            .Select(g => (g.Key.Kind, g.Key.Route, g.Key.FilePath, g.Key.Line, g.First().Requires))
            .OrderBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Route, StringComparer.Ordinal)
            .ToList();

        var deployments = await LoadDeploymentsAsync(context, io.WorkspaceLocation.WorkingDirectory);

        // --format tsv: one row per EP — kind, route, file, line, requires, loaded services, active services,
        // fqn (the last new: the queryable dotted name, == route when the route already is the FQN, falls back
        // to route for sites with no indexed method). The two service columns are comma-joined; empty without
        // deployments.json.
        if (tsv)
        {
            foreach (var e in eps.Take(max))
            {
                var loaded = deployments.ServicesForFile(e.FilePath);
                var active = deployments.ActiveServices(loadedServices: loaded, requires: e.Requires);
                io.TextOutput.Output.WriteLine(
                    $"{e.Kind}\t{e.Route}\t{e.FilePath}\t{e.Line}\t{string.Join(',', e.Requires ?? [])}\t{string.Join(',', loaded)}\t{string.Join(',', active)}\t{FqnOrRoute(route: e.Route, filePath: e.FilePath, line: e.Line, docIdBySite: docIdBySite)}"
                );
            }

            return 0;
        }

        io.TextOutput.Output.WriteLine($"Entry points: {eps.Count}");
        foreach (var kindGroup in eps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            io.TextOutput.Output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(max))
            {
                WriteEntryPointLine(
                    io.TextOutput.Output,
                    deployments,
                    route: e.Route,
                    filePath: e.FilePath,
                    line: e.Line,
                    requires: e.Requires,
                    fqn: FqnOrRoute(route: e.Route, filePath: e.FilePath, line: e.Line, docIdBySite: docIdBySite)
                );
            }

            WriteSampleTruncationNote(io.TextOutput.Output, total: kindGroup.Count(), shown: max, kind: kindGroup.Key);
        }

        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(eps.Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, io.TextOutput.Output);
        }

        return 0;
    }
}
