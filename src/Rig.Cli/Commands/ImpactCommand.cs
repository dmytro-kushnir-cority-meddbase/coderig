using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.Caching;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.Impact;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Caching.QueryCacheKeys;
using static Rig.Cli.Effects.EffectDerivation;
using static Rig.Cli.EntryPoints.EntryPointContext;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.EntryPointListRenderer;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// `rig impact --base <store> --head <store>` — a PURE two-store derived-facts diff. Both sides are REQUIRED
// indexed per-commit stores (a sha / short-sha / store-id matching an indexed `.rig/<id>/` store — NOT a git
// ref, NOT a working tree). It reports, per entry point, what the change DID, derived entirely from the two
// immutable stores:
//   (1) the ENTRY-POINT SET diff — EPs added/removed vs the base, paired on (Kind, Route);
//   (2) the BEHAVIORAL per-EP diff — entry points whose reachable EFFECT set changed (the high-signal handful);
//   (3) the STRUCTURAL per-EP diff — entry points whose reachable TREE changed (demoted to a cause-classified
//       one-liner by default; --structural expands it).
//
// There is NO git diff and NO speculative blast radius: the old git-working-tree seed (changed methods →
// reverse/forward reach) fed only the now-removed behavioral-delta section and the removed --reach blast
// radius. Every signal here is store-vs-store, so the output is the PROVEN diff between two indexed commits.
//
// This command is PURE ORCHESTRATION over the shipped engine — it adds no graph code. It runs `derive`'s
// DeriveEntryPoints/DeriveEffects on each store and diffs the per-EP forward-reach footprints. The compute
// half (the store-vs-store diff + its domain records) lives in ImpactEngine / ImpactModel (Rig.Cli.Impact);
// this file is the command wiring + rendering.
internal static class ImpactCommand
{
    internal static Command Build(TextWriter output, TextWriter error, string workingDirectory)
    {
        // Both sides are INDEXED STORE REFS (sha / short-sha / store-id matching a per-commit `.rig/<id>/`
        // store), resolved the same way every read command's --store is. --base-store / --head-store are
        // aliases for symmetry; they take the same store-ref form (the historical --base-store path/dir form
        // is gone — all four names resolve through ResolveReadStoreDir).
        var @base = new Option<string?>("--base", "--base-store")
        {
            Description = "BASE (before) side: an indexed commit store ref (sha / short-sha / store-id). Required.",
        };
        var head = new Option<string?>("--head", "--head-store")
        {
            Description = "HEAD (after) side: an indexed commit store ref (sha / short-sha / store-id). Required.",
        };
        var async = CommonOptions.Async();
        var includeDelivery = CommonOptions.IncludeDelivery();
        var rules = CommonOptions.Rules();
        var format = CommonOptions.Format();
        var limit = CommonOptions.Limit();
        var noCache = CommonOptions.NoCache();
        var noGate = CommonOptions.NoGate();
        var structural = new Option<bool>("--structural")
        {
            Description =
                "Also list every entry point whose reachable TREE changed — including the (usually large) set affected "
                + "only by a data-shape ripple (a record gaining a field changes every reaching EP's reach without "
                + "changing its behavior). Off by default: the default output lists EPs whose EFFECT set changed (the "
                + "behavioral signal) plus a one-line structural-only summary. This expands that summary to the full list.",
        };
        // CI guardrail for behavior-preserving MRs (refactors / framework migrations): exit non-zero if ANY
        // entry point's reachable EFFECT set changed (the per-EP behavioral delta — the same count the header
        // reports as "N with a changed behavior"). Structural-only reachable-tree ripple does NOT trip it (a
        // data-shape change with no new/lost effect is exactly what a refactor is allowed to do). The diff is
        // formatting/rename-immune, so this gates on behavior, not text.
        var expectNoEffectChange = new Option<bool>("--expect-no-effect-change")
        {
            Description = "CI gate: exit 1 if any entry point's reachable effect set changed (for behavior-preserving MRs).",
        };
        var cmd = new Command(
            name: "impact",
            description: "Two-store diff: the entry-point + per-EP effect/reach changes between two indexed commits (--base <store> --head <store>)."
        )
        {
            @base,
            head,
            async,
            includeDelivery,
            rules,
            format,
            limit,
            noCache,
            noGate,
            structural,
            expectNoEffectChange,
        };
        // Both stores are mandatory — impact is a pure two-store diff, there is no working-tree/git fallback
        // and no LATEST default. Error clearly (before opening anything) if either ref is missing.
        cmd.Validators.Add(result =>
        {
            var hasBase = !string.IsNullOrWhiteSpace(result.GetValue(@base));
            var hasHead = !string.IsNullOrWhiteSpace(result.GetValue(head));
            if (!hasBase || !hasHead)
            {
                result.AddError(
                    "rig impact requires both --base <store> and --head <store> (indexed commit store refs: sha / short-sha / store-id)."
                );
            }
        });
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                () =>
                    RunAsync(
                        new Options(
                            BaseRef: pr.GetValue(@base)!,
                            HeadRef: pr.GetValue(head)!,
                            Async: pr.GetValue(async),
                            IncludeDelivery: pr.GetValue(includeDelivery),
                            ExtraRules: CommonOptions.RulesOf(pr.GetValue(rules)),
                            Format: pr.GetValue(format),
                            Limit: pr.GetValue(limit),
                            NoCache: pr.GetValue(noCache),
                            Gate: !pr.GetValue(noGate),
                            Structural: pr.GetValue(structural),
                            ExpectNoEffectChange: pr.GetValue(expectNoEffectChange)
                        ),
                        new CommandIo(new TextOutput(output, error), new WorkspaceLocation(workingDirectory, null))
                    )
            )
        );
        return cmd;
    }

    // Bound option values for `rig impact`. Raw user inputs kept as parsed strings/values; derived locals
    // (tsv, max, mode) live at the top of RunAsync so cross-flag derivation stays in one place.
    private sealed record Options(
        string BaseRef,
        string HeadRef,
        bool Async,
        bool IncludeDelivery,
        IReadOnlyList<string> ExtraRules,
        string? Format,
        int? Limit,
        bool NoCache,
        bool Gate,
        bool Structural,
        bool ExpectNoEffectChange
    );

    private static async Task<int> RunAsync(Options opts, CommandIo io)
    {
        var tsv = CommonOptions.IsTsv(opts.Format);
        var max = opts.Limit ?? int.MaxValue;
        var mode = CommonOptions.Mode(async: opts.Async, includeDelivery: opts.IncludeDelivery); // --async => walk sound handoffs (delivery fan-out excluded unless --include-delivery)

        // The HEAD store: opened once here so the deployment-map read (render chrome) shares it with DiffAsync
        // (which also reads it for provenance + the cold derivation). Opening issues no query.
        await using var context = await OpenReadContextGatedAsync(io.WorkspaceLocation with { StoreRef = opts.HeadRef });

        // F4: load the DeploymentMap ONCE (render-only — the --structural chips). Not part of the diff, so it
        // stays here rather than in DiffAsync (which the web endpoint also calls, without deployment chrome).
        var deployments = await LoadDeploymentsAsync(context, io.WorkspaceLocation.WorkingDirectory, io.TextOutput.Error);

        // The store-vs-store diff (warm-cached or freshly derived) — the SAME artifact the web /api/impact
        // returns, so `rig impact` and the web view cannot diverge.
        var art = await ImpactEngine.DiffAsync(
            headContext: context,
            ws: io.WorkspaceLocation,
            baseRef: opts.BaseRef,
            headRef: opts.HeadRef,
            mode: mode,
            gate: opts.Gate,
            noCache: opts.NoCache,
            extraRules: opts.ExtraRules
        );

        RenderImpact(
            output: io.TextOutput.Output,
            impactDiff: art.Diff,
            baseProv: art.BaseProvenance,
            headProv: art.HeadProvenance,
            mode: mode,
            deployments: deployments,
            fqnSites: art.FqnSites,
            tsv: tsv,
            structural: opts.Structural,
            max: max
        );
        return ExpectNoEffectChangeExit(opts.ExpectNoEffectChange, ImpactEngine.EffectChangedEpCount(art.Diff), io.TextOutput.Error);
    }

    // The `--expect-no-effect-change` CI gate. Behavioral change = an entry point present in BOTH commits whose
    // reachable EFFECT set changed (impactDiff.PerEp — the header's "N with a changed behavior"). Structural-only
    // reachable-tree ripple is NOT a behavioral change and never trips the gate. The verdict goes to STDERR so a
    // `--format tsv` run's stdout stays machine-clean; the exit code is the CI signal (1 = changed, 0 = clean).
    internal static int ExpectNoEffectChangeExit(bool expect, int behavioralEpCount, TextWriter error)
    {
        if (!expect)
        {
            return 0;
        }

        if (behavioralEpCount > 0)
        {
            error.WriteLine(
                $"--expect-no-effect-change FAILED: {behavioralEpCount} entry point(s) changed behavior (reachable effect set) — see the per-EP section. exit 1."
            );
            return 1;
        }

        error.WriteLine("--expect-no-effect-change OK: no entry point's effect set changed.");
        return 0;
    }

    // The render of a computed impact diff — shared by the cold (just-computed) and warm (cache-replayed)
    // paths so a hit is BYTE-IDENTICAL to a recompute. A pure function of the diff + provenance + deployments
    // + the FQN site map + the presentation flags: tsv emits the typed rows, else the human sections. FqnForCard
    // only ever looks up the diff's own sites, so the warm path's site SUBSET serves it exactly as the full map.
    private static void RenderImpact(
        TextWriter output,
        ImpactDiff impactDiff,
        StoreProvenance baseProv,
        StoreProvenance headProv,
        FactPathFinder.TraversalMode mode,
        DeploymentMap deployments,
        Dictionary<(string, int), string> fqnSites,
        bool tsv,
        bool structural,
        int max
    )
    {
        if (tsv)
        {
            EmitTsv(output, impactDiff, fqnSites, max);
            return;
        }

        WriteHeader(output, baseProv, headProv, mode, impactDiff);
        WriteEpDiffHuman(output, baseProv, impactDiff.Ep, max);
        // PRIMARY signal: the entry points whose reachable EFFECT set changed (the behavioral handful). Always
        // shown — this is the "what actually does something different" answer.
        WritePerEpHuman(output, baseProv, impactDiff.PerEp, fqnSites, max);
        // The structural reachable-tree diff is mostly data-shape ripple (a record field add lights up every
        // reaching EP). By default we DEMOTE it to a one-line, cause-classified breadcrumb so a no-net-new-effect
        // migration still can't hide; --structural expands it to the full per-EP list.
        if (structural)
        {
            WriteAffected(output, baseProv, impactDiff, deployments, fqnSites, max);
        }
        else
        {
            WriteStructuralBreadcrumb(output, baseProv, impactDiff, impactDiff.PerEp);
        }
    }

    private static void EmitEpDiffTsv(TextWriter output, EpDiff? diff)
    {
        if (diff is null)
        {
            return;
        }

        foreach (var (kind, route) in diff.Added)
        {
            output.WriteLine($"ep_added\t{kind}\t{route}");
        }

        foreach (var (kind, route) in diff.Removed)
        {
            output.WriteLine($"ep_removed\t{kind}\t{route}");
        }
    }

    private static void WriteEpDiffHuman(TextWriter output, StoreProvenance baseProv, EpDiff? diff, int max)
    {
        output.WriteLine();
        if (diff is null)
        {
            return;
        }

        output.WriteLine($"Entry-point diff vs '{baseProv.ShortLabel}': +{diff.Added.Count} added, -{diff.Removed.Count} removed");
        foreach (var (kind, route) in diff.Added.Take(max))
        {
            output.WriteLine($"{Indent.L1}+ {kind} {route}");
        }

        foreach (var (kind, route) in diff.Removed.Take(max))
        {
            output.WriteLine($"{Indent.L1}- {kind} {route}");
        }
    }

    // Display label for a reach node: a `R:`-prefixed degenerate field/property-access node (Phase 3) renders
    // as its short member name tagged `(field/prop access)`; an ordinary method DocID renders via ShortName.
    private static string ReachNodeLabel(string node) =>
        node.StartsWith(ImpactEngine.RefNodePrefix, StringComparison.Ordinal)
            ? $"{ShortName(node[ImpactEngine.RefNodePrefix.Length..])} (field/prop access)"
            : ShortName(node);

    private static void EmitPerEpTsv(TextWriter output, IReadOnlyList<EpFootprintDelta> deltas, Dictionary<(string, int), string> fqnSites)
    {
        foreach (var d in deltas)
        {
            var fqn = ImpactEngine.FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
            output.WriteLine(
                $"ep_delta\t{d.Kind}\t{d.Route}\t{fqn}\t{d.BranchEffects}\t{d.BaseEffects}\t+{d.Added.Count}\t-{d.Removed.Count}\t~{d.Amplified.Count}"
            );
            foreach (var (provider, operation, resource, enclosing) in d.Added)
            {
                output.WriteLine($"ep_effect_added\t{d.Kind}\t{d.Route}\t{provider}\t{operation}\t{resource}\t{enclosing}");
            }

            foreach (var (provider, operation, resource, enclosing) in d.Removed)
            {
                output.WriteLine($"ep_effect_removed\t{d.Kind}\t{d.Route}\t{provider}\t{operation}\t{resource}\t{enclosing}");
            }

            foreach (var a in d.Amplified)
            {
                output.WriteLine(
                    $"ep_effect_amplified\t{d.Kind}\t{d.Route}\t{a.Provider}\t{a.Operation}\t{a.Resource}\t{a.Enclosing}\t{a.BaseCount}\t{a.BranchCount}\t{a.BaseInLoop}\t{a.BranchInLoop}"
                );
            }

            // FR-1e: a guard (lock/async_lock) added/removed on a path that still mutates shared state.
            //  ep_guard_delta  <kind>  <route>  <+guards comma-joined>  <-guards comma-joined>
            if (ImpactEngine.HasGuardDeltaOnSharedMutation(d))
            {
                var (gAdded, gRemoved) = ImpactEngine.GuardEffectDelta(d);
                output.WriteLine($"ep_guard_delta\t{d.Kind}\t{d.Route}\t{string.Join(',', gAdded)}\t{string.Join(',', gRemoved)}");
            }

            // HAZARD DELTA: one row per hazard finding GAINED / LOST on this EP's reach.
            //  ep_hazard_added / ep_hazard_removed  <kind>  <route>  <type>  <confidence>  <cell>  <enclosing>
            foreach (var h in d.HazardsAddedOrEmpty)
            {
                output.WriteLine($"ep_hazard_added\t{d.Kind}\t{d.Route}\t{h.Type}\t{h.Confidence}\t{h.Cell}\t{h.Enclosing}");
            }

            foreach (var h in d.HazardsRemovedOrEmpty)
            {
                output.WriteLine($"ep_hazard_removed\t{d.Kind}\t{d.Route}\t{h.Type}\t{h.Confidence}\t{h.Cell}\t{h.Enclosing}");
            }
        }
    }

    // PRIMARY section: the entry points whose reachable EFFECT set changed — the behavioral signal. This is the
    // small, high-information set (a handful), as opposed to the structural reachable-tree diff which is mostly
    // data-shape ripple.
    private static void WritePerEpHuman(
        TextWriter output,
        StoreProvenance baseProv,
        IReadOnlyList<EpFootprintDelta> deltas,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        output.WriteLine();
        if (deltas.Count == 0)
        {
            output.WriteLine(
                $"Behavioral changes per entry point vs '{baseProv.ShortLabel}': none — no entry point's reachable-effect set changed."
            );
            return;
        }

        // The behavioral set = (effect-set changed) ∪ (amplified) — an EP whose set is stable but has an
        // amplified effect (now produced more / in a loop) is in `deltas` too (DiffFootprints lists it).
        output.WriteLine(
            $"Behavioral changes per entry point vs '{baseProv.ShortLabel}' (reachable-effect set changed or effect amplified): {deltas.Count}"
        );
        foreach (var d in deltas.Take(max))
        {
            // Render the FQN (round-trips into `rig tree`), same as the structural list; falls back to the route.
            var label = ImpactEngine.FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
            var ampPart = d.Amplified.Count > 0 ? $", ~{d.Amplified.Count} amplified" : "";
            var hazAdded = d.HazardsAddedOrEmpty;
            var hazRemoved = d.HazardsRemovedOrEmpty;
            var hazPart = hazAdded.Count > 0 || hazRemoved.Count > 0 ? $", hazards +{hazAdded.Count}/-{hazRemoved.Count}" : "";
            output.WriteLine(
                $"{Indent.L2}{d.Kind} {label}  (effects {d.BaseEffects}→{d.BranchEffects}, +{d.Added.Count}/-{d.Removed.Count}{ampPart}{hazPart})"
            );
            foreach (var (provider, operation, resource, enclosing) in d.Added.Take(max))
            {
                output.WriteLine($"{Indent.L3}+ {provider} {operation}{Resource(resource)}  ({enclosing})");
            }

            foreach (var (provider, operation, resource, enclosing) in d.Removed.Take(max))
            {
                output.WriteLine($"{Indent.L3}- {provider} {operation}{Resource(resource)}  ({enclosing})");
            }

            // Amplified effects: same key on both sides, but produced MORE or now in a loop. Marked `~` and
            // worded for REVIEW (not "regression") — the static signal can't tell a hot-cache re-read from a
            // real extra cold call. Note: a harmless ×1->×2 will show; that's the chosen tradeoff.
            foreach (var a in d.Amplified.Take(max))
            {
                output.WriteLine(
                    $"{Indent.L3}~ {a.Provider} {a.Operation}{Resource(a.Resource)}  ({AmplifyNote(a)})  ({a.Enclosing})  [review]"
                );
            }

            // FR-1e: a lock/guard was added or removed on a path that STILL mutates shared state — the
            // concurrency protection around an inherently-shared cell changed. High-signal: this is the
            // exact shape of the lock-guarded-class race (a guard lost, or a fix that adds one). Flagged for
            // review, not a verdict.
            if (ImpactEngine.HasGuardDeltaOnSharedMutation(d))
            {
                var (gAdded, gRemoved) = ImpactEngine.GuardEffectDelta(d);
                var moves = new List<string>();
                moves.AddRange(gAdded.Select(g => $"+{g}"));
                moves.AddRange(gRemoved.Select(g => $"-{g}"));
                output.WriteLine(
                    $"{Indent.L3}⚠ guard delta on a shared-mutation path: {string.Join(" ", moves)}  (shared_state mutation still reachable)  [review]"
                );
            }

            // HAZARD DELTA: the hazard findings (race_window / lazy_init_race / n_plus_1 /
            // unserializable_payload) this EP's reach GAINED (+) or LOST (-) — a refactor that opened a race on
            // this path, or a fix that closed one. Shown with the confidence tier + the cell/context, mirroring
            // the effect +/- lines. Flagged for review, not a verdict.
            foreach (var h in hazAdded.Take(max))
            {
                output.WriteLine($"{Indent.L3}+ hazard {h.Type} ({h.Confidence}){Cell(h.Cell)}  ({h.Enclosing})  [review]");
            }

            foreach (var h in hazRemoved.Take(max))
            {
                output.WriteLine($"{Indent.L3}- hazard {h.Type} ({h.Confidence}){Cell(h.Cell)}  ({h.Enclosing})");
            }
        }

        static string Resource(string resource) => string.IsNullOrEmpty(resource) ? "" : $" {resource}";
        static string Cell(string cell) => string.IsNullOrEmpty(cell) ? "" : $" {cell}";
    }

    // The amplification annotation: the count move (×base -> ×branch) and/or a loop-entry note, both when
    // both fired. Worded as an observation, not a verdict — it pairs with the `[review]` tag in the line.
    private static string AmplifyNote(EpEffectAmplified a)
    {
        var parts = new List<string>();
        if (a.BranchCount > a.BaseCount)
        {
            parts.Add($"×{a.BaseCount} -> ×{a.BranchCount}");
        }

        if (a.BranchInLoop && !a.BaseInLoop)
        {
            parts.Add("now in loop");
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "amplified";
    }

    // The header: the one-line PROVEN-diff takeaway, then which two commits/branches were compared. Both sides
    // are indexed commits — there is no working tree.
    private static void WriteHeader(
        TextWriter output,
        StoreProvenance baseProv,
        StoreProvenance headProv,
        FactPathFinder.TraversalMode mode,
        ImpactDiff diff
    )
    {
        var asyncNote = mode switch
        {
            FactPathFinder.TraversalMode.AsyncExact => "  (--async: handoffs included; delivery fan-out excluded)",
            FactPathFinder.TraversalMode.AsyncInclude => "  (--async --include-delivery: delivery fan-out included)",
            _ => "",
        };

        output.WriteLine(DiffSummary(baseProv, diff));
        output.WriteLine();
        output.WriteLine($"Impact: {baseProv.Label}  ->  {headProv.Label}{asyncNote}");
    }

    // The one-line takeaway: the PROVEN change vs the base store — entry points added/removed, entry points
    // whose behavior (reachable-effect set) changed, and entry points whose reachable tree changed.
    private static string DiffSummary(StoreProvenance baseProv, ImpactDiff diff)
    {
        // The "changed behavior" headline counts EFFECT-set changes only; hazard-only EPs are reported by
        // hazardNote below (PerEp includes them, but they aren't an effect-set change — no double-count).
        var behavioralEps = ImpactEngine.EffectChangedEpCount(diff);
        var added = diff.Ep?.Added.Count ?? 0;
        var removed = diff.Ep?.Removed.Count ?? 0;
        // FR-1e: count the EPs whose guard (lock/atomic) around a still-reachable shared mutation changed.
        // Only appended when non-zero so the common (no-guard-change) summary line stays unchanged.
        var guardEps = diff.PerEp.Count(ImpactEngine.HasGuardDeltaOnSharedMutation);
        var guardNote = guardEps > 0 ? $" ⚠ {guardEps} with a guard delta on a shared-mutation path." : "";
        // Hazard delta: count the EPs that GAINED or LOST a hazard finding (race_window / n+1 / …). Appended
        // only when non-zero so the common (no-hazard-change) summary line stays unchanged.
        var hazardEps = diff.PerEp.Count(d => d.HazardsAddedOrEmpty.Count > 0 || d.HazardsRemovedOrEmpty.Count > 0);
        var hazardNote = hazardEps > 0 ? $" ⚠ {hazardEps} with a hazard delta." : "";
        return $"Diff vs '{baseProv.ShortLabel}': {PlusMinus(added: added, removed: removed)} entry point(s)"
            + $"; {behavioralEps} entry point(s) with a changed behavior, {diff.AffectedEps.Count} with a changed reachable tree.{guardNote}{hazardNote}";
    }

    private static string PlusMinus(int added, int removed) => $"+{added}/-{removed}";

    // The DEMOTED structural view (default): one line stating how many EPs have a changed reachable tree but NO
    // behavioral (effect-set) change, broken down by cause so a data-shape ripple reads as exactly that — and a
    // no-net-new-effect migration still surfaces as a non-zero `other` count that can't hide. `--structural`
    // expands this to the full per-EP list (WriteAffected). EPs whose effect set DID change are already shown by
    // WritePerEpHuman, so they're excluded here (the two sections partition the affected set, no double-count).
    private static void WriteStructuralBreadcrumb(
        TextWriter output,
        StoreProvenance baseProv,
        ImpactDiff diff,
        IReadOnlyList<EpFootprintDelta> behavioralDeltas
    )
    {
        output.WriteLine();
        var behavioralKeys = behavioralDeltas.Select(d => (d.Kind, d.Route)).ToHashSet();
        var structuralOnly = diff.AffectedEps.Where(d => !behavioralKeys.Contains((d.Kind, d.Route))).ToList();
        if (structuralOnly.Count == 0)
        {
            output.WriteLine($"Structural-only reachable-tree changes vs '{baseProv.ShortLabel}': none.");
            return;
        }

        var byCause = structuralOnly.GroupBy(ImpactEngine.ClassifyStructuralCause).ToDictionary(g => g.Key, g => g.Count());
        int N(StructuralCause c) => byCause.GetValueOrDefault(c);
        var parts = new List<string>();
        if (N(StructuralCause.RecordShape) > 0)
        {
            parts.Add($"{N(StructuralCause.RecordShape)} record-shape (reach a changed field/property)");
        }

        if (N(StructuralCause.CtorSig) > 0)
        {
            parts.Add($"{N(StructuralCause.CtorSig)} ctor-signature");
        }

        if (N(StructuralCause.InPlace) > 0)
        {
            parts.Add($"{N(StructuralCause.InPlace)} in-place body change");
        }

        if (N(StructuralCause.Other) > 0)
        {
            parts.Add($"{N(StructuralCause.Other)} other method-level churn");
        }

        output.WriteLine(
            $"Structural-only reachable-tree changes vs '{baseProv.ShortLabel}' (no behavioral effect change): {structuralOnly.Count} entry point(s)"
        );
        output.WriteLine($"{Indent.L1}{string.Join(", ", parts)}");
        // The `other` bucket is the one that can hide a real migration (method churn with no NET-new effect kind),
        // so call it out explicitly when present — that's the line a reviewer should not skip.
        if (N(StructuralCause.Other) > 0)
        {
            output.WriteLine(
                $"{Indent.L1}↳ {N(StructuralCause.Other)} are method-level churn — review these (a migration can change reach without a net-new effect kind)."
            );
        }

        output.WriteLine($"{Indent.L1}--structural to list them all (or --format tsv).");
    }

    // The affected entry points, computed STRUCTURALLY: each EP whose full reachable symbol set differs
    // base↔branch ("two trees, diffed"), grouped by kind with deployment chips and the per-EP +added/-removed
    // reachable methods. Independent of effect classification — catches the obj→sql kind of migration the
    // effect-set diff collapses, and excludes false positives whose reach didn't actually move.
    private static void WriteAffected(
        TextWriter output,
        StoreProvenance baseProv,
        ImpactDiff diff,
        DeploymentMap deployments,
        Dictionary<(string, int), string> fqnSites,
        int max
    )
    {
        output.WriteLine();
        output.WriteLine($"Affected entry points (reachable tree changed) vs '{baseProv.ShortLabel}': {diff.AffectedEps.Count}");
        if (diff.AffectedEps.Count == 0)
        {
            output.WriteLine($"{Indent.L1}none — no entry point's reachable structure changed.");
            return;
        }

        foreach (var kindGroup in diff.AffectedEps.GroupBy(d => d.Kind, StringComparer.Ordinal).OrderByDescending(g => g.Count()))
        {
            output.WriteLine($"{Indent.L1}{kindGroup.Key}: {kindGroup.Count()}");
            foreach (var d in kindGroup.Take(max / 4 + 1))
            {
                // +added/-removed/~changed counted by DISTINCT STEM, so a 30-overload ctor swap reads as ~1,
                // not +30/-30. The `~` lines are signature changes (same stem on both sides); the +/- lines
                // are genuine reach gains/losses, labelled by ShortName (added/removed raw DocIDs, deduped to
                // their stem for display so an overload set doesn't print N near-identical lines).
                // The in-place suffix (Phase 2) flags an EP affected by a reachable method's BODY change with no
                // structural reach move — so an EP with empty +/-/~ but a changed constant still reads as why.
                var inPlaceNote = d.InPlaceCount > 0 ? $", in-place: {d.InPlaceCount} reached method body(ies) changed" : "";
                // Label the card with the FQN (round-trips into `rig tree`); fall back to the path route when the
                // EP site maps to no indexed method symbol. The diff still keys on (Kind, Route) internally.
                var label = ImpactEngine.FqnForCard(route: d.Route, filePath: d.FilePath, line: d.Line, idBySite: fqnSites);
                var route = $"{label}  (+{d.AddedStems.Count}/-{d.RemovedStems.Count}/~{d.ChangedStems.Count} reachable{inPlaceNote})";
                WriteEntryPointLine(output, deployments, route: route, filePath: d.FilePath, line: d.Line, requires: d.Requires);
                foreach (var s in d.AddedStems.Take(3))
                {
                    output.WriteLine($"{Indent.L3}+ {ReachNodeLabel(s)}");
                }

                foreach (var s in d.RemovedStems.Take(3))
                {
                    output.WriteLine($"{Indent.L3}- {ReachNodeLabel(s)}");
                }

                foreach (var s in d.ChangedStems.Take(3))
                {
                    output.WriteLine($"{Indent.L3}~ {ShortName(s)} (signature changed)");
                }

                foreach (var s in (d.InPlace ?? []).Take(3))
                {
                    output.WriteLine($"{Indent.L3}≈ {ShortName(s)} (body changed in place)");
                }
            }

            if (kindGroup.Count() > max / 4 + 1)
            {
                output.WriteLine($"{Indent.L3}… +{kindGroup.Count() - (max / 4 + 1)} more (raise --limit, or --format tsv for all)");
            }
        }
    }

    private static void EmitTsv(TextWriter output, ImpactDiff diff, Dictionary<(string, int), string> fqnSites, int max)
    {
        // One stream of typed rows for CI/tooling. First column is the row kind. Every row here is the
        // STORE-vs-STORE derived-facts diff: the EP set diff + the per-EP footprint/reach diff between the two
        // indexed commits. There is NO git working-tree diff and NO speculative reverse-reach blast radius — the
        // old `changed` / `effect_added` / `effect_removed` / `obs_*` rows and the `entrypoint` / `effect`
        // (reverse-reach) rows are gone; read the per-EP rows (ep_delta / ep_effect_*) for the same effects,
        // attributed and correct.
        //  affected_ep  <kind>  <route>  <fqn>  <cause>  <file>  <line>  <+addedStems>  <-removedStems>  <~changedStems>  <inplace>   (proven; <route> is the path-style diff key, <fqn> the dotted name `rig tree` matches — equals <route> when unresolved; <cause> is behavioral|record-shape|ctor-sig|in-place|other — behavioral = effect set changed, the rest are structural-only; counts are DISTINCT param-free stems; inplace = reachable bodies changed)
        //  structural_summary  <total>  <behavioral>  <record-shape>  <ctor-sig>  <in-place>  <other>   (one row: the cause breakdown of the affected-EP set — behavioral counts the EPs whose effect set changed, the rest are structural-only)
        //  ep_reach_+   <kind>  <route>  <symbolId>                            (newly in the EP's reach — raw method DocID, or an `R:`-prefixed field/property-access target, Phase 3)
        //  ep_reach_-   <kind>  <route>  <symbolId>                            (dropped from the EP's reach — raw method DocID, or an `R:`-prefixed field/property-access target, Phase 3)
        //  ep_reach_~   <kind>  <route>  <stem>                                (a reachable method whose SIGNATURE changed — param-free stem)
        //  ep_reach_inplace  <kind>  <route>  <symbolId>                       (a reachable method whose BODY changed in place — raw DocID, Phase 2)
        //  ep_added     <kind>  <route>                                        (an entry point present only on the HEAD store)
        //  ep_removed   <kind>  <route>                                        (an entry point present only on the BASE store)
        //  ep_delta     <kind>  <route>  <fqn>  <branchEffects>  <baseEffects>  <+added>  <-removed>  <~amplified>   (one per EP whose reachable-effect footprint changed: set membership and/or amplification; counts are effect KEYS)
        //  ep_effect_added    <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>   (an effect KEY newly in the EP's footprint)
        //  ep_effect_removed  <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>   (an effect KEY dropped from the EP's footprint)
        //  ep_effect_amplified  <kind>  <route>  <provider>  <operation>  <resource>  <enclosing>  <baseCount>  <branchCount>  <baseInLoop>  <branchInLoop>   (Feature 1: SAME key on both stores but produced MORE — branchCount>baseCount — and/or MOVED INTO A LOOP — branchInLoop && !baseInLoop. count = # distinct reachable effect-bearing producing nodes. A REVIEW flag, not a verdict: can't tell a hot-cache re-read from a real extra cold call.)
        //  ep_guard_delta  <kind>  <route>  <+guards>  <-guards>   (FR-1e: a lock/async_lock acquire/release ADDED (+, comma-joined provider:operation) or REMOVED (-) on a path whose branch reach STILL carries a shared_state mutation — the concurrency guard around an inherently-shared cell changed. A REVIEW flag covering both the lost-guard race and the guard-adding fix.)
        //  ep_hazard_added    <kind>  <route>  <type>  <confidence>  <cell>  <enclosing>   (a hazard finding — race_window / lazy_init_race / n_plus_1 / unserializable_payload, see HazardKinds — newly present on the EP's reach: a refactor opened it. cell = the observation Context, enclosing = the param-free producing method. A REVIEW flag, not a verdict.)
        //  ep_hazard_removed  <kind>  <route>  <type>  <confidence>  <cell>  <enclosing>   (a hazard finding that DROPPED from the EP's reach base->head: a fix closed it. Same columns as ep_hazard_added.)

        // Cause per EP: behavioral when its effect set changed (it's in PerEp), else the structural sub-cause.
        var behavioralKeys = diff.PerEp.Select(d => (d.Kind, d.Route)).ToHashSet();
        string CauseTag(EpReachDelta e) =>
            behavioralKeys.Contains((e.Kind, e.Route))
                ? "behavioral"
                : ImpactEngine.ClassifyStructuralCause(e) switch
                {
                    StructuralCause.RecordShape => "record-shape",
                    StructuralCause.CtorSig => "ctor-sig",
                    StructuralCause.InPlace => "in-place",
                    _ => "other",
                };

        // structural_summary: the cause breakdown of the WHOLE affected set (not capped by --limit) so tooling
        // gets the true totals even when the per-EP rows below are truncated.
        var causeCounts = diff
            .AffectedEps.GroupBy(CauseTag, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        int CC(string k) => causeCounts.GetValueOrDefault(k);
        output.WriteLine(
            $"structural_summary\t{diff.AffectedEps.Count}\t{CC("behavioral")}\t{CC("record-shape")}\t{CC("ctor-sig")}\t{CC("in-place")}\t{CC("other")}"
        );

        foreach (var e in diff.AffectedEps.Take(max))
        {
            var fqn = ImpactEngine.FqnForCard(route: e.Route, filePath: e.FilePath, line: e.Line, idBySite: fqnSites);
            output.WriteLine(
                $"affected_ep\t{e.Kind}\t{e.Route}\t{fqn}\t{CauseTag(e)}\t{e.FilePath}\t{e.Line}\t+{e.AddedStems.Count}\t-{e.RemovedStems.Count}\t~{e.ChangedStems.Count}\t{e.InPlaceCount}"
            );
            foreach (var s in e.Added)
            {
                output.WriteLine($"ep_reach_+\t{e.Kind}\t{e.Route}\t{s}");
            }

            foreach (var s in e.Removed)
            {
                output.WriteLine($"ep_reach_-\t{e.Kind}\t{e.Route}\t{s}");
            }

            foreach (var s in e.ChangedStems)
            {
                output.WriteLine($"ep_reach_~\t{e.Kind}\t{e.Route}\t{s}");
            }

            foreach (var s in e.InPlace ?? [])
            {
                output.WriteLine($"ep_reach_inplace\t{e.Kind}\t{e.Route}\t{s}");
            }
        }

        EmitEpDiffTsv(output, diff.Ep);
        EmitPerEpTsv(output, diff.PerEp, fqnSites);
    }
}
