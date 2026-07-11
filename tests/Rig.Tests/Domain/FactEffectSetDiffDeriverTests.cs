using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for FactEffectSetDiffDeriver. The fixture helpers mirror FactCorrelationDeriverTests
// exactly (same FactGraphData builders, same DerivedEffect construction) so the test style is consistent.
public sealed class FactEffectSetDiffDeriverTests
{
    // --- fixture helpers (mirror FactCorrelationDeriverTests) ---

    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    // A graph with explicit nodes + no call edges (so each EP's reach set = just itself).
    private static FactGraphData GraphNodes(params string[] nodeIds)
    {
        var nodes = nodeIds.Select(M).ToArray();
        return new FactGraphData(Array.Empty<CallEdge>(), Array.Empty<ImplementsEdge>(), nodes);
    }

    private static CallEdge Edge(string caller, string callee) =>
        new(Caller: caller, Callee: callee, Kind: "invocation", FilePath: "f.cs", Line: 1);

    private static DerivedEffect Write(string resourceType, string enclosing, int line = 42) =>
        new(
            Provider: "llblgen",
            Operation: "bulk_write",
            ResourceType: resourceType,
            EnclosingSymbolId: enclosing,
            FilePath: "f.cs",
            Line: line
        );

    private static EffectSetDiffSpec Spec(EffectSetDiffPair pair, IReadOnlyList<EffectPredicate>? writePredicates = null) =>
        new(
            Pairs: [pair],
            Filter: writePredicates ?? [new EffectPredicate(Provider: "llblgen", Operation: "bulk_write")],
            Normalize: new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"])
        );

    private static EffectSetDiffPair Pair(string entity, string primaryId, string secondaryId) =>
        new(Label: entity, AId: primaryId, BId: secondaryId);

    // --- test cases ---

    [Test]
    public void Primary_writes_extra_table_secondary_lacks_it_yields_one_primary_only_finding()
    {
        // Primary EP reaches writes to Person + PersonEvent; secondary only reaches Person.
        // Expected: ONE finding — PersonEvent, AOnly.
        var graph = Graph(Edge("M:N.UiSave.Save", "M:N.PersonEventRepo.Write"), Edge("M:N.ApiImport.Import", "M:N.PersonRepo.Write"));
        // "M:N.UiSave.Save" also directly writes Person (same enclosing).
        var effects = new List<DerivedEffect>
        {
            Write("N.PersonEntityCollection", "M:N.UiSave.Save"),
            Write("N.PersonEventEntityCollection", "M:N.PersonEventRepo.Write"),
            Write("N.PersonEntityCollection", "M:N.ApiImport.Import"),
        };

        var findings = FactEffectSetDiffDeriver.Derive(
            graph: graph,
            effects: effects,
            spec: Spec(Pair(entity: "Person", primaryId: "M:N.UiSave.Save", secondaryId: "M:N.ApiImport.Import"))
        );

        findings.Count.ShouldBe(1);
        findings[0].Label.ShouldBe("Person");
        findings[0].ResourceKey.ShouldBe("PersonEvent");
        findings[0].Direction.ShouldBe(EffectDiffSide.AOnly);
        findings[0].PresentEpId.ShouldBe("M:N.UiSave.Save");
        findings[0].AbsentEpId.ShouldBe("M:N.ApiImport.Import");
        // The row is labeled by the present EP's provider:op for this resource (a durable write here).
        findings[0].Categories.ShouldBe(["llblgen:bulk_write"]);
    }

