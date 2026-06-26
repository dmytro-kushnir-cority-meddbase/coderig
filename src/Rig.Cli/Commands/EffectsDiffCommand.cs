using System.CommandLine;
using System.Diagnostics;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Cli.Telemetry;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig effects-diff <a> <b>` — generic effect-set diff between two entry points. Computes each EP's
// forward-reachable effect resource-keys (optionally filtered by `--only provider:op`) and reports the
// symmetric difference: resources one reaches that the other doesn't. Purely mechanical — it has no
// opinion about what a difference MEANS.
//
// "write-set divergence" (a UI/save path vs an import/API path writing different tables — an incident-born
// consistency check) is one USAGE: `rig effects-diff Save Import --only llblgen:write,bulk_write,delete`.
// The operator/agent supplies the domain interpretation ("these two are the same logical op; this gap is a
// bug"); the tool just diffs. Wraps FactEffectSetDiffDeriver (the pure deriver).
internal static class EffectsDiffCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var a = CommonOptions.Pattern(name: "a", description: "First entry-point method pattern.");
        var b = CommonOptions.Pattern(name: "b", description: "Second entry-point method pattern.");
        var only = new Option<string[]?>("--only")
        {
            Description =
                "Effect provider[:operation] to include (repeatable). Default: ALL effects. "
                + "Write-set divergence = --only llblgen:write --only llblgen:bulk_write --only llblgen:delete.",
            CustomParser = r => r.Tokens.Select(t => t.Value).ToArray(),
            AllowMultipleArgumentsPerToken = false,
        };
        var label = new Option<string?>("--label") { Description = "Optional label for the pair in output." };
        var format = CommonOptions.Format();
        var time = CommonOptions.Time();
        var store = CommonOptions.Store();
        var cmd = new Command(
            name: "effects-diff",
            description: "Diff the forward-reachable effect-sets of two entry points (symmetric difference, optionally filtered)."
        )
        {
            a,
            b,
            only,
            label,
            format,
            time,
            store,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        new Options(
                            APattern: pr.GetValue(a)!,
                            BPattern: pr.GetValue(b)!,
                            Only: pr.GetValue(only),
                            Label: pr.GetValue(label),
                            Format: pr.GetValue(format),
                            Time: pr.GetValue(time)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, pr.GetValue(store)))
                    )
            )
        );
        return cmd;
    }

    private sealed record Options(string APattern, string BPattern, string[]? Only, string? Label, string? Format, bool Time);

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var label = opts.Label ?? "";

        using var timing = QueryTiming.Start(opts.Time, io.TextOutput.Error);

        var rules = RuleSetLoader.Load(io.WorkspaceLocation.WorkingDirectory);

        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation);

        var graphWatch = Stopwatch.StartNew();
        var graph = await Reads.LoadShapedGraphAsync(context: context, rules: rules);
        graphWatch.Stop();
        timing.Record("graph load", graphWatch.Elapsed);

        // Resolve each pattern to exactly one node id (substring, OrdinalIgnoreCase). 0 = no symbol; >1 =
        // ambiguous (list candidates + error — never silently pick).
        var aId = ResolvePattern(pattern: opts.APattern, graph: graph, side: "a", io: io, tsv: tsv);
        if (aId is null)
        {
            return 1;
        }

        var bId = ResolvePattern(pattern: opts.BPattern, graph: graph, side: "b", io: io, tsv: tsv);
        if (bId is null)
        {
            return 1;
        }

        var traversalWatch = Stopwatch.StartNew();
        // --only filter. EMPTY = match every effect (the generic default). Resource normalization collapses
        // ORM type variants (Entity/Collection/DAO) to one logical key; the suffix list is a sensible
        // LLBLGen-friendly default (harmless elsewhere) — a future `--strip-suffix` knob can override it.
        var filter = ParseFilter(opts.Only);
        var normalize = new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"]);
        var spec = new EffectSetDiffSpec(
            Pairs: [new EffectSetDiffPair(Label: label, AId: aId, BId: bId)],
            Filter: filter,
            Normalize: normalize
        );

        // Whole-store effect derivation (same source `rig derive` uses) — unfiltered, so nothing is hidden
        // before the per-EP filter is applied.
        var effects = await DeriveHazardEffectsAsync(context: context, rules: rules);

        var findings = FactEffectSetDiffDeriver.Derive(graph: graph, effects: effects, spec: spec);
        traversalWatch.Stop();
        timing.Record("traversal", traversalWatch.Elapsed);

        var renderWatch = Stopwatch.StartNew();
        if (tsv)
        {
            // columns: label, category, resource_key, side, present_ep, absent_ep
            // category = the present EP's provider:op(s) for this resource (comma-joined) — labels the row's
            // KIND (e.g. permission:assert = a guard; llblgen:write = a durable write).
            foreach (var f in findings)
            {
                io.TextOutput.Output.WriteLine(
                    $"{f.Label}\t{string.Join(",", f.Categories)}\t{f.ResourceKey}\t{f.Direction}\t{f.PresentEpId}\t{f.AbsentEpId}"
                );
            }

            renderWatch.Stop();
            timing.Record("render", renderWatch.Elapsed);

            return 0;
        }

        if (findings.Count == 0)
        {
            io.TextOutput.Output.WriteLine($"No effect-set difference between '{opts.APattern}' and '{opts.BPattern}'.");

            renderWatch.Stop();
            timing.Record("render", renderWatch.Elapsed);

            return 0;
        }

        io.TextOutput.Output.WriteLine(
            $"Effect-set difference: {findings.Count} resource(s) differ between A='{opts.APattern}' and B='{opts.BPattern}'."
        );
        foreach (var f in findings)
        {
            var side = f.Direction == EffectDiffSide.AOnly ? "A-only" : "B-only";
            var category = f.Categories.Count > 0 ? string.Join(",", f.Categories) : "?";
            io.TextOutput.Output.WriteLine(
                $"{Indent.L1}{category}  {f.ResourceKey}  [{side}]  reached by: {ShortName(f.PresentEpId)}  not by: {ShortName(f.AbsentEpId)}"
            );
        }

        renderWatch.Stop();
        timing.Record("render", renderWatch.Elapsed);

        return 0;
    }

    // Resolve a pattern to exactly one node DocID. Returns null + writes a diagnostic on 0 or >1 matches.
    // >1 lists the candidates and tells the user to narrow it (never silently pick one).
    private static string? ResolvePattern(string pattern, FactGraphData graph, string side, CommandIo io, bool tsv)
    {
        var matches = graph
            .Methods.Select(m => m.SymbolId)
            .Where(id => id.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            var line = $"No symbol matches '{pattern}'.";
            (tsv ? io.TextOutput.Error : io.TextOutput.Output).WriteLine(line);
            return null;
        }

        if (matches.Count > 1)
        {
            var line = $"Ambiguous: '{pattern}' ({side}) matched {matches.Count} nodes — narrow it.";
            if (tsv)
            {
                io.TextOutput.Error.WriteLine(line);
            }
            else
            {
                io.TextOutput.Output.WriteLine(line);
                foreach (var candidate in matches.Take(10))
                {
                    io.TextOutput.Output.WriteLine($"{Indent.L1}{candidate}");
                }

                if (matches.Count > 10)
                {
                    io.TextOutput.Output.WriteLine($"{Indent.L1}… and {matches.Count - 10} more");
                }
            }

            return null;
        }

        return matches[0];
    }

    // Parse --only tokens into effect predicates. EMPTY (no --only) = empty list = match ALL effects.
    // A "provider:operation" token pins both; a bare "provider" token matches any operation of that provider.
    private static IReadOnlyList<EffectPredicate> ParseFilter(string[]? tokens)
    {
        if (tokens is null || tokens.Length == 0)
        {
            return [];
        }

        return tokens
            .Select(t =>
            {
                var colon = t.IndexOf(':');
                return colon < 0
                    ? new EffectPredicate(Provider: t, Operation: null)
                    : new EffectPredicate(Provider: t[..colon], Operation: t[(colon + 1)..]);
            })
            .ToList();
    }
}
