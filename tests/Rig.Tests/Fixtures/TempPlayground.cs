using System.Diagnostics;
using Shouldly;

namespace Rig.Tests.Fixtures;

public sealed class TempPlayground : IDisposable
{
    private TempPlayground(string rootDirectory, string solutionPath, string workingDirectory)
    {
        RootDirectory = rootDirectory;
        SolutionPath = solutionPath;
        WorkingDirectory = workingDirectory;
    }

    public string RootDirectory { get; }

    public string SolutionPath { get; }

    public string WorkingDirectory { get; }

    public Task BuildAsync() => RunDotnetAsync(["build", SolutionPath, "--no-restore"], WorkingDirectory);

    public static Task<TempPlayground> CreateEntryPointEffectsAsync() =>
        CreateAsync("EntryPointEffects", "EntryPointEffects.slnx", "rig-entrypoint-effects-");

    public static Task<TempPlayground> CreateLegacyNet48Async() =>
        CreateAsync("LegacyNet48Web", "LegacyNet48Web.slnx", "rig-legacy-net48-");

    public static Task<TempPlayground> CreateCoreAllocationsAsync() =>
        CreateAsync("CoreAllocations", "CoreAllocations.slnx", "rig-core-allocations-");

    private static async Task<TempPlayground> CreateAsync(string playgroundName, string solutionFileName, string tempPrefix)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var sourceDirectory = Path.Combine(repositoryRoot, "playgrounds", playgroundName);
        var rootDirectory = Directory.CreateTempSubdirectory(tempPrefix).FullName;
        var targetDirectory = Path.Combine(rootDirectory, playgroundName);

        CopyDirectory(sourceDirectory, targetDirectory);

        var solutionPath = Path.Combine(targetDirectory, solutionFileName);
        await RunDotnetAsync(["restore", solutionPath], targetDirectory);

        return new TempPlayground(rootDirectory, solutionPath, targetDirectory);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(RootDirectory))
                {
                    Directory.Delete(RootDirectory, recursive: true);
                }

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

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);

        foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(targetDirectory, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory))
        {
            var name = Path.GetFileName(directory);
            if (name is "bin" or "obj" or ".vs")
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(targetDirectory, name));
        }
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
}
