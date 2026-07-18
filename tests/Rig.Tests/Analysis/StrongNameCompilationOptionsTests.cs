using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Rig.Analysis;
using Rig.Analysis.Inventory;
using Rig.Analysis.Rules;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class StrongNameCompilationOptionsTests
{
    [Test]
    public void Propagates_relative_key_and_signing_flags_from_msbuild()
    {
        var directory = Directory.CreateTempSubdirectory("rig-signing-options-").FullName;
        try
        {
            var projectPath = Path.Combine(directory, "Signed.csproj");
            var info = new ProjectBuildInfo(
                ProjectFilePath: projectPath,
                References: [],
                ProjectReferences: [],
                SourceFiles: [],
                AnalyzerReferences: [],
                PreprocessorSymbols: [],
                Properties: new Dictionary<string, string>
                {
                    ["SignAssembly"] = "true",
                    ["AssemblyOriginatorKeyFile"] = Path.Combine("keys", "fixture.snk"),
                    ["DelaySign"] = "true",
                    ["PublicSign"] = "true",
                }
            );

            var options = SolutionSourceLoader.BuildCompilationOptions(info);

            options.CryptoKeyFile.ShouldBe(Path.Combine(directory, "keys", "fixture.snk"));
            options.DelaySign.ShouldBe(true);
            options.PublicSign.ShouldBeTrue();
            options.StrongNameProvider.ShouldBeOfType<DesktopStrongNameProvider>();
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Test]
    public async Task Signed_friend_project_binds_internal_call_without_public_key_mismatch()
    {
        var directory = Directory.CreateTempSubdirectory("rig-signed-friend-").FullName;
        try
        {
            var keyPath = Path.Combine(directory, "friend.snk");
            using (var rsa = new RSACryptoServiceProvider(1024))
            {
                rsa.PersistKeyInCsp = false;
                await File.WriteAllBytesAsync(keyPath, rsa.ExportCspBlob(includePrivateParameters: true));
            }

            var producerDirectory = Directory.CreateDirectory(Path.Combine(directory, "SignedProducer")).FullName;
            var consumerDirectory = Directory.CreateDirectory(Path.Combine(directory, "SignedConsumer")).FullName;
            var producerProject = Path.Combine(producerDirectory, "SignedProducer.csproj");
            var consumerProject = Path.Combine(consumerDirectory, "SignedConsumer.csproj");

            await File.WriteAllTextAsync(producerProject, SignedProject("SignedProducer"));
            await File.WriteAllTextAsync(
                Path.Combine(producerDirectory, "InternalApi.cs"),
                "namespace SignedProducer; internal static class InternalApi { internal static int Read() => 42; }"
            );

            // Build once to read the real strong-name public key, then use that exact identity in IVT.
            await RunDotnetAsync(["build", producerProject, "--nologo"], directory);
            var producerAssembly = Path.Combine(producerDirectory, "bin", "Debug", "net10.0", "SignedProducer.dll");
            var publicKey = Convert.ToHexString(AssemblyName.GetAssemblyName(producerAssembly).GetPublicKey()!).ToLowerInvariant();
            await File.WriteAllTextAsync(
                Path.Combine(producerDirectory, "Friend.cs"),
                $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"SignedConsumer, PublicKey={publicKey}\")]"
            );

            await File.WriteAllTextAsync(
                consumerProject,
                SignedProject("SignedConsumer", "<ProjectReference Include=\"..\\SignedProducer\\SignedProducer.csproj\" />")
            );
            await File.WriteAllTextAsync(
                Path.Combine(consumerDirectory, "Consumer.cs"),
                "namespace SignedConsumer; public static class Consumer { public static int Read() => SignedProducer.InternalApi.Read(); }"
            );

            var solutionPath = Path.Combine(directory, "SignedFriend.slnx");
            await File.WriteAllTextAsync(
                solutionPath,
                "<Solution><Project Path=\"SignedProducer/SignedProducer.csproj\" /><Project Path=\"SignedConsumer/SignedConsumer.csproj\" /></Solution>"
            );
            await RunDotnetAsync(["build", solutionPath, "--nologo"], directory);

            var progress = new List<string>();
            var result = await SolutionAnalyzer.AnalyzeAsync(
                solutionPath,
                RuleSetLoader.Load(directory),
                progress: progress.Add,
                parallelism: 1
            );

            progress.ShouldNotContain(message => message.Contains("CS0281", StringComparison.Ordinal));
            progress.ShouldNotContain(message => message.Contains("compilation error", StringComparison.OrdinalIgnoreCase));
            result.References!.ShouldContain(reference =>
                reference.RefKind == "invocation"
                && reference.TargetSymbolId == "M:SignedProducer.InternalApi.Read"
                && reference.TargetInSource
            );
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    private static string SignedProject(string assemblyName, string item = "") =>
        $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{{assemblyName}}</AssemblyName>
                <SignAssembly>true</SignAssembly>
                <AssemblyOriginatorKeyFile>..\friend.snk</AssemblyOriginatorKeyFile>
              </PropertyGroup>
              <ItemGroup>{{item}}</ItemGroup>
            </Project>
            """;

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
