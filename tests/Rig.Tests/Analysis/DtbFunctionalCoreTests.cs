using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// The PURE core of the design-time-build cache, tested with no filesystem at all: the fingerprint fold
// (BuildInputFingerprint.Of over a materialised BuildInputs), the paket closure material
// (PaketClosure.ComputeMaterial over raw manifest text), and the hit/miss verdict
// (BuildCacheDecision.Decide). The imperative shell (Gather/Load/build/Store) is exercised separately by
// the real-temp-dir tests in PaketClosureTests; this file pins the logic that decides correctness.
public sealed class DtbFunctionalCoreTests
{
    // ---- BuildCacheDecision.Decide ---------------------------------------------------------------------

    [Test]
    public void Decide_is_a_miss_when_no_sidecar_exists()
    {
        var decision = BuildCacheDecision.Decide(currentFingerprint: "fp1", stored: null);
        var miss = decision.ShouldBeOfType<BuildCacheDecision.Miss>();
        miss.Fingerprint.ShouldBe("fp1"); // the current fp is carried so the rebuild stores under it
    }

    [Test]
    public void Decide_is_a_hit_when_the_stored_fingerprint_matches()
    {
        var info = Pbi();
        var decision = BuildCacheDecision.Decide(currentFingerprint: "fp1", stored: new StoredBuild(Fingerprint: "fp1", Info: info));
        decision.ShouldBeOfType<BuildCacheDecision.Hit>().Info.ShouldBeSameAs(info);
    }

    [Test]
    public void Decide_is_a_miss_when_the_stored_fingerprint_is_stale()
    {
        var decision = BuildCacheDecision.Decide(currentFingerprint: "fp-new", stored: new StoredBuild(Fingerprint: "fp-old", Info: Pbi()));
        decision.ShouldBeOfType<BuildCacheDecision.Miss>().Fingerprint.ShouldBe("fp-new");
    }

    // ---- BuildInputFingerprint.Of ----------------------------------------------------------------------

    [Test]
    public void Of_is_deterministic_and_sensitive_to_each_input()
    {
        var baseline = Inputs();
        BuildInputFingerprint.Of(baseline).ShouldBe(BuildInputFingerprint.Of(Inputs())); // pure: same inputs, same key

        // Every component flips the key.
        BuildInputFingerprint.Of(baseline with { ProjectFile = new("P.csproj", "PROJ2") }).ShouldNotBe(BuildInputFingerprint.Of(baseline));
        BuildInputFingerprint
            .Of(baseline with { ConfigFiles = [new("Directory.Build.props", "CFG2"), new("global.json", null)] })
            .ShouldNotBe(BuildInputFingerprint.Of(baseline));
        BuildInputFingerprint.Of(baseline with { PaketClosureMaterial = "different" }).ShouldNotBe(BuildInputFingerprint.Of(baseline));
        BuildInputFingerprint.Of(baseline with { CsPaths = ["A.cs"] }).ShouldNotBe(BuildInputFingerprint.Of(baseline));
    }

    [Test]
    public void Of_distinguishes_an_absent_config_file_from_a_present_one()
    {
        var present = Inputs() with { ConfigFiles = [new("global.json", "HASH")] };
        var absent = Inputs() with { ConfigFiles = [new("global.json", null)] };
        BuildInputFingerprint.Of(present).ShouldNotBe(BuildInputFingerprint.Of(absent));
    }

    [Test]
    public void Of_distinguishes_a_paket_managed_project_from_a_non_paket_one()
    {
        var paket = Inputs() with { PaketClosureMaterial = "paket-lock-settings\n...\n" };
        var nonPaket = Inputs() with { PaketClosureMaterial = null };
        BuildInputFingerprint.Of(paket).ShouldNotBe(BuildInputFingerprint.Of(nonPaket));
    }

    // ---- PaketClosure.ComputeMaterial (the pure entry; no temp dirs) -----------------------------------

