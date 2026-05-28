using Rig.Cli;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

[Collection(RoslynIntegrationCollection.Name)]
public sealed class CliApplicationTests
{
    [Fact]
    public async Task No_arguments_prints_human_readable_command_summary()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync([], output, error);

        exitCode.ShouldBe(0);
        error.ToString().ShouldBeEmpty();
        output.ToString().ShouldContain("Runtime Intelligence Graph");
        output.ToString().ShouldContain("Usage:");
        output.ToString().ShouldContain("rig index <solution|project>");
        output.ToString().ShouldContain("rig runs");
        output.ToString().ShouldContain("rig entrypoints");
        output.ToString().ShouldContain("rig callgraph <index>");
        output.ToString().ShouldContain("rig effects [--entrypoint <index>]");
        output.ToString().ShouldContain("rig trace <symbol> [--paths]");
        output.ToString().ShouldContain("rig trace --contains <text> [--paths]");
        output.ToString().ShouldContain("rig files --skipped");
        output.ToString().ShouldContain("rig profile validate");
    }

    [Fact]
    public async Task Unknown_command_fails_with_actionable_error()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["wat"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Unknown command: wat");
    }

    [Fact]
    public async Task Effects_rejects_non_numeric_entrypoint_index()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["effects", "--entrypoint", "wat"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Invalid entrypoint index.");
        error.ToString().ShouldContain("Usage: rig effects --entrypoint <index>");
    }

    [Fact]
    public async Task Callgraph_rejects_missing_or_invalid_entrypoint_index()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["callgraph", "wat"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Missing or invalid entrypoint index.");
        error.ToString().ShouldContain("Usage: rig callgraph <index> [--full] [--summary]");
    }

    [Fact]
    public async Task Files_requires_skipped_flag()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["files"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Usage: rig files --skipped");
    }

    [Fact]
    public async Task Trace_requires_symbol_or_contains_query()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(["trace"], output, error);

        exitCode.ShouldBe(2);
        output.ToString().ShouldBeEmpty();
        error.ToString().ShouldContain("Usage: rig trace <symbol> [--paths]");
        error.ToString().ShouldContain("Usage: rig trace --contains <text> [--paths]");
    }

    [Fact]
    public async Task Index_then_entrypoints_and_effects_print_latest_playground_analysis()
    {
        using var playground = await TempPlayground.CreateEntryPointEffectsAsync();
        var workingDirectory = Path.Combine(playground.RootDirectory, "workspace");
        var solutionPath = playground.SolutionPath;
        var output = new StringWriter();
        var error = new StringWriter();

        var indexExitCode = await CliApplication.RunAsync(["index", solutionPath], output, error, workingDirectory);

        indexExitCode.ShouldBe(0);
        error.ToString().ShouldBeEmpty();
        output.ToString().ShouldContain($"Indexing: {Path.GetFullPath(solutionPath)}");
        output.ToString().ShouldContain("Progress: Loading rules");
        output.ToString().ShouldContain("Progress: Loading solution");
        output.ToString().ShouldContain("Progress: MSBuild");
        output.ToString().ShouldContain("Progress: Loaded");
        output.ToString().ShouldContain("Progress: Extracting observations");
        output.ToString().ShouldContain("Progress: Building callgraphs");
        output.ToString().ShouldContain("Progress: Saving run");
        output.ToString().ShouldContain("Run:");
        output.ToString().ShouldContain("EntryPoints: 11");
        output.ToString().ShouldContain("Effects: 23");
        File.Exists(Path.Combine(workingDirectory, ".rig", "rig.db")).ShouldBeTrue();

        output.GetStringBuilder().Clear();
        var runsExitCode = await CliApplication.RunAsync(["runs"], output, error, workingDirectory);

        runsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Runs");
        output.ToString().ShouldContain(solutionPath);
        output.ToString().ShouldContain("entrypoints=11 effects=23");
        output.ToString().ShouldContain("di=");

        output.GetStringBuilder().Clear();
        var entrypointsExitCode = await CliApplication.RunAsync(["entrypoints"], output, error, workingDirectory);

        entrypointsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("[  6] minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("minapi GET /minapi/cycles/self");
        output.ToString().ShouldContain("minapi GET /minapi/cycles/mutual");
        output.ToString().ShouldContain("minapi GET /minapi/cycles/three-step");
        output.ToString().ShouldContain("mvc POST api/teams");
        output.ToString().ShouldContain("mvc GET api/teams/via-interface");
        output.ToString().ShouldContain("fastendpoint POST /fastendpoints/teams");

        output.GetStringBuilder().Clear();
        var effectsExitCode = await CliApplication.RunAsync(["effects"], output, error, workingDirectory);

        effectsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("http GET  GetStringAsync  billing.example/invoices/{teamId}");
        output.ToString().ShouldContain("efcore read  ToListAsync  AppDbContext.Teams");
        output.ToString().ShouldContain("efcore commit  SaveChangesAsync  AppDbContext");
        output.ToString().ShouldContain("efcore schema  EnsureCreatedAsync  AppDbContext.Database");
        output.ToString().ShouldContain("efcore raw_sql  ExecuteSqlInterpolatedAsync  AppDbContext.Database");
        output.ToString().ShouldContain("redis read  StringGetAsync  team:{teamId}");
        output.ToString().ShouldContain("redis write  StringSetAsync  team:{name}");
        output.ToString().ShouldContain("smtp send  SendAsync  MailKit.Net.Smtp.SmtpClient");
        output.ToString().ShouldContain("repository write  AddAsync  Ardalis.SharedKernel.IRepository<T>");
        output.ToString().ShouldContain("[looped_effect:foreach]");
        output.ToString().ShouldContain("[parallel_fanout:Task.WhenAll]");
        output.ToString().ShouldContain("[parallel_fanout:Parallel.ForEach]");
        output.ToString().ShouldContain("[parallel_fanout:Parallel.ForEachAsync]");

        output.GetStringBuilder().Clear();
        var diExitCode = await CliApplication.RunAsync(["di"], output, error, workingDirectory);

        diExitCode.ShouldBe(0);
        output.ToString().ShouldContain("DI Registrations");
        output.ToString().ShouldContain("global::EntryPointEffects.Api.Services.ITeamRepository");
        output.ToString().ShouldContain("-> global::EntryPointEffects.Api.Services.TeamRepository");
        output.ToString().ShouldContain("evidence=method=AddScoped project=EntryPointEffects.Api");

        output.GetStringBuilder().Clear();
        var filesExitCode = await CliApplication.RunAsync(["files", "--skipped"], output, error, workingDirectory);

        filesExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Skipped Files");
        output.ToString().ShouldContain("GeneratedEndpoint.g.cs");
        output.ToString().ShouldContain("basis=profile");
        output.ToString().ShouldContain("reason=generated_fixture");

        output.GetStringBuilder().Clear();
        var callgraphExitCode = await CliApplication.RunAsync(["callgraph", "6", "--full"], output, error, workingDirectory);

        callgraphExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Callgraph: [6] minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("TeamWorkflow.LoadTeamSummaryAsync");
        output.ToString().ShouldContain("BillingClient.LoadInvoiceAsync");
        output.ToString().ShouldContain("BillingClient.LoadInvoicesAsync");
        output.ToString().ShouldContain("EFFECT http GET  GetStringAsync  billing.example/invoices/{teamId}");
        output.ToString().ShouldContain("EFFECT efcore read  ToListAsync  AppDbContext.Teams");
        output.ToString().ShouldContain("[looped_effect:foreach]");
        output.ToString().ShouldContain("[parallel_fanout:Task.WhenAll]");

        output.GetStringBuilder().Clear();
        var cycleCallgraphExitCode = await CliApplication.RunAsync(["callgraph", "9", "--full"], output, error, workingDirectory);

        cycleCallgraphExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Callgraph: [9] minapi GET /minapi/cycles/mutual");
        output.ToString().ShouldContain("Cycles: 1");
        output.ToString().ShouldContain("CYCLE CycleFixture.MutualA -> CycleFixture.MutualB -> CycleFixture.MutualA");
        output.ToString().ShouldContain("[cycle]");

        output.GetStringBuilder().Clear();
        var traceExitCode = await CliApplication.RunAsync(
            ["trace", "--contains", "TeamWorkflow.LoadTeamSummaryAsync", "--paths"],
            output,
            error,
            workingDirectory
        );

        traceExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Trace: global::EntryPointEffects.Api.Services.TeamWorkflow.LoadTeamSummaryAsync");
        output.ToString().ShouldContain("Run:");
        output.ToString().ShouldContain("Reached by entrypoints");
        output.ToString().ShouldContain("mvc GET api/teams/{id}");
        output.ToString().ShouldContain("minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("Paths");
        output.ToString().ShouldContain("Upstream");
        output.ToString().ShouldContain("Downstream");
        output.ToString().ShouldContain("TeamWorkflow.LoadTeamSummaryAsync");
    }
}
