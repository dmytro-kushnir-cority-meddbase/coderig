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
        output.ToString().ShouldContain("rig callgraph <entrypoint-id>");
        output.ToString().ShouldContain("rig effects --entrypoint <entrypoint-id>");
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
        output.ToString().ShouldContain("Run:");
        output.ToString().ShouldContain("EntryPoints: 5");
        output.ToString().ShouldContain("Effects: 19");
        File.Exists(Path.Combine(workingDirectory, ".rig", "rig.db")).ShouldBeTrue();

        output.GetStringBuilder().Clear();
        var runsExitCode = await CliApplication.RunAsync(["runs"], output, error, workingDirectory);

        runsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Runs");
        output.ToString().ShouldContain(solutionPath);
        output.ToString().ShouldContain("entrypoints=5 effects=19");
        output.ToString().ShouldContain("di=");

        output.GetStringBuilder().Clear();
        var entrypointsExitCode = await CliApplication.RunAsync(["entrypoints"], output, error, workingDirectory);

        entrypointsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("mvc POST api/teams");
        output.ToString().ShouldContain("fastendpoint POST /fastendpoints/teams");

        output.GetStringBuilder().Clear();
        var effectsExitCode = await CliApplication.RunAsync(["effects"], output, error, workingDirectory);

        effectsExitCode.ShouldBe(0);
        output.ToString().ShouldContain("http GET billing.example/invoices/{teamId}");
        output.ToString().ShouldContain("efcore read AppDbContext.Teams");
        output.ToString().ShouldContain("efcore commit AppDbContext");
        output.ToString().ShouldContain("efcore schema AppDbContext.Database");
        output.ToString().ShouldContain("efcore raw_sql AppDbContext.Database");
        output.ToString().ShouldContain("redis read team:{teamId}");
        output.ToString().ShouldContain("redis write team:{name}");
        output.ToString().ShouldContain("smtp send MailKit.Net.Smtp.SmtpClient");
        output.ToString().ShouldContain("repository write Ardalis.SharedKernel.IRepository<T>");
        output.ToString().ShouldContain("OBS looped_effect ctx=foreach");
        output.ToString().ShouldContain("OBS parallel_fanout ctx=Task.WhenAll");
        output.ToString().ShouldContain("OBS parallel_fanout ctx=Parallel.ForEach");
        output.ToString().ShouldContain("OBS parallel_fanout ctx=Parallel.ForEachAsync");

        output.GetStringBuilder().Clear();
        var filesExitCode = await CliApplication.RunAsync(["files", "--skipped"], output, error, workingDirectory);

        filesExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Skipped Files");
        output.ToString().ShouldContain("GeneratedEndpoint.g.cs");
        output.ToString().ShouldContain("basis=profile");
        output.ToString().ShouldContain("reason=generated_fixture");

        output.GetStringBuilder().Clear();
        var callgraphExitCode = await CliApplication.RunAsync(["callgraph", "minapi GET /minapi/teams/{id}"], output, error, workingDirectory);

        callgraphExitCode.ShouldBe(0);
        output.ToString().ShouldContain("Callgraph: minapi GET /minapi/teams/{id}");
        output.ToString().ShouldContain("TeamWorkflow.LoadTeamSummaryAsync");
        output.ToString().ShouldContain("BillingClient.LoadInvoiceAsync");
        output.ToString().ShouldContain("BillingClient.LoadInvoicesAsync");
        output.ToString().ShouldContain("BOUNDARY external HttpClient.GetStringAsync");
        output.ToString().ShouldContain("EFFECT efcore read AppDbContext.Teams");
        output.ToString().ShouldContain("OBS looped_effect ctx=foreach");
        output.ToString().ShouldContain("OBS parallel_fanout ctx=Task.WhenAll");
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