    [Test]
    public void ComputeMaterial_scopes_and_walks_transitively_over_raw_text()
    {
        const string lockText = """
            NUGET
              remote: https://api.nuget.org/v3/index.json
                PkgA (1.0)
                  PkgCommon (>= 1.0)
                PkgB (2.0)
                PkgCommon (1.0)
            """;
        const string deps = "source https://api.nuget.org/v3/index.json\nnuget PkgA 1.0\nnuget PkgB 2.0\nnuget PkgCommon 1.0";

        var a = PaketClosure.ComputeMaterial(lockText, deps, "PkgA");
        var b = PaketClosure.ComputeMaterial(lockText, deps, "PkgB");

        a.ShouldContain("pkgcommon"); // transitive dep of PkgA folded in
        a.ShouldNotContain("\tpkgb"); // PkgB not in A's closure
        b.ShouldContain("\tpkgb");

        // A PkgB bump changes B's material, not A's (the scoping invariant, proved purely).
        var lockBumpB = lockText.Replace("PkgB (2.0)", "PkgB (2.0.1)", StringComparison.Ordinal);
        PaketClosure.ComputeMaterial(lockBumpB, deps, "PkgA").ShouldBe(a);
        PaketClosure.ComputeMaterial(lockBumpB, deps, "PkgB").ShouldNotBe(b);
    }

    // ---- BuildInfoEquivalence.Compare (--verify-build-cache core) --------------------------------------

    [Test]
    public void Compare_ignores_list_ordering()
    {
        var fresh = Pbi() with { References = ["a.dll", "b.dll"] };
        var cached = Pbi() with { References = ["b.dll", "a.dll"] }; // same set, different order
        BuildInfoEquivalence.Compare(fresh: fresh, cached: cached).IsEquivalent.ShouldBeTrue();
    }

    [Test]
    public void Compare_flags_a_drifted_reference_set_and_names_the_field()
    {
        var fresh = Pbi() with { References = ["a.dll", "c.dll"] };
        var cached = Pbi() with { References = ["a.dll", "b.dll"] };
        var result = BuildInfoEquivalence.Compare(fresh: fresh, cached: cached);
        result.IsEquivalent.ShouldBeFalse();
        result.Summary.ShouldContain("References");
        result.Summary.ShouldContain("+1/-1"); // c.dll added, b.dll removed
    }

    [Test]
    public void Compare_flags_a_changed_CONSUMED_property()
    {
        // LangVersion is consumed by rig (it sets the parse options) — a drift here is a real mismatch.
        var fresh = Pbi() with
        {
            Properties = new Dictionary<string, string> { ["LangVersion"] = "12.0" },
        };
        var cached = Pbi() with { Properties = new Dictionary<string, string> { ["LangVersion"] = "11.0" } };
        var result = BuildInfoEquivalence.Compare(fresh: fresh, cached: cached);
        result.IsEquivalent.ShouldBeFalse();
        result.Summary.ShouldContain("Properties");
    }

    [Test]
    public void Compare_ignores_non_consumed_properties()
    {
        // The Buildalyzer Properties dict carries hundreds of nondeterministic entries rig never reads; a
        // drift in one of those is NOT a mismatch (this is the calibration the MedDBase verify run forced).
        var fresh = Pbi() with
        {
            Properties = new Dictionary<string, string> { ["LangVersion"] = "12.0", ["BuildStartTime"] = "T1" },
        };
        var cached = Pbi() with
        {
            Properties = new Dictionary<string, string> { ["LangVersion"] = "12.0", ["BuildStartTime"] = "T2" },
        };
        BuildInfoEquivalence.Compare(fresh: fresh, cached: cached).IsEquivalent.ShouldBeTrue();
    }

    private static BuildInputFingerprint.BuildInputs Inputs() =>
        new(
            ProjectFile: new BuildInputFingerprint.FileFold("P.csproj", "PROJ"),
            ConfigFiles:
            [
                new BuildInputFingerprint.FileFold("Directory.Build.props", "CFG"),
                new BuildInputFingerprint.FileFold("global.json", null),
            ],
            PaketClosureMaterial: "paket-lock-settings\nsource x\n",
            CsPaths: ["A.cs", "B.cs"]
        );

    private static ProjectBuildInfo Pbi() =>
        new(
            ProjectFilePath: "P.csproj",
            References: [],
            ProjectReferences: [],
            SourceFiles: ["A.cs"],
            AnalyzerReferences: [],
            PreprocessorSymbols: [],
            Properties: new Dictionary<string, string>(StringComparer.Ordinal)
        );
}
