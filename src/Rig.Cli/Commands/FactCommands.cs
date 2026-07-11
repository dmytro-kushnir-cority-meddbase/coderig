using System.CommandLine;
using Microsoft.EntityFrameworkCore;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
using Rig.Cli.Services;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using static Rig.Cli.Graph.TraversalGraphLoader;
using static Rig.Cli.Rendering.SymbolNameFormatter;

namespace Rig.Cli.Commands;

// The simple fact READERS: runs, di, profile, files, symbols, refs. Each opens the store read-only and
// renders, wrapped in CommandGuard so a missing/stale store reports cleanly.
internal static class FactCommands
{
    internal static Command BuildRuns(TextWriter output, TextWriter error, string workingDirectory)
    {
        var cmd = new Command(name: "runs", description: "List indexed runs across every per-commit store (solution, counts, timestamp).");
        cmd.SetAction(_ =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    // Per-commit layout: enumerate EVERY store, not just LATEST — otherwise `rig runs` hides
                    // the other indexed commits (e.g. the base store `impact --base` diffs against), which
                    // made a mine-order / pointer mix-up impossible to see. Mark the LATEST (the store the
                    // other read commands default to). No per-commit stores => fall back to the single
                    // default context (legacy flat store, or a clean "no store" via CommandGuard).
                    var storeIds = StoreLayout.AvailableStoreIds(workingDirectory);
                    if (storeIds.Count == 0)
                    {
                        await using var context = await OpenReadContextGatedAsync(new WorkspaceLocation(workingDirectory));
                        output.WriteLine("Runs");
                        await RenderRunsAsync(context: context, output: output, idIndent: Indent.L1, detailIndent: Indent.L2);
                        return 0;
                    }

                    var latest = StoreLayout.LatestStoreId(workingDirectory);
                    output.WriteLine($"Runs ({storeIds.Count} store(s) in {StoreLayout.RigDir(workingDirectory)})");
                    foreach (var storeId in storeIds)
                    {
                        var marker = string.Equals(storeId, latest, StringComparison.OrdinalIgnoreCase) ? "  ← LATEST (read default)" : "";
                        output.WriteLine($"{Indent.L1}store {storeId}{marker}");
                        await using var context = await OpenReadContextGatedAsync(
                            new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: storeId)
                        );
                        await RenderRunsAsync(context: context, output: output, idIndent: Indent.L2, detailIndent: Indent.L3);
                    }

                    return 0;
                }
            )
        );
        return cmd;
    }

    // Render the runs in one open store context, at the given indent levels (id line / detail lines).
    private static async Task RenderRunsAsync(RigDbContext context, TextWriter output, string idIndent, string detailIndent)
    {
        var runs = await Reads.ListRunsAsync(context);
        foreach (var run in runs)
        {
            output.WriteLine($"{idIndent}{run.Id}");
            output.WriteLine($"{detailIndent}indexed={run.CreatedAtUtc:u}");
            output.WriteLine($"{detailIndent}solution={run.SolutionPath}");
            if (run.SourceCommit is { } commit)
            {
                var shortSha = commit.Length >= 12 ? commit[..12] : commit;
                var branch = run.SourceBranch is { } b ? $" ({b})" : "";
                var dirty = run.SourceDirty ? " +dirty" : "";
                output.WriteLine($"{detailIndent}commit={shortSha}{branch}{dirty}");
            }

            output.WriteLine($"{detailIndent}symbols={run.SymbolCount} references={run.ReferenceCount} di={run.DiRegistrationCount}");
        }
    }

    internal static Command BuildDi(TextWriter output, TextWriter error, string workingDirectory)
    {
        var storeRef = CommonOptions.Store();
        var cmd = new Command(name: "di", description: "DI registrations: service -> implementation, lifetime, source.") { storeRef };

        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    await using var context = await OpenReadContextGatedAsync(
                        new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: pr.GetValue(storeRef))
                    );
                    var registrations = await Reads.LoadDiRegistrationsAsync(context);
                    if (registrations is null)
                    {
                        return CommandGuard.NoRunError(error);
                    }

                    if (registrations.Count == 0)
                    {
                        output.WriteLine("DI Registrations");
                        output.WriteLine($"{Indent.L1}0 DI registrations found.");
                        output.WriteLine(
                            $"{Indent.L1}(DI is mined from XML DI config files during `rig index`; an empty result is expected for projects without XML-based DI.)"
                        );
                        return 0;
                    }

                    DiRenderer.Render(registrations, output);
                    return 0;
                }
            )
        );
        return cmd;
    }

    internal static Command BuildProfile(TextWriter output, TextWriter error, string workingDirectory)
    {
        var validate = new Command(name: "validate", description: "Validate the analysis profile for this solution.");
        validate.SetAction(_ =>
        {
            try
            {
                AnalysisProfileValidator.ValidateForSolution(workingDirectory);
                output.WriteLine("Profile: valid");
                return Task.FromResult(0);
            }
            catch (Exception exception)
            {
                error.WriteLine($"Profile: invalid — {exception.Message}");
                return Task.FromResult(2);
            }
        });
        return new Command(name: "profile", description: "Analysis-profile commands.") { validate };
    }

    internal static Command BuildFiles(TextWriter output, TextWriter error, string workingDirectory)
    {
        var skipped = new Option<bool>("--skipped") { Description = "List source files skipped during indexing." };
        var storeRef = CommonOptions.Store();
        var cmd = new Command(name: "files", description: "Inspect indexed source files.") { skipped, storeRef };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    await using var context = await OpenReadContextGatedAsync(
                        new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: pr.GetValue(storeRef))
                    );
                    if (pr.GetValue(skipped))
                    {
                        var sourceFiles = await Reads.LoadSkippedSourceFilesAsync(context);
                        if (sourceFiles is null)
                        {
                            return CommandGuard.NoRunError(error);
                        }

                        SourceFileRenderer.RenderSkipped(sourceFiles, output);
                        return 0;
                    }

                    // Bare invocation: summarise what is in the store so it is not a dead end.
                    if (!await context.Database.CanConnectAsync())
                    {
                        return CommandGuard.NoRunError(error);
                    }

                    var indexedCount = await context
                        .SourceFiles.Where(f => f.Status != "skipped")
                        .Select(f => f.FilePath)
                        .Distinct()
                        .CountAsync();
                    var skippedCount = await context
                        .SourceFiles.Where(f => f.Status == "skipped")
                        .Select(f => f.FilePath)
                        .Distinct()
                        .CountAsync();
                    output.WriteLine("Source Files");
                    output.WriteLine($"{Indent.L1}{indexedCount} indexed source file(s)");
                    if (skippedCount > 0)
                    {
                        output.WriteLine($"{Indent.L1}({skippedCount} skipped — use --skipped to list)");
                    }

                    return 0;
                }
            )
        );
        return cmd;
    }

    internal static Command BuildSymbols(TextWriter output, TextWriter error, string workingDirectory)
    {
        var pattern = CommonOptions.Pattern(name: "pattern", description: "Symbol name pattern to search for.");
        var kind = CommonOptions.Kind();
        var limit = CommonOptions.Limit(50);
        var noLambdas = new Option<bool>("--no-lambdas")
        {
            Description = "Exclude compiler-generated lambdas (symbols containing ~λ in their DocID).",
        };
        var storeRef = CommonOptions.Store();
        var cmd = new Command(name: "symbols", description: "Search indexed symbols by name.")
        {
            pattern,
            kind,
            limit,
            noLambdas,
            storeRef,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    var p = pr.GetValue(pattern)!;
                    var k = pr.GetValue(kind);
                    var cap = pr.GetValue(limit);
                    var filterLambdas = pr.GetValue(noLambdas);
                    var sr = pr.GetValue(storeRef);
                    var ws = new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: sr);
                    await using var context = await OpenReadContextGatedAsync(ws);
                    // Fetch beyond the display cap so we can compute the true post-filter total: the LIKE
                    // fallback hard-caps at 5000 unique rows, and the FTS path returns all matches.
                    var allHits = await Reads.SearchSymbolsAsync(context, pattern: p, kind: k, limit: int.MaxValue);
                    var filtered = filterLambdas
                        ? allHits.Where(h => !h.SymbolId.Contains("~λ", StringComparison.Ordinal)).ToList()
                        : allHits.ToList();

                    var total = filtered.Count;
                    var shown = filtered.Take(cap).ToList();
                    output.WriteLine($"Symbols matching '{p}'{(k is null ? "" : $" kind={k}")}");
                    foreach (var hit in shown)
                    {
                        output.WriteLine($"{Indent.L1}{hit.Kind, -8} {hit.SymbolId}  {ShortenPath(hit.FilePath)}:{hit.Line}");
                    }

                    if (total > cap)
                    {
                        output.WriteLine($"{Indent.L1}(showing {cap} of {total} — use --limit to raise)");
                    }
                    else
                    {
                        output.WriteLine($"{Indent.L1}({total} shown)");
                    }

                    return 0;
                }
            )
        );
        return cmd;
    }

    internal static Command BuildRefs(TextWriter output, TextWriter error, string workingDirectory)
    {
        // pattern is OPTIONAL (ZeroOrOne): the default symbol-reference path still requires it (an explicit
        // guard below mirrors the old required-argument error), but the --unused/--usage assembly modes take
        // it as an optional substring FILTER on assembly names.
        var pattern = CommonOptions.Pattern(
            name: "pattern",
            description: "Target symbol pattern; for --unused/--usage an optional substring filter on assembly names."
        );
        pattern.Arity = ArgumentArity.ZeroOrOne;
        var unused = new Option<bool>("--unused")
        {
            Description =
                "List declared <ProjectReference> edges with zero first-party symbol usage (candidate prunable references); pattern filters declaring assemblies.",
        };
        var usage = new Option<bool>("--usage")
        {
            Description = "Show inbound first-party reference count per assembly; pattern filters target assemblies.",
        };
        var tsv = new Option<bool>("--tsv")
        {
            Description = "With --unused/--usage: emit tab-separated rows instead of the grouped human render.",
        };
        var firstParty = new Option<bool>("--first-party") { Description = "Only references from first-party code." };
        var kind = CommonOptions.Kind();
        var limit = CommonOptions.Limit(200);
        var storeRef = CommonOptions.Store();
        var cmd = new Command(name: "refs", description: "Find references to a symbol; or analyze assembly references (--unused/--usage).")
        {
            pattern,
            unused,
            usage,
            tsv,
            firstParty,
            kind,
            limit,
            storeRef,
        };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    var p = pr.GetValue(pattern);
                    var wantUnused = pr.GetValue(unused);
                    var wantUsage = pr.GetValue(usage);
                    var asTsv = pr.GetValue(tsv);
                    var sr = pr.GetValue(storeRef);

                    // Default (symbol-reference) path requires a pattern — mirror the old required-argument
                    // error, and BEFORE touching the store so the message is store-independent (as the parse
                    // error was). The --unused/--usage modes take pattern as an optional filter instead.
                    if (!wantUnused && !wantUsage && string.IsNullOrEmpty(p))
                    {
                        error.WriteLine("Required argument missing for command: 'refs'.");
                        return 1;
                    }

                    // The --unused/--usage assembly modes route through UnusedRefsQueryService (the SAME
                    // orchestration the web endpoint calls — no duplicated codepath), which opens its own read
                    // context; only the default symbol-reference path needs the context opened here.
                    if (wantUnused)
                    {
                        return await RunUnusedRefsAsync(workingDirectory, sr, p, asTsv, output, error);
                    }

                    if (wantUsage)
                    {
                        return await RunUsageRefsAsync(workingDirectory, sr, p, asTsv, output);
                    }

                    await using var context = await OpenReadContextGatedAsync(
                        new WorkspaceLocation(WorkingDirectory: workingDirectory, StoreRef: sr)
                    );

                    var fp = pr.GetValue(firstParty);
                    var refKind = pr.GetValue(kind);
                    // p is non-null here: the guard above returned for the empty-pattern default case.
                    var hits = await Reads.FindReferencesAsync(
                        context,
                        pattern: p!,
                        firstPartyOnly: fp,
                        refKind: refKind,
                        limit: pr.GetValue(limit)
                    );
                    output.WriteLine($"References to '{p}'{(fp ? " (first-party)" : "")}{(refKind is null ? "" : $" kind={refKind}")}");
                    foreach (
                        var group in hits.GroupBy(h => h.TargetSymbolId, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
                    )
                    {
                        output.WriteLine($"{Indent.L1}{group.Key}");
                        foreach (var hit in group)
                        {
                            output.WriteLine(
                                $"{Indent.L2}{hit.RefKind, -11} {hit.EnclosingSymbolId ?? "(top-level)"}  {ShortenPath(hit.FilePath)}:{hit.Line}"
                            );
                        }
                    }
                    output.WriteLine($"{Indent.L1}({hits.Count} reference(s) shown)");
                    return 0;
                }
            )
        );
        return cmd;
    }

    // `rig refs --unused [pattern]`: diff the declared <ProjectReference> graph (parsed from the solution's
    // .csproj files, no MSBuild) against the observed first-party usage edges (mined from facts), rendering
    // the declared assembly edges with zero usage — candidate prunable references. pattern (optional) filters
    // DECLARING assemblies by case-insensitive substring. The data path lives in UnusedRefsQueryService (shared
    // with the web endpoint); this method owns only the human + --tsv rendering and the filter.
    private static async Task<int> RunUnusedRefsAsync(
        string workingDirectory,
        string? storeRef,
        string? pattern,
        bool tsv,
        TextWriter output,
        TextWriter error
    )
    {
        var result = await UnusedRefsQueryService.UnusedAsync(workingDirectory, storeRef);
        if (!result.SolutionAvailable)
        {
            error.WriteLine(
                "Cannot analyze project references: the indexed solution's .csproj files are unavailable (re-index, or run from the store's directory)."
            );
            return 2;
        }

        var candidates = result.Candidates;
        if (!string.IsNullOrEmpty(pattern))
        {
            candidates = candidates.Where(c => c.DeclaringAsm.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (tsv)
        {
            foreach (var c in candidates)
            {
                output.WriteLine($"{c.DeclaringAsm}\t{c.UnusedAsm}");
            }

            return 0;
        }

        output.WriteLine("Unused project references (statically unused — reflection/markup loads NOT accounted for; verify via AUT):");
        var groups = candidates.GroupBy(c => c.DeclaringAsm, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var group in groups)
        {
            output.WriteLine($"{Indent.L1}{group.Key}");
            foreach (var c in group.OrderBy(x => x.UnusedAsm, StringComparer.Ordinal))
            {
                output.WriteLine($"{Indent.L2}-> {c.UnusedAsm}");
            }
        }

        output.WriteLine($"{Indent.L1}({candidates.Count} candidate(s) across {groups.Count} project(s))");
        return 0;
    }

    // `rig refs --usage [pattern]`: inbound first-party reference count per assembly, ascending (least-used
    // first). pattern (optional) filters TARGET assemblies by case-insensitive substring. Data path shared
    // with the web endpoint via UnusedRefsQueryService.
    private static async Task<int> RunUsageRefsAsync(
        string workingDirectory,
        string? storeRef,
        string? pattern,
        bool tsv,
        TextWriter output
    )
    {
        var counts = await UnusedRefsQueryService.UsageAsync(workingDirectory, storeRef);
        if (!string.IsNullOrEmpty(pattern))
        {
            counts = counts.Where(c => c.Assembly.Contains(pattern, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (tsv)
        {
            foreach (var c in counts)
            {
                output.WriteLine($"{c.Assembly}\t{c.Refs}\t{c.FromMethods}");
            }

            return 0;
        }

        output.WriteLine("Assembly usage (inbound first-party references):");
        foreach (var c in counts)
        {
            output.WriteLine($"{Indent.L1}{c.Refs, 6}  {c.FromMethods, 6}  {c.Assembly}");
        }

        return 0;
    }
}
