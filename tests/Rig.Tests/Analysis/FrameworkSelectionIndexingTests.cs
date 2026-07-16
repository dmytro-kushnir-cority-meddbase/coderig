using System.Diagnostics;
using Rig.Analysis;
using Rig.Analysis.Inventory;
using Rig.Analysis.Rules;
using Rig.Cli;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class FrameworkSelectionIndexingTests
{
    [Test]
    public async Task Explicit_framework_indexes_only_that_tfms_conditional_symbols()
    {
        var directory = Directory.CreateTempSubdirectory("rig-framework-selection-").FullName;
        try
        {
            var projectPath = Path.Combine(directory, "MultiTarget.csproj");
            await File.WriteAllTextAsync(
                projectPath,
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                  </PropertyGroup>
                  <ItemGroup Condition="$([MSBuild]::IsTargetFrameworkCompatible('$(TargetFramework)', 'net8.0'))">
                    <PackageReference Include="System.IO.Pipelines" Version="8.0.0" />
                  </ItemGroup>
                </Project>
                """
            );
            await File.WriteAllTextAsync(
                Path.Combine(directory, "ConditionalTypes.cs"),
                """
                namespace FrameworkFixture;

                #if NET8_0
                public sealed class NetEightOnly;
                #endif

                #if NET10_0
                public sealed class NetTenOnly;
                #endif
                """
            );
            await RunDotnetAsync(
                ["restore", projectPath, "--force-evaluate", "-p:TreatWarningsAsErrors=false"],
                directory
            );

            var rules = RuleSetLoader.Load(directory);
            var cacheDirectory = Path.Combine(directory, "build-cache");

            var netEight = await SolutionAnalyzer.AnalyzeAsync(
                projectPath,
                rules,
                buildCacheDir: cacheDirectory,
                framework: "net8.0"
            );
            var netTen = await SolutionAnalyzer.AnalyzeAsync(
                projectPath,
                rules,
                buildCacheDir: cacheDirectory,
                framework: "net10.0"
            );

            var netEightSymbols = netEight.Symbols ?? [];
            var netTenSymbols = netTen.Symbols ?? [];
            netEightSymbols.ShouldContain(symbol => symbol.SymbolId == "T:FrameworkFixture.NetEightOnly");
            netEightSymbols.ShouldNotContain(symbol => symbol.SymbolId == "T:FrameworkFixture.NetTenOnly");
            netTenSymbols.ShouldContain(symbol => symbol.SymbolId == "T:FrameworkFixture.NetTenOnly");
            netTenSymbols.ShouldNotContain(symbol => symbol.SymbolId == "T:FrameworkFixture.NetEightOnly");

            Directory.EnumerateFiles(cacheDirectory, "*.json").Count().ShouldBe(2);
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public void Requested_framework_must_be_declared_by_a_multi_targeted_project()
    {
        var exception = Should.Throw<InvalidOperationException>(() =>
            SolutionSourceLoader.SelectFramework("App", ["net8.0", "net10.0"], "net9.0")
        );

        exception.Message.ShouldContain("does not target requested framework 'net9.0'");
        exception.Message.ShouldContain("net8.0, net10.0");
    }

    [Test]
    public async Task Index_command_accepts_framework_option()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = await CliApplication.RunAsync(
            ["index", "C:/does-not-exist.slnx", "--framework", "net10.0"],
            output,
            error
        );

        exitCode.ShouldBe(2);
        error.ToString().ShouldNotContain("Unrecognized command or argument");
        error.ToString().ShouldContain("Failed to load");
    }

    private static async Task RunDotnetAsync(string[] arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start dotnet process.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        process.ExitCode.ShouldBe(0, output + Environment.NewLine + error);
    }

    private static void DeleteDirectory(string directory)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                Thread.Sleep(100);
            }
            catch (IOException)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }
}
