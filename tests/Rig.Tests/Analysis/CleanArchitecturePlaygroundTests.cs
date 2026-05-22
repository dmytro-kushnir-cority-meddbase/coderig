using System.Diagnostics;
using Shouldly;
using Rig.Analysis;

namespace Rig.Tests.Analysis;

public sealed class CleanArchitecturePlaygroundTests
{
    [Fact]
    public async Task Clean_architecture_playground_tracks_reconciled_entrypoints_and_effects()
    {
        var solutionPath = PlaygroundSolutionPath();
        await RestoreAsync(solutionPath);

        var result = await SolutionAnalyzer.AnalyzeAsync(solutionPath);

        result.EntryPoints.Select(entryPoint => entryPoint.DisplayName).ShouldBe(
            new[]
            {
                "fastendpoint DELETE /Contributors/{ContributorId:int}",
                "fastendpoint GET /Contributors",
                "fastendpoint GET /Contributors/{ContributorId:int}",
                "fastendpoint POST /Contributors",
                "fastendpoint PUT /Contributors/{ContributorId:int}"
            },
            ignoreOrder: true);

        result.Effects.Count().ShouldBe(24);

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "read" &&
            effect.Method == "ToListAsync" &&
            effect.Resource == "AppDbContext.Contributors");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "raw_sql" &&
            effect.Method == "ExecuteSqlInterpolatedAsync" &&
            effect.Resource == "AppDbContext.Database" &&
            effect.Observations.Any(observation => observation.Type == "looped_effect" && observation.Context == "for"));

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "schema" &&
            effect.Method == "EnsureCreatedAsync" &&
            effect.Resource == "AppDbContext.Database");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "schema" &&
            effect.Method == "MigrateAsync" &&
            effect.Resource == "AppDbContext.Database");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "repository" &&
            effect.Operation == "write" &&
            effect.Method == "AddAsync" &&
            effect.Resource == "Ardalis.SharedKernel.IRepository<T>");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "smtp" &&
            effect.Operation == "send" &&
            effect.Method == "SendAsync" &&
            effect.Resource == "MailKit.Net.Smtp.SmtpClient");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "mediatr" &&
            effect.Operation == "send" &&
            effect.Method == "Send" &&
            effect.Resource == "Clean.Architecture.UseCases.Contributors.List.ListContributorsQuery");

        result.Effects.ShouldContain(effect =>
            effect.Provider == "mediatr" &&
            effect.Operation == "publish" &&
            effect.Method == "Publish" &&
            effect.Resource == "Clean.Architecture.Core.ContributorAggregate.Events.ContributorDeletedEvent");

        result.Effects.ShouldNotContain(effect =>
            effect.Provider == "efcore" &&
            effect.Operation == "pending_write" &&
            effect.Method == "Add" &&
            effect.FilePath.EndsWith("MimeKitEmailSender.cs", StringComparison.OrdinalIgnoreCase));
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
            "CleanArchitecture",
            "Clean.Architecture.slnx"));
    }

    private static async Task RestoreAsync(string solutionPath)
    {
        var repositoryRoot = Path.GetDirectoryName(solutionPath)
            ?? throw new InvalidOperationException($"Could not resolve solution directory for {solutionPath}");

        await RunDotnetAsync(["restore", solutionPath], repositoryRoot);
    }

    private static async Task RunDotnetAsync(string[] arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start dotnet process.");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, output + Environment.NewLine + error);
    }
}
