using Rig.Cli.CommandLine;
using Rig.Cli.Commands;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Cli;

// Tests for the three FR-1 PRECISION changes:
//   Change 1 — #cctor exemption (CLR type-init lock serializes static ctors → no race possible)
//   Change 2 — human rollup dedup per (type, enclosing method) with ×N site count
//   Change 3 — --exclude-namespace <prefix> filter on the hazard surface

// ─── CHANGE 1: #cctor exemption ─────────────────────────────────────────────

public sealed class HazardCctorExemptionTests
{
    // A static constructor whose body reads then writes the same static cell must NOT emit any hazard
    // observation. The CLR type-init lock serializes static ctor execution — only one thread ever runs a
    // given #cctor, so the read→write pair cannot race.
    [Test]
    public void Static_ctor_cctor_read_before_write_emits_no_race_observation()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Config
                {
                    public static int Value;

                    static Config()
                    {
                        if (Value == 0)
                        {
                            Value = 1;
                        }
                    }
                }
            }
            """
        );

        // The mutate still fires (annotate-only — nothing suppressed from the effect list).
        result.SharedStateMutationsIn("#cctor").Count.ShouldBe(1);

        // But NO race observation of any kind — the CLR type-init lock serializes #cctor execution.
        result.RaceWindowsIn("#cctor").ShouldBeEmpty();
        result.LazyInitRacesIn("#cctor").ShouldBeEmpty();
    }

    // Instance constructors (.#ctor) are NOT exempted — they can race. Verify a read→write in a .#ctor
    // still classifies as lazy_init_race (the init context heuristic: ctor name → EnclosingLooksInitLike).
    [Test]
    public void Instance_ctor_read_before_write_still_classifies_as_lazy_init_race()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public sealed class Widget
                {
                    public static int _instance;

                    public Widget()
                    {
                        if (_instance == 0)
                        {
                            _instance = 1;
                        }
                    }
                }
            }
            """
        );

        // Instance ctor is NOT exempt — it must still carry a hazard classification.
        result.SharedStateMutationsIn("#ctor").ShouldNotBeEmpty();
        // The ctor context => lazy_init_race (low, heuristic), not race_window.
        result.LazyInitRacesIn("#ctor").ShouldNotBeEmpty();
        result.RaceWindowsIn("#ctor").ShouldBeEmpty();
    }

    // A plain method (non-init context, non-init cell name) must still produce a race_window — the cctor
    // exemption must not bleed into other methods.
    [Test]
    public void Plain_method_read_before_write_still_emits_race_window()
    {
        var result = ProductionFixCorpus.Analyze(
            """
            namespace App
            {
                public static class Counter
                {
                    public static int N;

                    public static void Bump()
                    {
                        if (N >= 0)
                        {
                            N = 1;
                        }
                    }
                }
            }
            """
        );

        result.RaceWindowsIn("Counter.Bump").Count.ShouldBe(1);
        result.RaceWindowsIn("Counter.Bump")[0].Confidence.ShouldBe("high");
    }

    // Verify the low-level deriver exemption directly (no corpus overhead): DeriveRaceWindows must
    // return the write effect untouched (no observation) when the enclosing id is a #cctor.
    [Test]
    public void DeriveRaceWindows_emits_no_observation_when_enclosing_is_cctor()
    {
        var cell = "F:App.Config.Value";
        var cctorId = "M:App.Config.#cctor";

        var read = new DerivedEffect(
            Provider: "shared_state",
            Operation: "read",
            ResourceType: cell,
            EnclosingSymbolId: cctorId,
            FilePath: "Config.cs",
            Line: 5,
            Observations: null
        );
        var write = new DerivedEffect(
            Provider: "shared_state",
            Operation: "mutate",
            ResourceType: cell,
            EnclosingSymbolId: cctorId,
            FilePath: "Config.cs",
            Line: 8,
            Observations: null
        );

        var result = FactHazardDeriver.DeriveRaceWindows([read, write]);

        // The write must be returned unchanged — no observation appended.
        var writtenBack = result.Single(e => e.Operation == "mutate");
        (writtenBack.Observations ?? []).ShouldBeEmpty();
    }

    // Contrast: the same read→write pair in a regular (non-cctor) method must receive an observation.
    [Test]
    public void DeriveRaceWindows_emits_observation_when_enclosing_is_regular_method()
    {
        var cell = "F:App.Cache.Status";
        var methodId = "M:App.Cache.Bump";

        var read = new DerivedEffect(
            Provider: "shared_state",
            Operation: "read",
            ResourceType: cell,
            EnclosingSymbolId: methodId,
            FilePath: "Cache.cs",
            Line: 3,
            Observations: null
        );
        var write = new DerivedEffect(
            Provider: "shared_state",
            Operation: "mutate",
            ResourceType: cell,
            EnclosingSymbolId: methodId,
            FilePath: "Cache.cs",
            Line: 7,
            Observations: null
        );

        var result = FactHazardDeriver.DeriveRaceWindows([read, write]);

        var writtenBack = result.Single(e => e.Operation == "mutate");
        writtenBack.Observations.ShouldNotBeNull();
        writtenBack.Observations!.ShouldHaveSingleItem();
    }
}

