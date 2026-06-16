using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig derive` — the stage-2 pass over facts (no Roslyn): re-derives effects, page/action entry points, and
// delegate/method-group handoff entry points from the reference index in a single command, one DB open, one
// rule load. Effects and entry points are matched against the same AnalysisRuleSet JSON the Roslyn pass uses
// (detectors are data, not code). `--format tsv` emits full-fidelity rows for tooling.
internal static class DeriveCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var rules = CommonOptions.Rules();
        var limit = CommonOptions.Limit(40);
        var only = CommonOptions.Only();
        var exclude = CommonOptions.Exclude();
        var format = CommonOptions.Format();
        var cmd = new Command(name: "derive", description: "Re-derive effects + entry points from facts (no Roslyn).")
        {
            rules,
            limit,
            only,
            exclude,
            format,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        extraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                        limit: pr.GetValue(limit),
                        only: CommonOptions.FilterSet(pr.GetValue(only)),
                        exclude: CommonOptions.FilterSet(pr.GetValue(exclude)),
                        format: pr.GetValue(format),
                        output: output,
                        error: error,
                        workingDirectory: workingDirectory
                    )
            )
        );
        return cmd;
    }

    private static async Task<int> RunAsync(
        IReadOnlyList<string> extraRules,
        int limit,
        HashSet<string> only,
        HashSet<string> exclude,
        string? format,
        TextWriter output,
        TextWriter error,
        string workingDirectory
    )
    {
        var handoffRules = FactHandoffRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        await using var context = OpenReadContext(workingDirectory);

        // Deployment attribution (opt-in: only when deployments.json sits next to .rig). Empty (no-op) when
        // the config is absent; `error` is the log sink so config problems surface.
        var deployments = await LoadDeploymentsAsync(context, workingDirectory, error);

        // Classified handoffs (background/timer/actor/event) shared by the listing, the origin-EP promotion,
        // and the TSV output — derived once. The total count yields the unclassified residual (a count, not a
        // listing), which is why this is loaded here rather than via DeriveEntryPointsAsync (which drops it).
        var handoffs = await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue, handoffRules);
        var classifiedHandoffs = handoffs.Where(h => h.Dispatcher is not null).ToList();
        var unclassifiedHandoffCount = handoffs.Count - classifiedHandoffs.Count;

        // Entry-point fact data is loaded up front: its base edges also feed the effect deriver's base-type
        // gates (e.g. clientpage_proxy = declaring type derives MedDBase.Pages.ProxyBase).
        var epData = await Reads.LoadFactEntryPointDataAsync(context);

        // --- Effects (data-driven over facts) ---
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var effects = DeriveEffects(
            workingDirectory: workingDirectory,
            extraRules: extraRules,
            invocations: invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs
        );
        effects = ApplyEffectFilters(effects: effects, only: only, exclude: exclude); // --only / --exclude (e.g. --exclude throw)

        // Machine-readable mode: emit full-fidelity rows (full DocIDs/paths) for tooling that joins
        // effects/entry points against the call graph. `rig derive --format tsv`.
        if (string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var e in effects)
            {
                var observations = string.Join(',', (e.Observations ?? []).Select(o => o.Type));
                output.WriteLine(
                    $"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{observations}"
                );
            }

            var tsvEpRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
            var tsvClassRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
            var tsvEps = FactEntryPointDeriver.Derive(epData, tsvEpRules, tsvClassRules);
            // Trailing columns (comma-joined, empty when no deployments.json): `service` = the hosts that
            // LOAD the EP (link its code); `activeService` = the subset it is ACTIVE-IN after the capability
            // gate (== service when the EP is ungated). `service` is kept for back-compat; tooling that wants
            // runs-here filters on the new `activeService` column.
            foreach (var ep in tsvEps.Concat(PromoteHandoffOrigins(classifiedHandoffs, tsvEps)))
            {
                var loaded = deployments.ServicesForFile(ep.FilePath);
                var active = deployments.ActiveServices(loaded, ep.Requires);
                output.WriteLine(
                    $"entrypoint\t{ep.Kind}\t{ep.Method}\t{ep.Route}\t{ep.FilePath}\t{ep.Line}\t{string.Join(',', loaded)}\t{string.Join(',', active)}"
                );
            }
            return 0;
        }

        output.WriteLine($"Effects re-derived from facts: {effects.Count}");
        foreach (var group in effects.GroupBy(e => (e.Provider, e.Operation)).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{group.Key.Provider} {group.Key.Operation}: {group.Count()}");
            foreach (var e in group.Take(limit / 8 + 1))
            {
                output.WriteLine(
                    $"{Indent.L3}{ShortName(e.ResourceType)}  <- {ShortName(e.EnclosingSymbolId)}  {ShortenPath(e.FilePath)}:{e.Line}"
                );
            }
        }

        // --- Observations attached to effects (looped_effect / parallel_fanout / …, P2b) ---
        var observationGroups = effects
            .SelectMany(e => e.Observations ?? [])
            .GroupBy(o => o.Type, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (observationGroups.Count > 0)
        {
            output.WriteLine();
            output.WriteLine($"Observations on effects: {observationGroups.Sum(g => g.Count())}");
            foreach (var group in observationGroups)
            {
                output.WriteLine($"{Indent.L1}{group.Key}: {group.Count()}");
            }
        }

        // --- Page + action entry points (fact-based BFS + attribute-ref detection) ---
        // epData was loaded above (shared with the effect deriver's base-type gates).
        var epRules = FactEntryPointRuleProvider.LoadForWorkingDirectory(workingDirectory, extraRules);
        var classRules = FactEntryPointRuleProvider.LoadClassInheritanceForWorkingDirectory(workingDirectory, extraRules);
        var derivedEps = FactEntryPointDeriver.Derive(epData, epRules, classRules);

        output.WriteLine();
        output.WriteLine($"Entry points re-derived from facts: {derivedEps.Count}");
        var perKindSample = limit / 4 + 1;
        foreach (var kindGroup in derivedEps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(perKindSample))
            {
                WriteEntryPointLine(output, deployments, e.Route, e.FilePath, e.Line, e.Requires);
            }

            WriteSampleTruncationNote(output, kindGroup.Count(), perKindSample, kindGroup.Key);
        }

        // --- Classified handoff entry points (Phase 1/3): dispatcher-consumed delegates, promoted to
        //     execution origins by kind (background/timer/actor/event), with the dispatcher + the
        //     registration site. The unclassified-methodGroup residual is collapsed to a count (it was a
        //     4,503-entry firehose). Each emits an `async_handoff` observation at its registration.
        var origins = PromoteHandoffOrigins(classifiedHandoffs, derivedEps);
        output.WriteLine();
        output.WriteLine(
            $"Handoff entry points (classified): {classifiedHandoffs.Count}  "
                + $"(promoted origins after dedup: {origins.Count}; unclassified methodGroup residual: {unclassifiedHandoffCount})"
        );
        foreach (var kindGroup in classifiedHandoffs.GroupBy(h => h.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var h in kindGroup.Take(perKindSample))
            {
                var tag = deployments.IsEmpty ? "" : $"  {EntryPointRenderer.DeployTag(deployments, h.FilePath, h.Requires)}";
                output.WriteLine(
                    $"{Indent.L3}{ShortName(h.Target)}  ⤳ via {h.Dispatcher}{tag}\n{Indent.L5}registered in {ShortName(h.RegisteredIn)}  {ShortenPath(h.FilePath)}:{h.Line}  [async_handoff]"
                );
            }
            WriteSampleTruncationNote(output, kindGroup.Count(), perKindSample, kindGroup.Key ?? "");
        }

        // The headline: entry points per deployed service (the summary table). An EP counts in every service
        // whose process loads it (shared libraries fan out to many hosts — see the chip counts).
        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(derivedEps.Concat(origins).Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, output);
        }

        return 0;
    }
}
