using Shouldly;
using Rig.Cli;

namespace Rig.Tests.Cli;

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
        output.ToString().ShouldContain("rig index <solution>");
        output.ToString().ShouldContain("rig runs");
        output.ToString().ShouldContain("rig entrypoints");
        output.ToString().ShouldContain("rig callgraph <index>");
        output.ToString().ShouldContain("rig effects [--entrypoint <index>]");
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
    public async Task Index_then_entrypoints_and_effects_print_latest_playground_analysis()
    {
        var workingDirectory = Directory.CreateTempSubdirectory("rig-tests-").FullName;
        var solutionPath = PlaygroundSolutionPath();
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
        output.ToString().ShouldContain("EntryPoints: 8");
        output.ToString().ShouldContain("Effects: 23");
        File.Exists(Path.Combine(workingDirectory, ".rig", "rig.db")).ShouldBeTrue();

        output.GetStringBuilder().Clear();
        var runsExitCode = await CliApplication.RunAsync(["runs"], output, error, workingDirectory);

        runsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Runs");
        output.ToString().ShouldContain(solutionPath);
        output.ToString().ShouldContain("entrypoints=8 effects=23");
        output.ToString().ShouldContain("di=");

        output.GetStringBuilder().Clear();
        var entrypointsExitCode = await CliApplication.RunAsync(["entrypoints"], output, error, workingDirectory);

        entrypointsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("[  6] minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("minapi GET /minapi/teams/{id}");
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
        var callgraphExitCode = await CliApplication.RunAsync(["callgraph", "6"], output, error, workingDirectory);

        callgraphExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Callgraph: [6] minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("TeamWorkflow.LoadTeamSummaryAsync");
        output.ToString().ShouldContain("BillingClient.LoadInvoiceAsync");
        output.ToString().ShouldContain("BillingClient.LoadInvoicesAsync");
        output.ToString().ShouldContain("BOUNDARY external HttpClient.GetStringAsync");
        output.ToString().ShouldContain("EFFECT efcore read  ToListAsync  AppDbContext.Teams");
        output.ToString().ShouldContain("[looped_effect:foreach]");
        output.ToString().ShouldContain("[parallel_fanout:Task.WhenAll]");
    }

    private static string PlaygroundSolutionPath()
    {
        return Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "playgrounds",
            "EntryPointEffects",
            "EntryPointEffects.slnx"));
    }
}
