using Rig.Cli;
using Rig.Cli.CommandLine;
using Rig.Domain.Data;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// `rig impact` end-to-end as a PURE two-store derived-facts diff. These tests drive the FULL command through
// CliApplication.RunAsync (the same entry the CLI uses) against two indexed per-commit stores materialized from
// analyzed playgrounds, and assert the rendered header + the derived diff — there is NO git working-tree seed,
// no --reach, no --repo. They are the integration coverage that replaces the old manual validation: identical
// stores diff to nothing, different stores surface a real per-EP behavioral/structural diff, the header renders
// both sides (with branch+commit provenance when present, falling back to the store-id when absent), and the
// missing-arg path errors cleanly.
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class ImpactTwoStoreTests(AnalyzedPlaygrounds playgrounds)
{
    // (a) Two stores materialized from the SAME analysis result, with NO git provenance. The derived-facts diff
    // is empty (no EP added/removed, no behavioral change, no reachable-tree change), the run succeeds, and the
    // header renders BOTH sides falling back to the store-id (no branch/commit) WITHOUT crashing or printing
    // "()" — the null-provenance path.
    [Test]
    public async Task Identical_stores_diff_to_nothing_and_header_falls_back_to_store_id()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var wd = NewWorkingDirectory();
        try
        {
            // No provenance => the header has no branch/commit to render and must fall back to the store-id ref.
            // Distinct ids so each --base/--head ref resolves unambiguously (exact-id match wins).
            var baseId = await MaterializeStoreAsync(wd, pg.Result, storeId: "identicalbase", provenance: null);
            var headId = await MaterializeStoreAsync(wd, pg.Result, storeId: "identicalhead", provenance: null);

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await CliApplication.RunAsync(["impact", "--base", baseId, "--head", headId], output, error, wd);

            exit.ShouldBe(0);
            var human = output.ToString();

            // The header renders both sides, falling back to the store-id (no branch, no "()" on null provenance).
            human.ShouldContain("Impact:");
            human.ShouldContain(baseId);
            human.ShouldContain(headId);
            human.ShouldContain("->");
            human.ShouldNotContain("()"); // never an empty "(<short>)" parenthetical on null provenance

            // The derived-facts diff is empty across all three signals.
            human.ShouldContain("+0/-0 entry point(s)"); // EP set diff: nothing added/removed
            human.ShouldContain("0 entry point(s) with a changed behavior");
            human.ShouldContain("0 with a changed reachable tree");
            human.ShouldContain("no entry point's reachable-effect set changed"); // the behavioral "none" line
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // (a2) FR-4 gate end-to-end: identical stores have no per-EP effect change, so
    // `--expect-no-effect-change` must PASS (exit 0) and print its OK verdict to STDERR — proving the gate
    // doesn't false-positive on a behavior-preserving diff (the property that makes it safe in CI) and that
    // the flag is wired through the full command. (The fail path — count > 0 → exit 1 — is unit-covered in
    // ImpactExpectGateTests; constructing a shared-EP effect delta from two stores is disproportionate here.)
    [Test]
    public async Task Expect_no_effect_change_passes_on_identical_stores()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var wd = NewWorkingDirectory();
        try
        {
            var baseId = await MaterializeStoreAsync(wd, pg.Result, storeId: "gatebase", provenance: null);
            var headId = await MaterializeStoreAsync(wd, pg.Result, storeId: "gatehead", provenance: null);

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await CliApplication.RunAsync(
                ["impact", "--base", baseId, "--head", headId, "--expect-no-effect-change"],
                output,
                error,
                wd
            );

            exit.ShouldBe(0);
            // Report still renders on stdout; the gate verdict is on stderr (so --format tsv stays clean).
            output.ToString().ShouldContain("0 entry point(s) with a changed behavior");
            error.ToString().ShouldContain("OK");
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // (b) Two stores from DIFFERENT sources (EntryPointEffects vs LegacyNet48). The entry-point set genuinely
    // differs both ways and the reachable effects/trees differ, so the diff is non-empty: some EPs are flagged
    // and the structural breadcrumb (or behavioral section) renders. Asserts the STRUCTURE of the output, not
    // exact counts (those are playground-dependent).
    [Test]
    public async Task Different_stores_surface_a_non_empty_per_ep_diff_and_render_the_sections()
    {
        var headPg = await playgrounds.EntryPointEffectsAsync();
        var basePg = await playgrounds.LegacyNet48Async();
        var wd = NewWorkingDirectory();
        try
        {
            var baseId = await MaterializeStoreAsync(wd, basePg.Result, storeId: "diffbase", provenance: null);
            var headId = await MaterializeStoreAsync(wd, headPg.Result, storeId: "diffhead", provenance: null);

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await CliApplication.RunAsync(["impact", "--base", baseId, "--head", headId], output, error, wd);

            exit.ShouldBe(0);
            var human = output.ToString();

            // Header + summary scaffolding present.
            human.ShouldContain("Impact:");
            human.ShouldContain("Diff vs");

            // The two sources have entry points the other lacks => the EP set diff is non-empty both ways.
            human.ShouldContain("Entry-point diff vs");
            human.ShouldContain("+ "); // at least one added entry point line
            human.ShouldContain("- "); // at least one removed entry point line

            // The behavioral section always renders (header line present whether or not any EP changed); and the
            // structural breadcrumb renders by default (no --structural). At least one of the per-EP sections
            // names a real change for genuinely different sources.
            human.ShouldContain("Behavioral changes per entry point");
            human.ShouldContain("Structural-only reachable-tree changes");

            // The tsv form emits the proven, typed store-vs-store rows with no human chrome.
            output.GetStringBuilder().Clear();
            (await CliApplication.RunAsync(["impact", "--base", baseId, "--head", headId, "--format", "tsv"], output, error, wd)).ShouldBe(
                0
            );
            var tsv = output.ToString();
            tsv.ShouldContain("\t");
            tsv.ShouldContain("structural_summary\t");
            tsv.ShouldContain("ep_added\t"); // EP present only on HEAD (sources differ)
            tsv.ShouldContain("ep_removed\t"); // EP present only on BASE
            tsv.ShouldNotContain("Diff vs"); // no human summary chrome in tsv mode
            tsv.ShouldNotContain("working-tree"); // the git working-tree seed is gone
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // (d) Both stores stamped with branch + commit provenance (GitProvenance at save time). The header must
    // render "branch (sha) -> branch (sha)" — the 12-char short sha and the branch on each side. Proves the
    // provenance IS injectable in a test (no real git needed): Writes.SaveAsync persists it, ReadProvenanceAsync
    // reads it back, and the store-id is the short sha so --base/--head address it the way `rig index` would.
    [Test]
    public async Task Stamped_provenance_renders_branch_and_short_sha_per_side()
    {
        var pg = await playgrounds.EntryPointEffectsAsync();
        var wd = NewWorkingDirectory();
        try
        {
            // 40-char shas; the store-id (and the header's short label) is the first 12 chars.
            const string baseCommit = "aaaaaaaaaaaa1111111111111111111111111111";
            const string headCommit = "bbbbbbbbbbbb2222222222222222222222222222";
            var baseId = await MaterializeStoreAsync(
                wd,
                pg.Result,
                storeId: baseCommit[..12],
                provenance: new GitProvenance(Commit: baseCommit, Branch: "main", Dirty: false)
            );
            var headId = await MaterializeStoreAsync(
                wd,
                pg.Result,
                storeId: headCommit[..12],
                provenance: new GitProvenance(Commit: headCommit, Branch: "feature/x", Dirty: false)
            );

            var output = new StringWriter();
            var error = new StringWriter();
            var exit = await CliApplication.RunAsync(["impact", "--base", baseId, "--head", headId], output, error, wd);

            exit.ShouldBe(0);
            var human = output.ToString();

            // The header is "Impact: <baseBranch> (<baseShort>)  ->  <headBranch> (<headShort>)".
            human.ShouldContain($"Impact: main ({baseCommit[..12]})  ->  feature/x ({headCommit[..12]})");
            // The diff-summary leads with the short sha (its ShortLabel), not the raw store-ref.
            human.ShouldContain($"Diff vs '{baseCommit[..12]}'");
        }
        finally
        {
            TryDelete(wd);
        }
    }

    // The two-store contract: BOTH refs are mandatory. A missing --base or --head (or both) is a clean
    // command-validation error (exit 1) before any store is opened — there is no working-tree fallback.
    [Test]
    public async Task Missing_either_store_ref_is_a_validation_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        (await CliApplication.RunAsync(["impact"], output, error)).ShouldBe(1);
        error.ToString().ShouldContain("rig impact requires both --base <store> and --head <store>");

        error.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["impact", "--base", "deadbeef"], output, error)).ShouldBe(1);
        error.ToString().ShouldContain("requires both --base");

        error.GetStringBuilder().Clear();
        (await CliApplication.RunAsync(["impact", "--head", "deadbeef"], output, error)).ShouldBe(1);
        error.ToString().ShouldContain("requires both --base");
    }

    private static string NewWorkingDirectory()
    {
        var wd = Path.Combine(Path.GetTempPath(), $"rig-impact-2store-{Guid.NewGuid():n}");
        Directory.CreateDirectory(wd);
        return wd;
    }

    // Materialize an analysis result into an indexed per-commit store (.rig/<storeId>/rig.db), under the given
    // store-id and (optionally) stamped with commit+branch provenance. storeId is exactly what --base/--head
    // resolve against (ResolveReadStoreDir). A null provenance leaves the store with no branch/commit — the
    // header then falls back to the store-id ref.
    private static async Task<string> MaterializeStoreAsync(
        string workingDirectory,
        AnalysisResult result,
        string storeId,
        GitProvenance? provenance
    )
    {
        var dir = StoreLayout.NewStoreDir(workingDirectory, storeId);
        var db = Path.Combine(dir, StoreLayout.DbFileName);
        await using var ctx = new RigDbContext(db, pooling: false);
        await Writes.SaveAsync(ctx, result, provenance: provenance);
        return storeId;
    }

    private static void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup; a held SQLite handle on a CI box must not fail the test.
        }
    }
}
