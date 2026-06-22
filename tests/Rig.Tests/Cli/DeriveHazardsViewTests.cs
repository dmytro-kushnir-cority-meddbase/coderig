using Rig.Cli.Commands;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Cli;

// The `rig derive` Hazards VIEW (rendering only — detection is upstream in FactHazardDeriver /
// FactObservationDeriver and frozen). Pins: (1) HazardFindings flattens only HAZARD observations (race_window
// / lazy_init_race / n_plus_1 / unserializable_payload) and leaves STRUCTURAL ones (looped_effect, …) out;
// (2) WriteHazards names each type with a per-confidence-tier breakdown + sampled sites; (3) the tsv `hazard`
// row carries confidence + reason + detail. These are the columns a consumer reads to answer "how confident /
// why", which the comma-joined `effect`-row observation Type list can't.
public sealed class DeriveHazardsViewTests
{
    private static EffectObservationInfo Obs(
        string type,
        string confidence,
        string reason,
        string context = "cell",
        string detail = "F.cs:1"
    ) => new(Type: type, Context: context, Detail: detail, Confidence: confidence, Basis: "fact_derived", Reason: reason);

    private static DerivedEffect Effect(string enclosing, params EffectObservationInfo[] observations) =>
        new(
            Provider: "shared_state",
            Operation: "mutate",
            ResourceType: "F:App.Cache._x",
            EnclosingSymbolId: enclosing,
            FilePath: "C:/repo/App/Cache.cs",
            Line: 42,
            Observations: observations
        );

    [Test]
    public void HazardFindings_keeps_hazards_and_drops_structural_observations()
    {
        var effects = new[]
        {
            Effect(
                "M:App.Cache.Bump",
                Obs(FactHazardDeriver.RaceWindowType, "high", "rmw_no_isolation_on_path"),
                Obs("looped_effect", "high", "in_loop") // STRUCTURAL — must NOT become a hazard finding
            ),
            Effect("M:App.Svc.Read", Obs(HazardKinds.NPlusOne, "high", "looped_read_with_varying_key")),
        };

        var findings = DeriveCommand.HazardFindings(effects);

        findings.Select(f => f.Type).ShouldBe([FactHazardDeriver.RaceWindowType, HazardKinds.NPlusOne]);
        findings.ShouldNotContain(f => f.Type == "looped_effect");
        // The finding carries the effect's site + the observation's confidence/reason for rendering.
        var race = findings.Single(f => f.Type == FactHazardDeriver.RaceWindowType);
        race.Confidence.ShouldBe("high");
        race.Enclosing.ShouldBe("M:App.Cache.Bump");
        race.Line.ShouldBe(42);
    }

    [Test]
    public void WriteHazards_names_types_and_breaks_down_by_confidence()
    {
        var findings = new List<DeriveCommand.HazardFinding>();
        for (var i = 0; i < 121; i++)
        {
            findings.Add(Finding(FactHazardDeriver.RaceWindowType, "high"));
        }

        for (var i = 0; i < 26; i++)
        {
            findings.Add(Finding(FactHazardDeriver.RaceWindowType, "medium"));
        }

        for (var i = 0; i < 32; i++)
        {
            findings.Add(Finding(FactHazardDeriver.LazyInitRaceType, "low"));
        }

        for (var i = 0; i < 4; i++)
        {
            findings.Add(Finding(HazardKinds.NPlusOne, "high"));
        }

        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, findings, limit: 40);
        var text = sw.ToString();

        text.ShouldContain("Hazards (pattern findings): 183");
        // race_window is busiest → first, with its high/medium tier breakdown.
        text.ShouldContain("race_window: 147 (high 121, medium 26)");
        // lazy_init_race shows its (single) low tier — a non-high tier is informative.
        text.ShouldContain("lazy_init_race: 32 (low 32)");
        // n_plus_1 is always high → the bare "(high N)" parenthetical is suppressed as noise.
        text.ShouldContain("n_plus_1: 4");
        text.ShouldNotContain("n_plus_1: 4 (high");
        // The capped sample carries the reason and points at the tsv for the full list.
        text.ShouldContain("rmw_no_isolation_on_path");
        text.ShouldContain("rig derive --format tsv");
    }

    [Test]
    public void WriteHazards_emits_nothing_when_no_hazards()
    {
        var sw = new StringWriter();
        DeriveCommand.WriteHazards(sw, [], limit: 40);
        sw.ToString().ShouldBeEmpty();
    }

    [Test]
    public void HazardTsvRow_carries_confidence_reason_and_detail()
    {
        var row = DeriveCommand.HazardTsvRow(
            new DeriveCommand.HazardFinding(
                Type: FactHazardDeriver.RaceWindowType,
                Confidence: "medium",
                Reason: "rmw_in_transaction_verify_isolation",
                Context: "F:App.Cache._x",
                Detail: "C:/repo/App/Cache.cs:7",
                Enclosing: "M:App.Cache.Bump",
                FilePath: "C:/repo/App/Cache.cs",
                Line: 42
            )
        );

        var cols = row.Split('\t');
        cols[0].ShouldBe("hazard");
        cols[1].ShouldBe("race_window");
        cols[2].ShouldBe("medium"); // confidence — the field the effect-row Type list can't carry
        cols[3].ShouldBe("rmw_in_transaction_verify_isolation"); // reason
        cols[4].ShouldBe("F:App.Cache._x"); // cell/context
        cols[5].ShouldBe("M:App.Cache.Bump"); // enclosing
        cols[6].ShouldBe("C:/repo/App/Cache.cs"); // file
        cols[7].ShouldBe("42"); // line
        cols[8].ShouldBe("C:/repo/App/Cache.cs:7"); // detail (paired-read site)
    }

    private static DeriveCommand.HazardFinding Finding(string type, string confidence) =>
        new(
            Type: type,
            Confidence: confidence,
            Reason: type == FactHazardDeriver.RaceWindowType ? "rmw_no_isolation_on_path" : $"{type}_reason",
            Context: "F:App.Cache._x",
            Detail: "C:/repo/App/Cache.cs:7",
            Enclosing: "M:App.Cache.Bump",
            FilePath: "C:/repo/App/Cache.cs",
            Line: 42
        );
}
