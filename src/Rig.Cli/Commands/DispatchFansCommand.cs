using System.CommandLine;
using System.Globalization;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig dispatch-fans` — read-only DIAGNOSTIC: a ranked worklist of dispatch "god-seams" whose virtual/
// base/interface fan-out the call-site receiver FAILED to narrow, classified by WHY (absent receiver,
// base-typed receiver, generic type-parameter, or external/unbound binding). An un-narrowed fan is often
// a hypothesis that a rule or entry-point definition is missing the receiver/binding. Changes NO traversal
// behaviour — it only re-measures DispatchTargets over the same shaped graph reaches/tree/path walk.
internal static class DispatchFansCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var rules = CommonOptions.Rules();
        var format = CommonOptions.Format();
        var store = CommonOptions.Store();
        var top = new Option<int>("--top") { Description = "Max hub rows to show (default 30).", DefaultValueFactory = _ => 30 };
        var cause = new Option<string?>("--cause")
        {
            Description =
                "Keep only hubs with un-narrowed edges of this cause (absent-receiver | base-typed-receiver | type-parameter | external-or-unbound).",
        };
        cause.AcceptOnlyFromAmong(
            FactPathFinder.DispatchFanCauses.AbsentReceiver,
            FactPathFinder.DispatchFanCauses.BaseTypedReceiver,
            FactPathFinder.DispatchFanCauses.TypeParameter,
            FactPathFinder.DispatchFanCauses.ExternalOrUnbound
        );
        var cmd = new Command(
            name: "dispatch-fans",
            description: "Diagnostic: dispatch hubs whose receiver did NOT narrow the CHA fan-out, ranked + classified."
        )
        {
            rules,
            format,
            store,
            top,
            cause,
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
                            Top: pr.GetValue(top),
                            Cause: pr.GetValue(cause)
                        ),
                        new CommandIo(Output: output, Error: error, WorkingDirectory: workingDirectory, StoreRef: pr.GetValue(store))
                    )
            )
        );
        return cmd;
    }

    private sealed record Options(IReadOnlyList<string> ExtraRules, string? Format, int Top, string? Cause);

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var rules = RuleSetLoader.Load(io.WorkingDirectory, opts.ExtraRules);

        await using var context = OpenReadContext(io.WorkingDirectory, io.StoreRef);

        // The full shaped graph (every call edge + mined dispatch facts), same load `rig derive` uses, so
        // the measured fan matches what the rest of the pipeline sees. This is a whole-graph diagnostic, not
        // a pattern-bounded traversal, so it does NOT go through LoadShapedTraversalGraphAsync.
        var graph = await Reads.LoadShapedGraphAsync(context: context, rules: rules);

        var rows = FactPathFinder.DispatchFanReport(graph);
        if (opts.Cause is not null)
        {
            rows = rows.Where(r => HasCause(r, opts.Cause)).ToList();
        }

        if (rows.Count == 0)
        {
            if (!tsv)
            {
                io.Output.WriteLine(
                    opts.Cause is null ? "No un-narrowed dispatch fans found." : $"No un-narrowed dispatch fans with cause '{opts.Cause}'."
                );
            }

            return 0;
        }

        if (tsv)
        {
            // One row per hub: residualFan, incomingEdges, actionable|irreducible, cause-breakdown, hub.
            // The cause-breakdown is a compact comma-joined `cause=count` of the non-zero causes.
            foreach (var r in rows.Take(opts.Top))
            {
                io.Output.WriteLine(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "{0}\t{1}\t{2}\t{3}\t{4}",
                        r.ResidualFan,
                        r.IncomingEdges,
                        r.Actionable ? "actionable" : "irreducible",
                        CauseBreakdown(r),
                        r.Hub
                    )
                );
            }

            return 0;
        }

        var actionable = rows.Count(r => r.Actionable);
        io.Output.WriteLine(
            $"Un-narrowed dispatch fans: {rows.Count} hub(s) ({actionable} actionable) — ranked by residualFan × incomingEdges."
        );
        io.Output.WriteLine(
            "(actionable = some un-narrowed edge has an absent or type-parameter receiver — likely a missing rule/EP def to capture the receiver/binding.)"
        );
        foreach (var r in rows.Take(opts.Top))
        {
            io.Output.WriteLine(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "  [{0}] fan {1} × {2} edge(s) = {3}  {4}  ({5})",
                    r.Actionable ? "actionable" : "irreducible",
                    r.ResidualFan,
                    r.IncomingEdges,
                    r.Rank,
                    ShortName(r.Hub),
                    CauseBreakdown(r)
                )
            );
        }

        if (rows.Count > opts.Top)
        {
            io.Output.WriteLine($"  … +{rows.Count - opts.Top} more hub(s) (raise --top, or --format tsv for all)");
        }

        return 0;
    }

    private static bool HasCause(FactPathFinder.DispatchFanRow r, string cause) =>
        cause switch
        {
            FactPathFinder.DispatchFanCauses.AbsentReceiver => r.AbsentReceiver > 0,
            FactPathFinder.DispatchFanCauses.BaseTypedReceiver => r.BaseTypedReceiver > 0,
            FactPathFinder.DispatchFanCauses.TypeParameter => r.TypeParameter > 0,
            FactPathFinder.DispatchFanCauses.ExternalOrUnbound => r.ExternalOrUnbound > 0,
            _ => false,
        };

    // Compact `cause=count` of the non-zero causes (e.g. "absent-receiver=12,external-or-unbound=3").
    private static string CauseBreakdown(FactPathFinder.DispatchFanRow r)
    {
        var parts = new List<string>(4);
        if (r.AbsentReceiver > 0)
        {
            parts.Add(
                string.Format(CultureInfo.InvariantCulture, "{0}={1}", FactPathFinder.DispatchFanCauses.AbsentReceiver, r.AbsentReceiver)
            );
        }

        if (r.TypeParameter > 0)
        {
            parts.Add(
                string.Format(CultureInfo.InvariantCulture, "{0}={1}", FactPathFinder.DispatchFanCauses.TypeParameter, r.TypeParameter)
            );
        }

        if (r.BaseTypedReceiver > 0)
        {
            parts.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}={1}",
                    FactPathFinder.DispatchFanCauses.BaseTypedReceiver,
                    r.BaseTypedReceiver
                )
            );
        }

        if (r.ExternalOrUnbound > 0)
        {
            parts.Add(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}={1}",
                    FactPathFinder.DispatchFanCauses.ExternalOrUnbound,
                    r.ExternalOrUnbound
                )
            );
        }

        return string.Join(",", parts);
    }
}