    [Test]
    public void Identical_write_sets_yield_zero_findings()
    {
        // Both EPs reach the same tables (Person only). No divergence.
        var graph = GraphNodes("M:N.UiSave.Save", "M:N.ApiImport.Import");
        var effects = new List<DerivedEffect>
        {
            Write("N.PersonEntityCollection", "M:N.UiSave.Save"),
            Write("N.PersonEntityCollection", "M:N.ApiImport.Import"),
        };

        var findings = FactEffectSetDiffDeriver.Derive(
            graph: graph,
            effects: effects,
            spec: Spec(Pair(entity: "Person", primaryId: "M:N.UiSave.Save", secondaryId: "M:N.ApiImport.Import"))
        );

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Symmetric_divergence_each_writes_unique_table_yields_two_findings_one_per_direction()
    {
        // Primary writes Person + PersonEvent; secondary writes Person + PersonAudit.
        // PersonEvent is primary-only; PersonAudit is secondary-only => TWO findings.
        var graph = GraphNodes("M:N.UiSave.Save", "M:N.ApiImport.Import");
        var effects = new List<DerivedEffect>
        {
            Write("N.PersonEntityCollection", "M:N.UiSave.Save"),
            Write("N.PersonEventEntityCollection", "M:N.UiSave.Save"),
            Write("N.PersonEntityCollection", "M:N.ApiImport.Import"),
            Write("N.PersonAuditEntityCollection", "M:N.ApiImport.Import"),
        };

        var findings = FactEffectSetDiffDeriver.Derive(
            graph: graph,
            effects: effects,
            spec: Spec(Pair(entity: "Person", primaryId: "M:N.UiSave.Save", secondaryId: "M:N.ApiImport.Import"))
        );

        findings.Count.ShouldBe(2);
        findings.Any(f => f.ResourceKey == "PersonAudit" && f.Direction == EffectDiffSide.BOnly).ShouldBeTrue();
        findings.Any(f => f.ResourceKey == "PersonEvent" && f.Direction == EffectDiffSide.AOnly).ShouldBeTrue();
    }

    [Test]
    public void Findings_are_stably_ordered_by_entity_then_key_then_direction()
    {
        // Two entities (Alpha, Zeta) each with a primary-only table — verify sort order is stable on
        // (Label, ResourceKey, Direction). Calling Derive twice must return the same order.
        var graph = GraphNodes("M:N.AlphaUi.Save", "M:N.AlphaApi.Import", "M:N.ZetaUi.Save", "M:N.ZetaApi.Import");
        var effects = new List<DerivedEffect>
        {
            Write("N.AlphaEntityCollection", "M:N.AlphaUi.Save"),
            Write("N.AlphaLinkEntityCollection", "M:N.AlphaUi.Save"),
            Write("N.AlphaEntityCollection", "M:N.AlphaApi.Import"),
            Write("N.ZetaEntityCollection", "M:N.ZetaUi.Save"),
            Write("N.ZetaEventEntityCollection", "M:N.ZetaUi.Save"),
            Write("N.ZetaEntityCollection", "M:N.ZetaApi.Import"),
        };

        var spec = new EffectSetDiffSpec(
            Pairs:
            [
                Pair(entity: "Alpha", primaryId: "M:N.AlphaUi.Save", secondaryId: "M:N.AlphaApi.Import"),
                Pair(entity: "Zeta", primaryId: "M:N.ZetaUi.Save", secondaryId: "M:N.ZetaApi.Import"),
            ],
            Filter: [new EffectPredicate(Provider: "llblgen", Operation: "bulk_write")],
            Normalize: new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"])
        );

        var first = FactEffectSetDiffDeriver.Derive(graph: graph, effects: effects, spec: spec);
        var second = FactEffectSetDiffDeriver.Derive(graph: graph, effects: effects, spec: spec);

        first.Count.ShouldBe(2);
        first.Select(f => (f.Label, f.ResourceKey)).ShouldBe(second.Select(f => (f.Label, f.ResourceKey)));

        // Alpha comes before Zeta lexicographically.
        first[0].Label.ShouldBe("Alpha");
        first[1].Label.ShouldBe("Zeta");
    }

    [Test]
    public void Write_in_reachable_callee_counts_toward_ep_write_set()
    {
        // Primary EP calls a helper that writes PersonEvent; the write effect is enclosed by the HELPER,
        // not the EP itself. It must still count toward the primary's write-set (via forward reach).
        // Both EPs must be graph NODES (the Graph() helper derives nodes from edges) — the secondary gets a
        // benign edge so its reach set is non-empty; it writes Person but never reaches the PersonEvent helper.
        var graph = Graph(Edge("M:N.UiSave.Save", "M:N.EventHelper.Emit"), Edge("M:N.ApiImport.Import", "M:N.PersonRepo.Write"));
        var effects = new List<DerivedEffect>
        {
            Write("N.PersonEntityCollection", "M:N.UiSave.Save"),
            Write("N.PersonEventEntityCollection", "M:N.EventHelper.Emit"), // enclosed by helper, reachable from primary
            Write("N.PersonEntityCollection", "M:N.ApiImport.Import"),
            // secondary does NOT call EventHelper — PersonRepo.Write has no write effect
        };

        var findings = FactEffectSetDiffDeriver.Derive(
            graph: graph,
            effects: effects,
            spec: Spec(Pair(entity: "Person", primaryId: "M:N.UiSave.Save", secondaryId: "M:N.ApiImport.Import"))
        );

        findings.Count.ShouldBe(1);
        findings[0].ResourceKey.ShouldBe("PersonEvent");
        findings[0].Direction.ShouldBe(EffectDiffSide.AOnly);
    }

    [Test]
    public void Write_not_matching_predicate_is_excluded_from_write_set()
    {
        // An effect with a different provider (e.g. "cache") does not count as a write. With the non-
        // matching effect excluded both EPs write only Person → no findings.
        var graph = GraphNodes("M:N.UiSave.Save", "M:N.ApiImport.Import");
        var effects = new List<DerivedEffect>
        {
            Write("N.PersonEntityCollection", "M:N.UiSave.Save"),
            // A cache effect enclosed by primary — must be ignored because provider != "llblgen".
            new DerivedEffect(
                Provider: "cache",
                Operation: "write",
                ResourceType: "N.PersonEventEntityCollection",
                EnclosingSymbolId: "M:N.UiSave.Save",
                FilePath: "f.cs",
                Line: 5
            ),
            Write("N.PersonEntityCollection", "M:N.ApiImport.Import"),
        };

        var findings = FactEffectSetDiffDeriver.Derive(
            graph: graph,
            effects: effects,
            spec: Spec(Pair(entity: "Person", primaryId: "M:N.UiSave.Save", secondaryId: "M:N.ApiImport.Import"))
        );

        findings.ShouldBeEmpty();
    }

    [Test]
    public void No_pairs_yields_empty_result()
    {
        var graph = GraphNodes("M:N.UiSave.Save");
        var effects = new List<DerivedEffect> { Write("N.PersonEntityCollection", "M:N.UiSave.Save") };

        var spec = new EffectSetDiffSpec(
            Pairs: [],
            Filter: [new EffectPredicate(Provider: "llblgen", Operation: "bulk_write")],
            Normalize: new NormalizeSpec(SimpleTypeName: true)
        );

        var findings = FactEffectSetDiffDeriver.Derive(graph: graph, effects: effects, spec: spec);

        findings.ShouldBeEmpty();
    }
}