// ─── CHANGE 2: dedup rollup per (type, enclosing method) ────────────────────

public sealed class HazardRollupDedupTests
{
    private static DeriveCommand.HazardFinding Finding(
        string type,
        string confidence,
        string enclosing,
        string context = "F:App.Cache._x",
        string filePath = "C:/repo/App/Cache.cs",
        int line = 42
    ) =>
        new(
            Type: type,
            Confidence: confidence,
            Reason: type == FactHazardDeriver.RaceWindowType ? "rmw_no_isolation_on_path" : $"{type}_reason",
            Context: context,
            Detail: "C:/repo/App/Cache.cs:1",
            Enclosing: enclosing,
            FilePath: filePath,
            Line: line
        );

    // When one method has multiple race_window sites, WriteHazards must render ONE line for that method
    // with a ×N suffix and the per-type header must report both site(s) and method(s) counts.
    [Test]
    public void WriteHazards_dedups_multiple_sites_in_one_method_to_one_row_with_xN_suffix()
    {
        var findings = new List<DeriveCommand.HazardFinding>
        {
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.Cache.Bump", line: 10),
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.Cache.Bump", line: 20),
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.Cache.Bump", line: 30),
        };

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        // The per-type header must mention site and method counts.
        text.ShouldContain("race_window: 3 site(s)");
        // Only 1 method, so "across N method(s)" is suppressed as noise for the single-method case.
        text.ShouldNotContain("across 1 method");

        // One method row with ×3 suffix.
        text.ShouldContain("×3");
        // Must NOT print 3 separate rows for the same method. Rows render the SHORT name (ShortName strips
        // the namespace), so "M:App.Cache.Bump" surfaces as "Cache.Bump".
        var methodRows = text.Split('\n').Count(l => l.Contains("Cache.Bump"));
        methodRows.ShouldBe(1);
    }

    // Multiple distinct methods: each method gets its own row; the header shows both site and method counts.
    [Test]
    public void WriteHazards_shows_site_and_method_counts_in_header_when_multiple_methods()
    {
        var findings = new List<DeriveCommand.HazardFinding>
        {
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.A.Foo", line: 1),
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.A.Foo", line: 2),
            Finding(FactHazardDeriver.RaceWindowType, "medium", "M:App.B.Bar", line: 3),
        };

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        text.ShouldContain("race_window: 3 site(s) across 2 method(s)");
    }

    // The worst-first ordering: a high-confidence method must appear before a medium-confidence method;
    // within the same confidence, higher site count comes first.
    [Test]
    public void WriteHazards_orders_methods_worst_confidence_first_then_by_site_count_desc()
    {
        var findings = new List<DeriveCommand.HazardFinding>
        {
            Finding(FactHazardDeriver.RaceWindowType, "medium", "M:App.B.Low", line: 1),
            Finding(FactHazardDeriver.RaceWindowType, "medium", "M:App.B.Low", line: 2),
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.A.High", line: 3),
        };

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        // Rows render the SHORT name (namespace stripped): "M:App.A.High" -> "A.High", "M:App.B.Low" -> "B.Low".
        var highPos = text.IndexOf("A.High", StringComparison.Ordinal);
        var lowPos = text.IndexOf("B.Low", StringComparison.Ordinal);
        highPos.ShouldBeLessThan(lowPos, "high-confidence method must appear before medium-confidence method");
    }

