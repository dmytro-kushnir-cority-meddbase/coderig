using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// Per-project Central Package Management scoping (CpmClosure + its fold into BuildInputFingerprint): a
// `<PackageVersion>` bump in a central Directory.Packages.props must invalidate ONLY the projects that
// reference that package — not all under the props. The pure tests drive CpmClosure.ComputeMaterial /
// .Material from in-memory XML; the last test proves the scoping flows through the real fingerprint on disk.
public sealed class CpmClosureTests
{
    private const string Props = """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="Serilog" Version="3.0.1" />
            <PackageVersion Include="Dapper" Version="2.1.35" />
            <PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />
          </ItemGroup>
        </Project>
        """;

    private static string Csproj(params string[] packageRefs)
    {
        var items = string.Join("\n    ", packageRefs.Select(p => $"<PackageReference Include=\"{p}\" />"));
        return $"<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    {items}\n  </ItemGroup>\n</Project>";
    }

    // A version bump invalidates only the projects that reference the package; an unrelated project is stable.
    [Test]
    public void Bumping_a_central_version_only_changes_referencing_projects()
    {
        var serilogUser = CpmClosure.ComputeMaterial(Props, Csproj("Serilog"));
        var dapperUser = CpmClosure.ComputeMaterial(Props, Csproj("Dapper"));

        // Bump Serilog 3.0.1 -> 3.0.2.
        var bumped = Props.Replace("Serilog\" Version=\"3.0.1\"", "Serilog\" Version=\"3.0.2\"", StringComparison.Ordinal);
        CpmClosure.ComputeMaterial(bumped, Csproj("Serilog")).ShouldNotBe(serilogUser); // references Serilog -> changes
        CpmClosure.ComputeMaterial(bumped, Csproj("Dapper")).ShouldBe(dapperUser); // doesn't -> unchanged
    }

    // The folded material carries only the referenced package's version, not the others.
    [Test]
    public void Material_folds_only_referenced_versions()
    {
        var m = CpmClosure.ComputeMaterial(Props, Csproj("Serilog"));
        m.ShouldContain("serilog");
        m.ShouldNotContain("dapper");
        m.ShouldNotContain("newtonsoft.json");
    }

    // A change to the props' GLOBAL part (a property here) is folded wholesale → invalidates every CPM project.
    [Test]
    public void Global_props_change_invalidates_all_cpm_projects()
    {
        var withProp = Props.Replace(
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n    <NoWarn>NU1605</NoWarn>",
            StringComparison.Ordinal
        );
        CpmClosure.ComputeMaterial(withProp, Csproj("Serilog")).ShouldNotBe(CpmClosure.ComputeMaterial(Props, Csproj("Serilog")));
        CpmClosure.ComputeMaterial(withProp, Csproj("Dapper")).ShouldNotBe(CpmClosure.ComputeMaterial(Props, Csproj("Dapper")));
    }

    // With transitive pinning on, <PackageVersion> governs transitive deps too — the true closure is unknowable
    // from the props, so ALL versions are folded (conservative). A bump to a NON-referenced package then still
    // invalidates, unlike the default scoped case.
    [Test]
    public void Transitive_pinning_folds_all_versions()
    {
        var pinned = Props.Replace(
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>",
            "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n    <CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled>",
            StringComparison.Ordinal
        );
        var serilogUser = CpmClosure.ComputeMaterial(pinned, Csproj("Serilog"));
        serilogUser.ShouldContain("dapper"); // folded despite not being referenced (transitive pinning)

        var bumpDapper = pinned.Replace("Dapper\" Version=\"2.1.35\"", "Dapper\" Version=\"2.1.36\"", StringComparison.Ordinal);
        CpmClosure.ComputeMaterial(bumpDapper, Csproj("Serilog")).ShouldNotBe(serilogUser); // bump to a non-ref pkg invalidates
    }

    // End-to-end through the real fingerprint, in one CPM repo: bumping a package OUTSIDE a project's reference
    // set leaves its fingerprint identical; bumping one it references flips it.
    [Test]
    public void BuildInputFingerprint_scopes_cpm_invalidation_per_project()
    {
        var root = Directory.CreateTempSubdirectory("rig-cpm-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Directory.Packages.props"), Props);
            var projA = MakeProject(root, "ProjA", "Serilog");
            MakeProject(root, "ProjB", "Dapper");
            var fpA = BuildInputFingerprint.Compute(projA);

            // Bump Dapper (ProjA doesn't reference it) — ProjA unchanged. Distinct length keeps the parse memo
            // from reusing the prior props.
            File.WriteAllText(
                Path.Combine(root, "Directory.Packages.props"),
                Props.Replace("Dapper\" Version=\"2.1.35\"", "Dapper\" Version=\"2.1.36\"", StringComparison.Ordinal)
            );
            BuildInputFingerprint.Compute(projA).ShouldBe(fpA);

            // Bump Serilog (ProjA references it) — ProjA invalidated.
            File.WriteAllText(
                Path.Combine(root, "Directory.Packages.props"),
                Props.Replace("Serilog\" Version=\"3.0.1\"", "Serilog\" Version=\"3.0.999\"", StringComparison.Ordinal)
            );
            BuildInputFingerprint.Compute(projA).ShouldNotBe(fpA);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException) { }
        }
    }

    private static string MakeProject(string root, string name, params string[] packageRefs)
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var csproj = Path.Combine(dir, $"{name}.csproj");
        File.WriteAllText(csproj, Csproj(packageRefs));
        return csproj;
    }
}
