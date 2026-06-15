using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// A C# project that references an F#/VB project hits CS0012 ("type defined in an assembly that is not
// referenced") because the C# workspace can't compile the non-C# project — the fix adds the referenced
// project's BUILT OUTPUT DLL as a metadata reference instead. These cover the csproj-XML parsing + DLL
// resolution that fix relies on (the live end-to-end is verified by a re-mine).
public sealed class NonCSharpProjectReferenceTests
{
    [Test]
    public void Resolves_fsharp_project_reference_to_its_built_output_dll_and_ignores_csharp_refs()
    {
        var root = Directory.CreateTempSubdirectory("rig-fsref-").FullName;
        try
        {
            // F# dependency with a built Release output DLL.
            var fsDir = Path.Combine(root, "Lib");
            Directory.CreateDirectory(fsDir);
            var fsproj = Path.Combine(fsDir, "Lib.fsproj");
            File.WriteAllText(
                fsproj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><AssemblyName>Lib.FSharp</AssemblyName></PropertyGroup></Project>"
            );
            var outDir = Path.Combine(fsDir, "bin", "Release", "netstandard2.0");
            Directory.CreateDirectory(outDir);
            var dll = Path.Combine(outDir, "Lib.FSharp.dll");
            File.WriteAllText(dll, "");

            // A C# sibling reference that must be ignored (it's loaded as a live ProjectReference).
            var csDepDir = Path.Combine(root, "Other");
            Directory.CreateDirectory(csDepDir);
            File.WriteAllText(Path.Combine(csDepDir, "Other.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");

            // The consuming C# project references both.
            var appDir = Path.Combine(root, "App");
            Directory.CreateDirectory(appDir);
            var appProj = Path.Combine(appDir, "App.csproj");
            File.WriteAllText(
                appProj,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
                    + "<ProjectReference Include=\"..\\Lib\\Lib.fsproj\" />"
                    + "<ProjectReference Include=\"..\\Other\\Other.csproj\" />"
                    + "</ItemGroup></Project>"
            );

            var dlls = SolutionSourceLoader.NonCSharpProjectReferenceDlls(appProj).ToList();

            dlls.Count.ShouldBe(1);
            dlls[0].ShouldBe(dll);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Built_output_resolution_prefers_release_over_debug()
    {
        var root = Directory.CreateTempSubdirectory("rig-fsout-").FullName;
        try
        {
            var fsproj = Path.Combine(root, "Lib.fsproj");
            // No <AssemblyName> -> defaults to the project filename.
            File.WriteAllText(fsproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            foreach (var config in new[] { "Debug", "Release" })
            {
                var d = Path.Combine(root, "bin", config, "netstandard2.0");
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "Lib.dll"), "");
            }

            var resolved = SolutionSourceLoader.ResolveBuiltOutputDll(fsproj).ShouldNotBeNull();
            resolved.ShouldContain("Release");
            resolved.ShouldEndWith("Lib.dll");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
