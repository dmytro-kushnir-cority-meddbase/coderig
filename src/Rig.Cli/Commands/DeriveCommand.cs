using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using static Rig.Cli.Caching.QueryCacheKeys;
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
                        new Options(
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Limit: pr.GetValue(limit),
                            Only: CommonOptions.FilterSet(pr.GetValue(only)),
                            Exclude: CommonOptions.FilterSet(pr.GetValue(exclude)),
                            Format: pr.GetValue(format)
                        ),
                        new CommandIo(Output: output, Error: error, WorkingDirectory: workingDirectory, StoreRef: pr.GetValue(store))
                    )
            )
        );
        return cmd;
    }

    private sealed record Options(
        IReadOnlyList<string> ExtraRules,
        int Limit,
        HashSet<string> Only,
        HashSet<string> Exclude,
        string? Format
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        // #4: capture the resolved rule paths from the load, so the fingerprint below reuses them instead of
        // re-running the cascade merge (RulesFingerprint.ComputeFromPaths) just to re-discover the same paths.
        var rules = RuleSetLoader.Load(
            workingDirectory: io.WorkingDirectory,
            extraRules: opts.ExtraRules,
            loadedPaths: out var loadedRulePaths
        );
        // F7: use the out-param overload so the resolved store dir is available for the StoreKey computation
        // below without a second ResolveReadStoreDir call (io:read ×7).
        await using var context = OpenReadContext(workingDirectory: io.WorkingDirectory, storeRef: io.StoreRef, storeDir: out var rigDir);

        // Deployment attribution (opt-in: only when deployments.json sits next to .rig). Empty (no-op) when
        // the config is absent; `error` is the log sink so config problems surface.
        var deployments = await LoadDeploymentsAsync(context, io.WorkingDirectory, io.Error);

        // Shaped graph: built once here and reused for both the handoff-EP classifier (F1 fix: avoids the
        // double-load in DeriveHandoffEntryPointsAsync's fallback path) and the event-cycle deriver below.
        var shapedGraph = await Reads.LoadShapedGraphAsync(context: context, rules: rules);

        // Classified handoffs (background/timer/actor/event) shared by the listing, the origin-EP promotion,
        // and the TSV output — derived once. The total count yields the unclassified residual (a count, not a
        // listing), which is why this is loaded here rather than via DeriveEntryPointsAsync (which drops it).
        var handoffs = await Reads.DeriveHandoffEntryPointsAsync(
            context: context,
            limit: int.MaxValue,
            handoffRules: rules.Handoff,
            graph: shapedGraph
        );
        var classifiedHandoffs = handoffs.Where(h => h.Dispatcher is not null).ToList();
        var unclassifiedHandoffCount = handoffs.Count - classifiedHandoffs.Count;

        // Entry-point fact data is loaded up front: its base edges also feed the effect deriver's base-type
        // gates (e.g. clientpage_proxy = declaring type derives MedDBase.Pages.ProxyBase).
        var epData = await Reads.LoadFactEntryPointDataAsync(context);

        // --- Effects (whole-store, hazard-augmented): the field-fed shared_state arms + the race_window/
        //     dual_write/thread_local_context post-pass, cached store+rules-keyed and SHARED with `tree
        //     --hazards` (an effect is a per-method fact, EP- and mode-independent). A reindex or rule edit
        //     misses the cache and recomputes, so hazards stay query-side data. ---
        // rigDir resolved above via the out-param OpenReadContext overload (F7).
        var storeKey = StoreKey(Path.Combine(rigDir, StoreLayout.DbFileName));
        var rulesHash = RulesFingerprint.ComputeFromPaths(loadedRulePaths); // #4: reuse the paths Load resolved.
        var effects = await LoadOrDeriveHazardEffectsAsync(
            context: context,
            rigDirectory: rigDir,
            storeKey: storeKey,
            rulesHash: rulesHash,
            rules: rules,
            useCache: true,
            epData: epData // #1b: reuse the EP data loaded above; skip the redundant load on a cache miss.
        );
        effects = ApplyEffectFilters(effects: effects, only: opts.Only, exclude: opts.Exclude); // --only / --exclude (e.g. --exclude throw)

        // --- event_cycle (the GRAPH-tier hazard): a feedback cycle that closes through ≥1 publish→consumer
        //     DELIVERY edge (event raise / actor tell). Unlike every other hazard it is NOT an effect-attached
        //     observation, so it is derived here over the delivery-edge-bearing graph and added as a SECOND
        //     hazard source — NOT folded into HazardFindings(effects), which is pure-over-effects and shared
        //     with impact. The graph is built IN-MEMORY via LoadShapedGraphAsync (handoff-classified load →
        //     ShapeGraph → MarkEventSubscriptionHandoffs → AddDeliveryEdges) so the cycles it finds are
        //     exactly the cycles the materialized call_edges carry. ---
        var cycleFindings = EventCycleFindings(FactCycleDeriver.DeriveEventCycles(shapedGraph));

        // Both hazard sources unioned: the over-effects pattern findings + the graph-tier event_cycle findings.
        // Fed to BOTH the tsv `hazard` emission AND the rendered Hazards section so neither view misses a source.
        var allHazards = new List<HazardFinding>(HazardFindings(effects));
        allHazards.AddRange(cycleFindings);

        // Machine-readable mode: emit full-fidelity rows (full DocIDs/paths) for tooling that joins
        // effects/entry points against the call graph. `rig derive --format tsv`.
        if (tsv)
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
                io.Output.WriteLine(
                    $"effect\t{e.Provider}\t{e.Operation}\t{e.ResourceType}\t{e.EnclosingSymbolId}\t{e.FilePath}\t{e.Line}\t{observations}"
                );
            }

            foreach (var h in allHazards)
            {
                io.Output.WriteLine(HazardTsvRow(h));
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
                io.Output.WriteLine(
                    $"entrypoint\t{ep.Kind}\t{ep.Method}\t{ep.Route}\t{ep.FilePath}\t{ep.Line}\t{string.Join(',', loaded)}\t{string.Join(',', active)}"
                );
            }
            return 0;
        }

        io.Output.WriteLine($"Effects re-derived from facts: {effects.Count}");
        foreach (var group in effects.GroupBy(e => (e.Provider, e.Operation)).OrderByDescending(g => g.Count()))
        {
            io.Output.WriteLine($"{Indent.L1}{group.Key.Provider} {group.Key.Operation}: {group.Count()}");
            foreach (var e in group.Take(opts.Limit / 8 + 1))
            {
                io.Output.WriteLine(
                    $"{Indent.L3}{ShortName(e.ResourceType)}  <- {ShortName(e.EnclosingSymbolId)}  {ShortenPath(e.FilePath)}:{e.Line}"
                );
            }
        }

        // --- Hazards: the higher-order findings that match PATTERNS over effects (race_window / lazy_init_race /
        //     n_plus_1 / unserializable_payload — see HazardKinds). Promoted out of the generic observations
        //     block into their own section with per-type, per-confidence counts + sampled sites. ---
        WriteHazards(io.Output, allHazards, opts.Limit);

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
            io.Output.WriteLine();
            io.Output.WriteLine($"Observations on effects: {observationGroups.Sum(g => g.Count())}");
            foreach (var group in observationGroups)
            {
                io.Output.WriteLine($"{Indent.L1}{group.Key}: {group.Count()}");
            }
        }

        // --- Page + action entry points (fact-based BFS + attribute-ref detection) ---
        // epData was loaded above (shared with the effect deriver's base-type gates).
        var derivedEps = FactEntryPointDeriver.Derive(epData, rules.EntryPoints, rules.ClassInheritance);

        io.Output.WriteLine();
        io.Output.WriteLine($"Entry points re-derived from facts: {derivedEps.Count}");
        var perKindSample = opts.Limit / 4 + 1;
        foreach (var kindGroup in derivedEps.GroupBy(e => e.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            io.Output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var e in kindGroup.Take(perKindSample))
            {
                WriteEntryPointLine(io.Output, deployments, route: e.Route, filePath: e.FilePath, line: e.Line, requires: e.Requires);
            }

            WriteSampleTruncationNote(io.Output, total: kindGroup.Count(), shown: perKindSample, kind: kindGroup.Key);
        }

        // --- Classified handoff entry points (Phase 1/3): dispatcher-consumed delegates, promoted to
        //     execution origins by kind (background/timer/actor/event), with the dispatcher + the
        //     registration site. The unclassified-methodGroup residual is collapsed to a count (it was a
        //     4,503-entry firehose). Each emits an `async_handoff` observation at its registration.
        var origins = PromoteHandoffOrigins(classifiedHandoffs, derivedEps);
        io.Output.WriteLine();
        io.Output.WriteLine(
            $"Handoff entry points (classified): {classifiedHandoffs.Count}  "
                + $"(promoted origins after dedup: {origins.Count}; unclassified methodGroup residual: {unclassifiedHandoffCount})"
        );
        foreach (var kindGroup in classifiedHandoffs.GroupBy(h => h.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            io.Output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var h in kindGroup.Take(perKindSample))
            {
                var tag = deployments.IsEmpty ? "" : $"  {EntryPointRenderer.DeployTag(deployments, h.FilePath, h.Requires)}";
                io.Output.WriteLine(
                    $"{Indent.L3}{ShortName(h.Target)}  ⤳ via {h.Dispatcher}{tag}\n{Indent.L5}registered in {ShortName(h.RegisteredIn)}  {ShortenPath(h.FilePath)}:{h.Line}  [async_handoff]"
                );
            }
            WriteSampleTruncationNote(io.Output, total: kindGroup.Count(), shown: perKindSample, kind: kindGroup.Key ?? "");
        }

        // The headline: entry points per deployed service (the summary table). An EP counts in every service
        // whose process loads it (shared libraries fan out to many hosts — see the chip counts).
        if (!deployments.IsEmpty)
        {
            WriteServiceSummary(derivedEps.Concat(origins).Select(e => (e.Kind, (string?)e.FilePath, e.Requires)), deployments, io.Output);
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

    // Map each graph-tier event_cycle into a HazardFinding so it flows through the SAME Hazards view + tsv
    // split as the over-effects findings. event_cycle is NOT effect-attached, so it has no single owning
    // effect: the REPRESENTATIVE site is the first delivery edge's Caller / FilePath / Line (the raise that
    // closes the loop — the most actionable anchor). Context is the cycle size; Detail enumerates every
    // delivery edge with its dispatcher and site so the full evidence survives into the tsv `hazard` row.
    // Pure + internal so the mapping is unit-testable without a store.
    internal static IReadOnlyList<HazardFinding> EventCycleFindings(IReadOnlyList<EventCycle> cycles)
    {
        var findings = new List<HazardFinding>(cycles.Count);
        foreach (var cycle in cycles)
        {
            var representative = cycle.DeliveryEdges[0];
            var detail = string.Join(
                ", ",
                cycle.DeliveryEdges.Select(e => $"{e.Caller}->{e.Callee}@{e.FilePath}:{e.Line}[{e.HandoffDispatcher}]")
            );
            findings.Add(
                new HazardFinding(
                    Type: FactCycleDeriver.EventCycleType,
                    Confidence: cycle.Confidence,
                    Reason: "feedback_cycle_over_delivery_edges",
                    Context: $"{cycle.Members.Count} methods",
                    Detail: detail,
                    Enclosing: representative.Caller,
                    FilePath: representative.FilePath,
                    Line: representative.Line
                )
            );
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
        output.WriteLine($"Hazards (pattern findings): {findings.Count}");
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
