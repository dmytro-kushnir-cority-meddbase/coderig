using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// Per-project Paket invalidation scoping (PaketClosure + its fold into BuildInputFingerprint). The contract:
// a paket.lock / paket.dependencies version bump must invalidate ONLY the projects whose transitive closure
// actually resolves the changed package — not all ~300, as the old whole-file hash did. These build tiny
// synthetic repos (root lock + dependencies, per-project paket.references) and assert the closure material
// changes for exactly the affected projects. PaketClosure.Compute returns CONTENT-derived material (no file
// paths), so two repos with identical structure yield identical material — letting us compare across fresh
// dirs without the project-path term BuildInputFingerprint folds.
public sealed class PaketClosureTests
{
    // ProjA -> PkgA -> PkgCommon (transitive);  ProjB -> PkgB (standalone). PkgCommon is in A's closure only.
    private const string LockBase = """
        NUGET
          remote: https://api.nuget.org/v3/index.json
            PkgA (1.0)
              PkgCommon (>= 1.0)
            PkgB (2.0)
            PkgCommon (1.0)
        """;

    private const string DepsBase = """
        source https://api.nuget.org/v3/index.json
        framework: net8.0
        nuget PkgA 1.0
        nuget PkgB 2.0
        nuget PkgCommon 1.0
        """;

    // A bump to PkgB (in ProjB's closure, NOT ProjA's) invalidates ProjB only; a bump to the transitive
    // PkgCommon (in ProjA's closure, NOT ProjB's) invalidates ProjA only. The whole point of the change.
    [Test]
    public void Bumping_a_package_only_changes_the_closures_that_contain_it()
    {
        using var basis = new Repo(LockBase, DepsBase);

        // Bump PkgB 2.0 -> 2.0.1 (length differs too — irrelevant here, content-keyed).
        using var bumpB = new Repo(LockBase.Replace("PkgB (2.0)", "PkgB (2.0.1)", StringComparison.Ordinal), DepsBase);
        bumpB.Closure("ProjA").ShouldBe(basis.Closure("ProjA")); // A doesn't resolve PkgB -> unchanged
        bumpB.Closure("ProjB").ShouldNotBe(basis.Closure("ProjB")); // B does -> changed

        // Bump the TRANSITIVE PkgCommon 1.0 -> 1.0.1 (the resolved 4-indent line).
        using var bumpCommon = new Repo(
            LockBase.Replace("    PkgCommon (1.0)", "    PkgCommon (1.0.1)", StringComparison.Ordinal),
            DepsBase
        );
        bumpCommon.Closure("ProjA").ShouldNotBe(basis.Closure("ProjA")); // A reaches PkgCommon transitively -> changed
        bumpCommon.Closure("ProjB").ShouldBe(basis.Closure("ProjB")); // B doesn't -> unchanged
    }

    // The closure walks dependency edges: ProjA references only PkgA, but PkgCommon (PkgA's dep) must be folded
    // in, so a transitive bump is seen.
    [Test]
    public void Closure_includes_transitive_dependencies()
    {
        using var repo = new Repo(LockBase, DepsBase);
        var a = repo.Closure("ProjA").ShouldNotBeNull();
        a.ShouldContain("pkgcommon"); // transitive dep folded
        a.ShouldContain("pkga");
        a.ShouldNotContain("pkgb"); // not in A's closure
    }

    // A change to a GLOBAL resolution setting (framework / source / redirects) is folded wholesale, so it
    // invalidates every paket-managed project regardless of closure — the deliberate safe coupling.
    [Test]
    public void Global_resolution_setting_change_invalidates_all_paket_projects()
    {
        using var basis = new Repo(LockBase, DepsBase);
        using var changed = new Repo(LockBase, DepsBase.Replace("framework: net8.0", "framework: net8.0, net48", StringComparison.Ordinal));

        changed.Closure("ProjA").ShouldNotBe(basis.Closure("ProjA"));
        changed.Closure("ProjB").ShouldNotBe(basis.Closure("ProjB"));
    }

    // A project with no paket.references is not paket-managed: no closure, so the lock contributes nothing to
    // its fingerprint (a lock bump can never invalidate it).
    [Test]
    public void Non_paket_project_has_no_closure_contribution()
    {
        using var repo = new Repo(LockBase, DepsBase);
        repo.AddProjectWithoutReferences("ProjC");
        PaketClosure.Compute(repo.ProjectDir("ProjC")).ShouldBeNull();
    }

    // End-to-end through the real fingerprint, in ONE repo (so the project-path term is constant): bumping a
    // package OUTSIDE ProjA's closure leaves ProjA's fingerprint identical; bumping one INSIDE flips it.
    [Test]
    public void BuildInputFingerprint_scopes_paket_invalidation_per_project()
    {
        using var repo = new Repo(LockBase, DepsBase);
        var fpA = repo.Fingerprint("ProjA");

        // PkgB is not in ProjA's closure -> ProjA's fingerprint is unchanged after the bump.
        repo.RewriteLock(LockBase.Replace("PkgB (2.0)", "PkgB (2.0.1)", StringComparison.Ordinal));
        repo.Fingerprint("ProjA").ShouldBe(fpA);

        // PkgCommon IS in ProjA's closure -> the bump invalidates ProjA. (Distinct length keeps the per-file
        // parse memo from reusing the previous lock.)
        repo.RewriteLock(LockBase.Replace("    PkgCommon (1.0)", "    PkgCommon (1.0.99)", StringComparison.Ordinal));
        repo.Fingerprint("ProjA").ShouldNotBe(fpA);
    }

    // A throwaway repo: root paket.lock + paket.dependencies and two projects (ProjA->PkgA, ProjB->PkgB).
    private sealed class Repo : IDisposable
    {
        private readonly string _root;

        public Repo(string lockText, string depsText)
        {
            _root = Directory.CreateTempSubdirectory("rig-paket-").FullName;
            File.WriteAllText(Path.Combine(_root, "paket.lock"), lockText);
            File.WriteAllText(Path.Combine(_root, "paket.dependencies"), depsText);
            AddProject("ProjA", "PkgA");
            AddProject("ProjB", "PkgB");
        }

        public string ProjectDir(string name) => Path.Combine(_root, name);

        public string? Closure(string name) => PaketClosure.Compute(ProjectDir(name));

        public string Fingerprint(string name) => BuildInputFingerprint.Compute(Path.Combine(ProjectDir(name), $"{name}.csproj"));

        public void RewriteLock(string lockText) => File.WriteAllText(Path.Combine(_root, "paket.lock"), lockText);

        public void AddProjectWithoutReferences(string name)
        {
            var dir = Directory.CreateDirectory(ProjectDir(name)).FullName;
            File.WriteAllText(Path.Combine(dir, $"{name}.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        }

        private void AddProject(string name, params string[] directReferences)
        {
            AddProjectWithoutReferences(name);
            File.WriteAllText(Path.Combine(ProjectDir(name), "paket.references"), string.Join('\n', directReferences));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort temp cleanup.
            }
        }
    }
}