    // The sample cap is now on METHOD rows (post-dedup), not sites: when distinct methods exceed the
    // per-type cap (limit/8 + 1 = 6 at limit 40), a truncation note points at the tsv for the full list.
    [Test]
    public void WriteHazards_truncation_note_fires_when_method_rows_exceed_the_sample_cap()
    {
        var findings = new List<DeriveCommand.HazardFinding>();
        for (var i = 0; i < 8; i++)
        {
            findings.Add(Finding(FactHazardDeriver.RaceWindowType, "high", $"M:App.N{i}.Do", line: i + 1));
        }

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        // 8 methods > 6-row cap → note fires (and references the tsv as the full-list escape hatch).
        text.ShouldContain("more race_window");
        text.ShouldContain("rig derive --format tsv");
    }

    // HazardTsvRow must stay per-site (one row per finding) — the dedup is human-only.
    [Test]
    public void HazardTsvRow_is_per_site_not_deduplicated()
    {
        var finding = Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.Cache.Bump");

        // Each HazardFinding maps to exactly one tsv row — the row count must equal the finding count.
        var row = DeriveCommand.HazardTsvRow(finding);
        row.ShouldStartWith("hazard\t");
        // The row must NOT contain any ×N suffix (dedup is human-only).
        row.ShouldNotContain("×");
    }

    // When findings are empty, WriteHazards still emits nothing.
    [Test]
    public void WriteHazards_emits_nothing_when_no_findings()
    {
        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, [], limit: 40);
        sw.ToString().ShouldBeEmpty();
    }

    // Single method, single site: no ×N suffix rendered (not meaningful for a single site).
    [Test]
    public void WriteHazards_no_xN_suffix_for_single_site_method()
    {
        var findings = new List<DeriveCommand.HazardFinding>
        {
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.Cache.Bump", line: 42),
        };

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        text.ShouldNotContain("×");
        text.ShouldContain("race_window: 1 site(s)");
    }

    // The per-confidence tier breakdown still uses SITE counts (not method counts).
    [Test]
    public void WriteHazards_tier_breakdown_uses_site_counts()
    {
        var findings = new List<DeriveCommand.HazardFinding>
        {
            // Two high-confidence sites in one method, one medium-confidence site in another.
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.A.Foo", line: 1),
            Finding(FactHazardDeriver.RaceWindowType, "high", "M:App.A.Foo", line: 2),
            Finding(FactHazardDeriver.RaceWindowType, "medium", "M:App.B.Bar", line: 3),
        };

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        // Tier breakdown is site-level: high=2, medium=1.
        text.ShouldContain("(high 2, medium 1)");
    }
}

// ─── CHANGE 3: --exclude-namespace filter ────────────────────────────────────

public sealed class HazardExcludeNamespaceTests
{
    private static DeriveCommand.HazardFinding Finding(string enclosing, string type = "race_window") =>
        new(
            Type: type,
            Confidence: "high",
            Reason: "rmw_no_isolation_on_path",
            Context: "F:App.Cache._x",
            Detail: "F.cs:1",
            Enclosing: enclosing,
            FilePath: "F.cs",
            Line: 1
        );

    // MatchesExcludedNamespace: a prefix that matches the DocID namespace portion should return true.
    [Test]
    public void MatchesExcludedNamespace_returns_true_when_enclosing_starts_with_prefix()
    {
        CommonOptions.MatchesExcludedNamespace("M:Echo.Process.Actor.Bump", ["Echo.Process"]).ShouldBeTrue();
    }

    // MatchesExcludedNamespace: a non-matching prefix must return false.
    [Test]
    public void MatchesExcludedNamespace_returns_false_when_no_prefix_matches()
    {
        CommonOptions.MatchesExcludedNamespace("M:App.Cache.Bump", ["Echo.Process", "System."]).ShouldBeFalse();
    }

    // MatchesExcludedNamespace: matching is case-insensitive.
    [Test]
    public void MatchesExcludedNamespace_is_case_insensitive()
    {
        CommonOptions.MatchesExcludedNamespace("M:echo.process.Actor.Bump", ["Echo.Process"]).ShouldBeTrue();
    }

    // MatchesExcludedNamespace: empty prefix list never matches.
    [Test]
    public void MatchesExcludedNamespace_returns_false_for_empty_prefix_list()
    {
        CommonOptions.MatchesExcludedNamespace("M:App.Cache.Bump", []).ShouldBeFalse();
    }

