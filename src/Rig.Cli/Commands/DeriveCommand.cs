using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
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
// rule load. Effects and entry points are matched against the same rig.rules.json the Roslyn pass uses
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
        var store = CommonOptions.Store();
        var cmd = new Command(name: "derive", description: "Re-derive effects + entry points from facts (no Roslyn).")
        {
            rules,
            limit,
            only,
            exclude,
            format,
            store,
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
                        workingDirectory: workingDirectory,
                        storeRef: pr.GetValue(store)
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
        string workingDirectory,
        string? storeRef
    )
    {
        var rules = RuleSetLoader.Load(workingDirectory, extraRules);
        await using var context = OpenReadContext(workingDirectory, storeRef);

        // Deployment attribution (opt-in: only when deployments.json sits next to .rig). Empty (no-op) when
        // the config is absent; `error` is the log sink so config problems surface.
        var deployments = await LoadDeploymentsAsync(context, workingDirectory, error);

        // Classified handoffs (background/timer/actor/event) shared by the listing, the origin-EP promotion,
        // and the TSV output — derived once. The total count yields the unclassified residual (a count, not a
        // listing), which is why this is loaded here rather than via DeriveEntryPointsAsync (which drops it).
        var handoffs = await Reads.DeriveHandoffEntryPointsAsync(context, int.MaxValue, rules.Handoff);
        var classifiedHandoffs = handoffs.Where(h => h.Dispatcher is not null).ToList();
        var unclassifiedHandoffCount = handoffs.Count - classifiedHandoffs.Count;

        // Entry-point fact data is loaded up front: its base edges also feed the effect deriver's base-type
        // gates (e.g. clientpage_proxy = declaring type derives MedDBase.Pages.ProxyBase).
        var epData = await Reads.LoadFactEntryPointDataAsync(context);

        // --- Effects (data-driven over facts) ---
        var invocations = await Reads.LoadInvocationRefsAsync(context);
        var throwRefs = await Reads.LoadThrowRefsAsync(context);
        var staticFieldWriteRefs = await Reads.LoadStaticFieldWriteRefsAsync(context);
        var staticFieldReadRefs = await Reads.LoadStaticFieldReadRefsAsync(context);
        var threadStaticCells = await Reads.LoadThreadStaticFieldIdsAsync(context);
        var effects = DeriveEffects(
            effectRules: rules.Effects,
            observationRules: rules.Observations,
            invocations: invocations,
            baseEdges: epData.BaseEdges,
            ctorRefs: epData.CtorRefs,
            throwRefs: throwRefs,
            staticFieldWriteRefs: staticFieldWriteRefs,
            staticFieldReadRefs: staticFieldReadRefs,
            deriveHazards: true, // whole-store path runs the race_window hazard post-pass
            threadStaticCells: threadStaticCells // reroute [ThreadStatic] RMW → thread_local_context
        );
        effects = ApplyEffectFilters(effects: effects, only: only, exclude: exclude); // --only / --exclude (e.g. --exclude throw)

        // Machine-readable mode: emit full-fidelity rows (full DocIDs/paths) for tooling that joins
        // effects/entry points against the call graph. `rig derive --format tsv`.
        if (string.Equals(format, "tsv", StringComparison.OrdinalIgnoreCase))
        {
            // TSV column reference (tab-separated; one row per record):
            //   effect      \t provider \t operation \t resource \t enclosing \t file \t line \t observations(csv of Type)
            //   hazard      \t type \t confidence \t reason \t cell/context \t enclosing \t file \t line \t detail
            //   entrypoint  \t kind \t method \t route \t file \t line \t services(csv) \t activeServices(csv)
            // The `effect` row's observations column keeps the comma-joined Type list (back-compat: existing
            // consumers); the `hazard` row carries the full per-hazard evidence (confidence / reason / detail)
            // the effect row can't — one row per hazard observation on an effect (see HazardKinds for the set).
            foreach (var e in effects)
            {
                var observations = string.Join(',', (e.Observations ?? []).Select(o => o.Type));
                output.WriteLine(
                    $"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{observations}"
                );
            }

            foreach (var h in HazardFindings(effects))
            {
                output.WriteLine(HazardTsvRow(h));
            }

            var tsvEps = FactEntryPointDeriver.Derive(epData, rules.EntryPoints, rules.ClassInheritance);
            // Trailing columns (comma-joined, empty when no deployments.json): `service` = the hosts that
            // LOAD the EP (link its code); `activeService` = the subset it is ACTIVE-IN after the capability
            // gate (== service when the EP is ungated). `service` is kept for back-compat; tooling that wants
            // runs-here filters on the new `activeService` column.
            foreach (var ep in tsvEps.Concat(PromoteHandoffOrigins(classifiedHandoffs, tsvEps)))
            {
                var loaded = deployments.ServicesForFile(ep.FilePath);
                var active = deployments.ActiveServices(loadedServices: loaded, requires: ep.Requires);
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

        // --- Hazards: the higher-order findings that match PATTERNS over effects (race_window / lazy_init_race /
        //     n_plus_1 / unserializable_payload — see HazardKinds). Promoted out of the generic observations
        //     block into their own section with per-type, per-confidence counts + sampled sites. ---
        WriteHazards(output, HazardFindings(effects), limit);

        // --- STRUCTURAL observations attached to effects (looped_effect / parallel_fanout /
        //     lock_held_across_effect / transaction_spans_effect, P2b) — context facts, NOT hazards. The
        //     hazard kinds are EXCLUDED here so they're never double-counted (they're in the Hazards section).
        var observationGroups = effects
            .SelectMany(e => e.Observations ?? [])
            .Where(o => !HazardKinds.IsHazard(o.Type))
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
        var derivedEps = FactEntryPointDeriver.Derive(epData, rules.EntryPoints, rules.ClassInheritance);

        output.WriteLine();
        output.WriteLine($"Entry points re-derived from facts: {derivedEps.Count}");
        var perKindSample = limit / 4 + 1;
        foreach (var kindGroup in derivedEps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(perKindSample))
            {
                WriteEntryPointLine(output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
            }

            WriteSampleTruncationNote(output, total: kindGroup.Count(), shown: perKindSample, kind: kindGroup.Key);
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
            WriteSampleTruncationNote(output, total: kindGroup.Count(), shown: perKindSample, kind: kindGroup.Key ?? "");
        }

        // The headline: entry points per deployed service (the summary table). An EP counts in every service
        // whose process loads it (shared libraries fan out to many hosts — see the chip counts).
        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(derivedEps.Concat(origins).Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, output);
        }

        return 0;
    }

    // One hazard finding flattened from an effect: the hazard observation (Type/Confidence/Reason/Context/
    // Detail) joined to the effect's location (enclosing method + file:line) so it can be rendered or emitted
    // without re-walking the effect. Context is the hazard's own context (the cell for race_window, the loop
    // identifier for n_plus_1, the unsupported type for unserializable_payload); Detail is the hazard's detail
    // (the paired-read site for race_window, the loop detail for n_plus_1, the payload args for serialization).
    internal sealed record HazardFinding(
        string Type,
        string Confidence,
        string Reason,
        string Context,
        string Detail,
        string Enclosing,
        string FilePath,
        int Line
    );

    // Flatten every HAZARD observation (HazardKinds) across the effects into one finding per (effect,
    // observation), carrying the effect's site. STRUCTURAL observations are excluded — they stay in the
    // generic Observations block. Pure + internal so the Hazards view + tsv can be unit-tested without a store.
    internal static IReadOnlyList<HazardFinding> HazardFindings(IReadOnlyList<DerivedEffect> effects)
    {
        var findings = new List<HazardFinding>();
        foreach (var e in effects)
        {
            foreach (var o in e.Observations ?? [])
            {
                if (!HazardKinds.IsHazard(o.Type))
                {
                    continue;
                }

                findings.Add(
                    new HazardFinding(
                        Type: o.Type,
                        Confidence: o.Confidence,
                        Reason: o.Reason,
                        Context: o.Context,
                        Detail: o.Detail,
                        Enclosing: e.EnclosingSymbolId ?? "",
                        FilePath: e.FilePath,
                        Line: e.Line
                    )
                );
            }
        }

        return findings;
    }

    // The tsv `hazard` row for one finding (see the column reference in RunAsync): the full per-hazard
    // evidence — type, confidence, reason, cell/context, enclosing, file, line, detail — the comma-joined
    // `effect`-row observation Type list can't carry. Pure + internal so the column contract is unit-testable.
    internal static string HazardTsvRow(HazardFinding h) =>
        $"hazard\t{h.Type}\t{h.Confidence}\t{h.Reason}\t{h.Context}\t{h.Enclosing}\t{h.FilePath}\t{h.Line}\t{h.Detail}";

    // Confidence tiers in disclosure order (high first), so a per-type breakdown reads worst-first.
    private static readonly string[] ConfidenceOrder = ["high", "medium", "low"];

    // The Hazards section: per hazard type a total + a confidence-tier breakdown, then a capped sample of
    // sites (cell/context + enclosing method + file:line + reason) with a tsv-for-all hint. Types are ordered
    // by total count desc (busiest hazard first), ties broken by type name. The tier breakdown parenthetical
    // is shown UNLESS the only tier present is "high" (the default tier — a bare "(high N)" is noise: see
    // n_plus_1 / unserializable_payload, which are always high). Pure render over a finding list — internal so
    // it can be exercised against a synthetic finding set without wiring a full DeriveCommand run.
    internal static void WriteHazards(TextWriter output, IReadOnlyList<HazardFinding> findings, int limit)
    {
        if (findings.Count == 0)
        {
            return;
        }

        output.WriteLine();
        output.WriteLine($"Hazards (pattern findings over effects): {findings.Count}");
        var perTypeSample = limit / 8 + 1;
        var byType = findings
            .GroupBy(f => f.Type, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal);
        foreach (var typeGroup in byType)
        {
            var tierCounts = typeGroup
                .GroupBy(f => f.Confidence, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            var onlyHigh = tierCounts.Count == 1 && tierCounts.ContainsKey("high");
            var breakdown = onlyHigh
                ? ""
                : " ("
                    + string.Join(
                        ", ",
                        ConfidenceOrder
                            .Where(tierCounts.ContainsKey)
                            .Select(t => $"{t} {tierCounts[t]}")
                            // Any non-standard tier (shouldn't happen) is appended after the known ones.
                            .Concat(
                                tierCounts
                                    .Keys.Where(k => !ConfidenceOrder.Contains(k, StringComparer.Ordinal))
                                    .OrderBy(k => k, StringComparer.Ordinal)
                                    .Select(k => $"{k} {tierCounts[k]}")
                            )
                    )
                    + ")";
            output.WriteLine($"{Indent.L1}{typeGroup.Key}: {typeGroup.Count()}{breakdown}");

            foreach (var f in typeGroup.Take(perTypeSample))
            {
                output.WriteLine(
                    $"{Indent.L3}{ShortName(f.Context)}  <- {ShortName(f.Enclosing)}  {ShortenPath(f.FilePath)}:{f.Line}  [{f.Reason}]"
                );
            }

            WriteSampleTruncationNote(output, total: typeGroup.Count(), shown: perTypeSample, kind: typeGroup.Key);
        }
    }
}
