using System.CommandLine;
using Rig.Analysis.Rules;
using Rig.Cli.CommandLine;
using Rig.Cli.Rendering;
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
                        await using var context = OpenReadContext(workingDirectory);
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
                        await using var context = OpenReadContext(workingDirectory: workingDirectory, storeRef: storeId);
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
        var cmd = new Command(name: "di", description: "DI registrations: service -> implementation, lifetime, source.");
        cmd.SetAction(_ =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    await using var context = OpenReadContext(workingDirectory);
                    var registrations = await Reads.LoadDiRegistrationsAsync(context);
                    if (registrations is null)
                    {
                        return CommandGuard.NoRunError(error);
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
        var cmd = new Command(name: "files", description: "Inspect indexed source files.") { skipped };
        // `files` exists only to serve --skipped today; keep the historical usage hint as a validator
        // (exits with a parse error rather than running an empty command).
        cmd.Validators.Add(result =>
        {
            if (!result.GetValue(skipped))
            {
                result.AddError("Usage: rig files --skipped");
            }
        });
        cmd.SetAction(_ =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    await using var context = OpenReadContext(workingDirectory);
                    var sourceFiles = await Reads.LoadSkippedSourceFilesAsync(context);
                    if (sourceFiles is null)
                    {
                        return CommandGuard.NoRunError(error);
                    }

                    SourceFileRenderer.RenderSkipped(sourceFiles, output);
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
        var cmd = new Command(name: "symbols", description: "Search indexed symbols by name.") { pattern, kind, limit };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    var p = pr.GetValue(pattern)!;
                    var k = pr.GetValue(kind);
                    await using var context = OpenReadContext(workingDirectory);
                    var hits = await Reads.SearchSymbolsAsync(context, pattern: p, kind: k, limit: pr.GetValue(limit));
                    output.WriteLine($"Symbols matching '{p}'{(k is null ? "" : $" kind={k}")}");
                    foreach (var hit in hits)
                    {
                        output.WriteLine($"{Indent.L1}{hit.Kind, -8} {hit.SymbolId}  {ShortenPath(hit.FilePath)}:{hit.Line}");
                    }

                    output.WriteLine($"{Indent.L1}({hits.Count} shown)");
                    return 0;
                }
            )
        );
        return cmd;
    }

    internal static Command BuildRefs(TextWriter output, TextWriter error, string workingDirectory)
    {
        var pattern = CommonOptions.Pattern(name: "pattern", description: "Target symbol pattern to find references to.");
        var firstParty = new Option<bool>("--first-party") { Description = "Only references from first-party code." };
        var kind = CommonOptions.Kind();
        var limit = CommonOptions.Limit(200);
        var cmd = new Command(name: "refs", description: "Find references to a symbol.") { pattern, firstParty, kind, limit };
        cmd.SetAction(pr =>
            CommandGuard.RunGuardedAsync(
                workingDirectory,
                error,
                async () =>
                {
                    var p = pr.GetValue(pattern)!;
                    var fp = pr.GetValue(firstParty);
                    var refKind = pr.GetValue(kind);
                    await using var context = OpenReadContext(workingDirectory);
                    var hits = await Reads.FindReferencesAsync(
                        context,
                        pattern: p,
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
}
