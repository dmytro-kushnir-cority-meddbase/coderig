using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig write-set-divergence <primary> <secondary>` — incident-born structural write-set divergence check.
// Compares the reachable write-sets of two entry points performing the "same" logical operation on an entity
// and surfaces tables written by one but not the other (the secondary path silently skips junction/link/
// event/denormalized rows the primary maintains).
//
// Design: incident-born detectors that require per-use domain knowledge (which two EPs are "the same op")
// do not belong as config-driven derive hazards — they belong as explicit CLI commands that take the pairing
// as args. This command wraps FactWriteSetDivergenceDeriver (the pure deriver, unchanged) with the CLI
// surface: pattern→node resolution, defaults, formatting.
internal static class WriteSetDivergenceCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        var primary = CommonOptions.Pattern(
            name: "primary",
            description: "Canonical / UI entry-point method pattern (e.g. 'SaveInvoice')."
        );
        var secondary = CommonOptions.Pattern(
            name: "secondary",
            description: "Import / API entry-point method pattern (e.g. 'ImportInvoice')."
        );
        var entity = new Option<string?>("--entity") { Description = "Label for the entity pair in output (default: \"pair\")." };
        var write = new Option<string[]?>("--write")
        {
            Description =
                "Effect provider:operation that counts as a write (repeatable; default: llblgen:write llblgen:bulk_write llblgen:delete).",
            CustomParser = r => r.Tokens.Select(t => t.Value).ToArray(),
            AllowMultipleArgumentsPerToken = false,
        };
        var format = CommonOptions.Format();
        var store = CommonOptions.Store();
        var cmd = new Command(
            name: "write-set-divergence",
            description: "Compare the reachable write-sets of two entry points and report diverging tables."
        )
        {
            primary,
            secondary,
            entity,
            write,
            format,
            store,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        new Options(
                            PrimaryPattern: pr.GetValue(primary)!,
                            SecondaryPattern: pr.GetValue(secondary)!,
                            EntityLabel: pr.GetValue(entity),
                            WriteEffects: pr.GetValue(write),
                            Format: pr.GetValue(format)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, pr.GetValue(store)))
                    )
            )
        );
        return cmd;
    }

    private sealed record Options(
        string PrimaryPattern,
        string SecondaryPattern,
        string? EntityLabel,
        string[]? WriteEffects,
        string? Format
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var entityLabel = string.IsNullOrWhiteSpace(opts.EntityLabel) ? "pair" : opts.EntityLabel;

        var rules = RuleSetLoader.Load(io.WorkspaceLocation.WorkingDirectory);

        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation);
        var graph = await Reads.LoadShapedGraphAsync(context: context, rules: rules);

        // Resolve each pattern to exactly one node id — same substring-OrdinalIgnoreCase semantics
        // DeriveCommand used for the rule-declared pairs. >1 match = ambiguous; 0 match = no symbol.
        var primaryId = ResolvePattern(pattern: opts.PrimaryPattern, graph: graph, label: "primary", io: io, tsv: tsv);
        if (primaryId is null)
        {
            return 1;
        }

        var secondaryId = ResolvePattern(pattern: opts.SecondaryPattern, graph: graph, label: "secondary", io: io, tsv: tsv);
        if (secondaryId is null)
        {
            return 1;
        }

        // Parse --write predicates. Default: the three LLBLGen write operations the DeriveCommand wired.
        var writePredicates = ParseWritePredicates(opts.WriteEffects);

        var normalize = new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"]);
        var spec = new WriteSetDivergenceSpec(
            Pairs: [new WriteSetDivergencePair(EntityLabel: entityLabel, PrimaryEnclosingId: primaryId, SecondaryEnclosingId: secondaryId)],
            WritePredicates: writePredicates,
            WriteNormalize: normalize
        );

        // Derive effects for the write-set comparison — mirrors DeriveCommand's unfilteredEffects.
        // WSD needs the pre-filter set so no writes are hidden. DeriveHazardEffectsAsync is the whole-store
        // derivation (invocations + field accesses + hazard post-pass), same as what `rig derive` uses.
        var derivedEffects = await DeriveHazardEffectsAsync(context: context, rules: rules);

        var findings = FactWriteSetDivergenceDeriver.Derive(graph: graph, effects: derivedEffects, spec: spec);

        if (tsv)
        {
            // TSV columns: entity, table, direction, present_ep, absent_ep
            foreach (var f in findings)
            {
                io.TextOutput.Output.WriteLine($"{f.EntityLabel}\t{f.ResourceKey}\t{f.Direction}\t{f.PresentEpId}\t{f.AbsentEpId}");
            }

            return 0;
        }

        if (findings.Count == 0)
        {
            io.TextOutput.Output.WriteLine(
                $"No write-set divergence found between '{opts.PrimaryPattern}' and '{opts.SecondaryPattern}' for entity '{entityLabel}'."
            );
            return 0;
        }

        io.TextOutput.Output.WriteLine(
            $"Write-set divergence: {findings.Count} table(s) differ between '{opts.PrimaryPattern}' and '{opts.SecondaryPattern}' (entity: {entityLabel})"
        );
        foreach (var f in findings)
        {
            var directionLabel = f.Direction == WriteSetDirection.PrimaryOnly ? "primary-only" : "secondary-only";
            var presentShort = ShortName(f.PresentEpId);
            var absentShort = ShortName(f.AbsentEpId);
            io.TextOutput.Output.WriteLine(
                $"{Indent.L1}{f.ResourceKey}  [{directionLabel}]  written by: {presentShort}  missing from: {absentShort}"
            );
        }

        return 0;
    }

    // Resolve a pattern to exactly one node DocID from the shaped graph. Returns null and writes an
    // error message when 0 or >1 nodes match. Mirrors the resolution DeriveCommand used for rule pairs,
    // with improved error messages (>1 now lists the candidates and tells the user to narrow the pattern
    // instead of silently skipping, which was the old derive-hazard behavior).
    private static string? ResolvePattern(string pattern, FactGraphData graph, string label, CommandIo io, bool tsv)
    {
        var matches = graph
            .Methods.Select(m => m.SymbolId)
            .Where(id => id.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (matches.Count == 0)
        {
            var line = $"No symbol matches '{pattern}'.";
            if (tsv)
            {
                io.TextOutput.Error.WriteLine(line);
            }
            else
            {
                io.TextOutput.Output.WriteLine(line);
            }

            return null;
        }

        if (matches.Count > 1)
        {
            var line = $"Ambiguous: '{pattern}' matched {matches.Count} nodes — narrow it.";
            if (tsv)
            {
                io.TextOutput.Error.WriteLine(line);
            }
            else
            {
                io.TextOutput.Output.WriteLine(line);
                io.TextOutput.Output.WriteLine($"  ({label} candidates)");
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

    // Default write predicates: the three LLBLGen write operations used by the old DeriveCommand wiring.
    // --write overrides by parsing "provider:operation" or "provider" tokens (operation=null means any op).
    private static IReadOnlyList<EffectPredicate> ParseWritePredicates(string[]? tokens)
    {
        if (tokens is null || tokens.Length == 0)
        {
            return
            [
                new EffectPredicate(Provider: "llblgen", Operation: "write"),
                new EffectPredicate(Provider: "llblgen", Operation: "bulk_write"),
                new EffectPredicate(Provider: "llblgen", Operation: "delete"),
            ];
        }

        return tokens
            .Select(t =>
            {
                var colon = t.IndexOf(':');
                if (colon < 0)
                {
                    return new EffectPredicate(Provider: t, Operation: null);
                }

                return new EffectPredicate(Provider: t[..colon], Operation: t[(colon + 1)..]);
            })
            .ToList();
    }
}
