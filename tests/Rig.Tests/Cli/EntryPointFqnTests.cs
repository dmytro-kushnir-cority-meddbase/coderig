using Rig.Analysis;
using Rig.Cli;
using Rig.Cli.CommandLine;
using Rig.Cli.Deployments;
using Rig.Cli.EntryPoints;
using Rig.Cli.Rendering;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// The EP listings (`rig derive` / `entrypoints` / `callers --entrypoints`) render the slash-form EP ROUTE
// (e.g. "Account/Public/Login"), which matches NOTHING as a `rig tree`/`reaches`/`callers` pattern — so the
// output couldn't be acted on without a separate `rig symbols` lookup. These tests pin the fix: each EP line
// now also carries the method's fully-qualified dotted name (the queryable handle that round-trips), in both
// the human (`↪ <fqn>` line) and tsv (trailing column) forms, reusing impact's FqnForCard/StripParams form.
// The FQN is suppressed when it equals the route (class-inheritance EPs, whose route already IS the FQN) and
// falls back to the route when the EP's site maps to no indexed method (ctor-less pages, promoted handoffs).
public sealed class EntryPointFqnTests
{
    // The pure DocID -> queryable FQN reduction shared by every EP listing AND the impact EP card: strip the
    // leading `M:` kind prefix and the parameter list, leave everything else (incl. generic arity, so the name
    // still round-trips into a pattern). Non-`M:` ids and empties pass through.
    [Test]
    public void FqnFromDocId_strips_M_prefix_and_param_list()
    {
        SymbolNameFormatter
            .FqnFromDocId("M:MedDBase.Pages.Appointment.Search.Search.Proceed")
            .ShouldBe("MedDBase.Pages.Appointment.Search.Search.Proceed");
        SymbolNameFormatter
            .FqnFromDocId("M:MedDBase.Pages.Patient.Medical.ObservationRequest.Save(System.Boolean)")
            .ShouldBe("MedDBase.Pages.Patient.Medical.ObservationRequest.Save");
        // Generic arity is preserved — it is part of the DocID, so keeping it is what makes the name a valid
        // substring pattern (the arity-stripped route would not match the DocID).
        SymbolNameFormatter
            .FqnFromDocId("M:LegacyNet48Web.Pages.Workflows.WorkflowPaneBase`1.Save")
            .ShouldBe("LegacyNet48Web.Pages.Workflows.WorkflowPaneBase`1.Save");
        SymbolNameFormatter.FqnFromDocId("").ShouldBe("");
        SymbolNameFormatter.FqnFromDocId(null).ShouldBe("");
    }

    // MethodDocIdBySite indexes the method-symbol set by (file,line); FqnOrRoute resolves an EP's site to its
    // dotted FQN, else falls back to the route when the site maps to no method (ctor-less page / promoted EP).
    [Test]
    public void FqnOrRoute_resolves_site_to_fqn_else_falls_back_to_route()
    {
        var epData = new FactEntryPointDeriver.FactEntryPointData(
            BaseEdges: [],
            Methods:
            [
                new MethodSymbol(
                    SymbolId: "M:LegacyNet48Web.Pages.Accounts.InvoiceMain.SubmitInvoice",
                    Name: "SubmitInvoice",
                    ContainingSymbolId: "T:LegacyNet48Web.Pages.Accounts.InvoiceMain",
                    Signature: "SubmitInvoice()",
                    FilePath: "C:/repo/Pages/Accounts/InvoiceMain.cs",
                    Line: 12,
                    IsOverride: false
                ),
            ],
            Types: [],
            CtorRefs: []
        );

        var bySite = EntryPointContext.MethodDocIdBySite(epData);

        // Site hit -> the dotted FQN (NOT the slash route).
        EntryPointContext
            .FqnOrRoute("Accounts/InvoiceMain.SubmitInvoice", "C:/repo/Pages/Accounts/InvoiceMain.cs", 12, bySite)
            .ShouldBe("LegacyNet48Web.Pages.Accounts.InvoiceMain.SubmitInvoice");
        // No site (empty file) -> route fallback.
        EntryPointContext.FqnOrRoute("background/Some/Route.Tick", "", 0, bySite).ShouldBe("background/Some/Route.Tick");
        // File present but no (file,line) match -> route fallback.
        EntryPointContext
            .FqnOrRoute("Accounts/TermsAndConditions", "C:/repo/Pages/Accounts/TermsAndConditions.cs", 9, bySite)
            .ShouldBe("Accounts/TermsAndConditions");
    }