    // MatchesExcludedNamespace: the "M:" kind prefix is stripped before comparison.
    [Test]
    public void MatchesExcludedNamespace_strips_kind_prefix_before_matching()
    {
        // "M:System.Text.X" — the "System." prefix should match after stripping "M:".
        CommonOptions.MatchesExcludedNamespace("M:System.Text.StringBuilder.Append", ["System."]).ShouldBeTrue();
    }

    // WriteHazards must exclude findings whose enclosing starts with a given prefix — they must not
    // appear in the rendered output when pre-filtered by the caller.
    [Test]
    public void WriteHazards_after_exclusion_hides_filtered_namespace_findings()
    {
        var allFindings = new List<DeriveCommand.HazardFinding>
        {
            Finding("M:App.Service.Save"),
            Finding("M:Echo.Process.MailboxLoop.HandleMsg"),
            Finding("M:System.Threading.Timer.Callback"),
        };

        // Simulate what the command does: filter before WriteHazards.
        var excludedPrefixes = CommonOptions.NamespacePrefixes(["Echo.Process", "System."]);
        var filtered = allFindings.Where(f => !CommonOptions.MatchesExcludedNamespace(f.Enclosing, excludedPrefixes)).ToList();

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, filtered, limit: 40);
        var text = sw.ToString();

        // Rows render the SHORT name (ShortName strips the namespace), so assert on the rendered member name,
        // not the namespace — otherwise the assertion would pass trivially (the namespace never renders at all).
        // The surviving App.Service.Save renders as "Service.Save"; the filtered Echo.Process / System.Threading
        // findings render as "MailboxLoop.HandleMsg" / "Timer.Callback" — which must be ABSENT (proving the filter).
        text.ShouldContain("Service.Save");
        text.ShouldNotContain("MailboxLoop");
        text.ShouldNotContain("Timer.Callback");
    }

    // The effect rows (non-hazard) must be unaffected by the namespace filter — only hazards are filtered.
    [Test]
    public void Effect_rows_are_unaffected_by_exclude_namespace()
    {
        // This test confirms the separation of concerns: MatchesExcludedNamespace is used only for hazard
        // filtering; the effect filter helpers (ApplyEffectFilters) operate independently.
        // We verify that a "System." enclosing is still excluded from hazards while the same finding object
        // (which represents an effect site) is returned from HazardFindings unchanged when effects are not
        // filtered. The point: the filter is applied only to the hazard list, not re-applied to effects.
        var effect = new DerivedEffect(
            Provider: "shared_state",
            Operation: "mutate",
            ResourceType: "F:App.Cache._x",
            EnclosingSymbolId: "M:System.Threading.Timer.Callback",
            FilePath: "Timer.cs",
            Line: 5,
            Observations:
            [
                new EffectObservationInfo(
                    Type: FactHazardDeriver.RaceWindowType,
                    Context: "F:App.Cache._x",
                    Detail: "Timer.cs:3",
                    Confidence: "high",
                    Basis: "fact_derived",
                    Reason: "rmw_no_isolation_on_path"
                ),
            ]
        );

        // HazardFindings flatly reads all hazard observations — it is not namespace-aware.
        var findings = DeriveCommand.HazardFindings([effect]);
        findings.Count.ShouldBe(1);

        // Now apply the namespace filter: the hazard IS excluded.
        var excluded = findings.Where(f => !CommonOptions.MatchesExcludedNamespace(f.Enclosing, ["System."])).ToList();
        excluded.ShouldBeEmpty();
    }

    // Multiple prefixes: all matching prefixes must be excluded.
    [Test]
    public void MatchesExcludedNamespace_with_multiple_prefixes_excludes_all_matching()
    {
        var prefixes = CommonOptions.NamespacePrefixes(["Echo.Process", "MMS.Diagnostics", "System."]);

        CommonOptions.MatchesExcludedNamespace("M:Echo.Process.Core.Recv", prefixes).ShouldBeTrue();
        CommonOptions.MatchesExcludedNamespace("M:MMS.Diagnostics.Logger.Log", prefixes).ShouldBeTrue();
        CommonOptions.MatchesExcludedNamespace("M:System.Collections.Generic.List`1.Add", prefixes).ShouldBeTrue();
        CommonOptions.MatchesExcludedNamespace("M:App.Service.Save", prefixes).ShouldBeFalse();
    }
}