    // The renderer adds the `↪ <fqn>` line ONLY when the fqn is supplied and differs from the route — so a
    // route≠FQN EP gains its queryable handle, while an EP whose route already IS the FQN (or a caller that
    // passes no fqn) renders exactly as before (no redundant line).
    [Test]
    public void WriteEntryPointLine_emits_fqn_line_only_when_it_differs_from_route()
    {
        // route ≠ fqn -> the ↪ line appears.
        var differs = Render(route: "Accounts/InvoiceMain.SubmitInvoice", fqn: "LegacyNet48Web.Pages.Accounts.InvoiceMain.SubmitInvoice");
        differs.ShouldContain("Accounts/InvoiceMain.SubmitInvoice");
        differs.ShouldContain("↪ LegacyNet48Web.Pages.Accounts.InvoiceMain.SubmitInvoice");

        // route == fqn (class-inheritance EP) -> no redundant ↪ line.
        var same = Render(route: "LegacyNet48Web.App.Worker.Run", fqn: "LegacyNet48Web.App.Worker.Run");
        same.ShouldNotContain("↪");

        // fqn null (non-opted caller / unchanged behaviour) -> no ↪ line.
        Render(route: "Accounts/InvoiceMain.SubmitInvoice", fqn: null).ShouldNotContain("↪");

        static string Render(string route, string? fqn)
        {
            var sw = new StringWriter();
            EntryPointListRenderer.WriteEntryPointLine(
                sw,
                DeploymentMap.Empty,
                route: route,
                filePath: "C:/repo/Pages/Accounts/InvoiceMain.cs",
                line: 12,
                requires: null,
                fqn: fqn
            );
            return sw.ToString();
        }
    }

    // End-to-end over a store with page/action (slash-route) EPs (the LegacyNet48 playground analyzed
    // in-process, the same path the impact two-store test uses — the CLI `index` build trips the playground's
    // proxy source-generator, so we materialize from the analyzer result instead). `rig entrypoints` now
    // prints the dotted, queryable FQN alongside the slash route, in both human (`↪` line) and tsv (trailing
    // column). The `↪` marker appears only on a rendered FQN line, so its presence proves the handle is there.
    [Test]
    public async Task Entrypoints_command_renders_queryable_fqn_for_slash_route_pages()
    {
        using var playgrounds = new AnalyzedPlaygrounds();
        var legacy = await playgrounds.LegacyNet48Async();

        var workingDirectory = Path.Combine(Path.GetTempPath(), $"rig-epfqn-{Guid.NewGuid():n}");
        Directory.CreateDirectory(workingDirectory);
        try
        {
            var storeId = await MaterializeStoreAsync(workingDirectory, legacy.Result, "cccccccccccc0000fqn", "fqn-test");
            var rulesPath = Path.Combine(legacy.WorkingDirectory, "rig.rules.json");
            var output = new StringWriter();
            var error = new StringWriter();

            // --- Human: the slash-form page route is still listed, and a `↪ <dotted FQN>` line carries the
            //     queryable handle. The route "Account/Public/Login" never contains "LegacyNet48Web.Pages.",
            //     and the `↪` marker is emitted only on an FQN line — so this pins "the FQN is rendered". ---
            (
                await CliApplication.RunAsync(["entrypoints", "--rules", rulesPath, "--store", storeId], output, error, workingDirectory)
            ).ShouldBe(0);
            var human = output.ToString();
            human.ShouldContain("Account/Public/Login"); // the slash-form route is still listed
            human.ShouldContain("↪ LegacyNet48Web.Pages."); // a queryable dotted FQN line was rendered

            // --- TSV: the trailing column is the dotted FQN — for at least one slash-route EP it is the
            //     queryable dotted name, distinct from the slash route (ctor-less pages fall back to the route,
            //     so we assert existence, not universality). ---
            output.GetStringBuilder().Clear();
            (
                await CliApplication.RunAsync(
                    ["entrypoints", "--rules", rulesPath, "--store", storeId, "--format", "tsv"],
                    output,
                    error,
                    workingDirectory
                )
            ).ShouldBe(0);
            var rows = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split('\t')).ToList();

            var slashRouteRows = rows.Where(c => c.Length >= 8 && c[0] is "page" or "action" && c[1].Contains('/')).ToList();
            slashRouteRows.ShouldNotBeEmpty();
            // At least one slash-route EP carries a dotted, queryable FQN in the trailing column, ≠ its route.
            // (Index from Length-1, not ^1 — Shouldly's predicate is an expression tree, which bars ^/Range.)
            slashRouteRows.ShouldContain(c =>
                c[c.Length - 1].StartsWith("LegacyNet48Web.Pages.", StringComparison.Ordinal)
                && !c[c.Length - 1].Contains('/')
                && c[c.Length - 1] != c[1]
            );
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException) { }
        }
    }

    // Mirror of CliApplicationTests.MaterializeStoreAsync (private there): write an in-process AnalysisResult
    // to a real per-commit store, addressable by the returned store id via `--store`.
    private static async Task<string> MaterializeStoreAsync(string workingDirectory, AnalysisResult result, string commit, string branch)
    {
        var provenance = new GitProvenance(Commit: commit, Branch: branch, Dirty: false);
        var storeId = StoreLayout.NewStoreId(provenance);
        var db = Path.Combine(StoreLayout.NewStoreDir(workingDirectory, storeId), StoreLayout.DbFileName);
        await using var ctx = new RigDbContext(db, pooling: false);
        await Writes.SaveAsync(ctx, result, provenance: provenance);
        return storeId;
    }
}
