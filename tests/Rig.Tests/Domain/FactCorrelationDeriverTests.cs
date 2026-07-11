using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Unit tests for the generic effect-correlation deriver (FR-7 reframe). The spec under test is the
// cache_coherence instance: anchor = llblgen.bulk_write, companion = cache.invalidate, co-referenced via
// ResourceKey (simple-type-name + suffix strip on each side). Polarity=Absence flags a bulk write whose
// forward closure has no same-entity cache invalidation.
public sealed class FactCorrelationDeriverTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    private static CallEdge Edge(string caller, string callee) =>
        new(Caller: caller, Callee: callee, Kind: "invocation", FilePath: "f.cs", Line: 1);

    private static DerivedEffect Anchor(string resourceType, string enclosing, int line = 42) =>
        new(
            Provider: "llblgen",
            Operation: "bulk_write",
            ResourceType: resourceType,
            EnclosingSymbolId: enclosing,
            FilePath: "f.cs",
            Line: line
        );

    private static DerivedEffect Invalidate(string resourceType, string enclosing, int line = 99) =>
        new(
            Provider: "cache",
            Operation: "invalidate",
            ResourceType: resourceType,
            EnclosingSymbolId: enclosing,
            FilePath: "f.cs",
            Line: line
        );

    private static CorrelationSpec Spec(IReadOnlyList<string>? excludeNs = null) =>
        new(
            Anchor: new EffectPredicate("llblgen", "bulk_write"),
            Companion: new EffectPredicate("cache", "invalidate"),
            AnchorNormalize: new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"]),
            CompanionNormalize: new NormalizeSpec(SimpleTypeName: true, StripSuffix: ["Cache"]),
            ExcludeEnclosingNamespaceSuffix: excludeNs
        );

    [Test]
    public void Flags_a_bulk_write_with_no_companion_invalidation()
    {
        // Importer.Run does a bulk write; nothing invalidates the Account cache anywhere.
        var graph = Graph(Edge("M:N.Importer.Run", "M:N.AccountEntityCollection.UpdateMulti(System.Object)"));
        var effects = new List<DerivedEffect> { Anchor("N.AccountEntityCollection", "M:N.Importer.Run") };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec());

        findings.Count.ShouldBe(1);
        findings[0].ResourceKey.ShouldBe("Account");
        findings[0].Method.ShouldContain("Importer.Run");
        findings[0].Line.ShouldBe(42);
        findings[0].AnchorProvider.ShouldBe("llblgen");
        findings[0].AnchorOperation.ShouldBe("bulk_write");
    }

    [Test]
    public void Clean_when_invalidation_is_in_a_forward_reachable_method_same_key()
    {
        // Importer.Run -> Invalidator.Bust; the invalidation effect is enclosed by Bust, reachable from Run.
        var graph = Graph(Edge("M:N.Importer.Run", "M:N.Invalidator.Bust"));
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.Run"),
            Invalidate("N.AccountCache", "M:N.Invalidator.Bust"),
        };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec());

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Clean_when_invalidation_is_in_the_anchors_own_method_proving_self_reach()
    {
        // The invalidation is enclosed by the SAME method as the anchor. This passes ONLY if a seed's reach
        // set includes the seed itself (ReachesFromEachSeed seeds depth-0 with the start node). There is no
        // edge out of Importer.Run, so a self-reach is the only way this can be clean.
        var graph = Graph(Edge("M:N.Importer.Run", "M:N.AccountEntityCollection.UpdateMulti(System.Object)"));
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.Run"),
            Invalidate("N.AccountCache", "M:N.Importer.Run"),
        };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec());

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Key_mismatch_still_flags_companion_of_a_different_resource_does_not_clear_it()
    {
        // Bust is reachable and DOES invalidate — but the PersonCache, not the Account cache. The Account
        // anchor stays flagged (no companion for its key).
        var graph = Graph(Edge("M:N.Importer.Run", "M:N.Invalidator.Bust"));
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.Run"),
            Invalidate("N.PersonCache", "M:N.Invalidator.Bust"),
        };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec());

        findings.Count.ShouldBe(1);
        findings[0].ResourceKey.ShouldBe("Account");
    }

    [Test]
    public void ExcludeEnclosingNamespaceSuffix_filters_generated_orm_anchors()
    {
        // Same flagged shape, but the anchor is enclosed by a generated-ORM mutator whose namespace ends in
        // "CollectionClasses" — filtered out, so no finding.
        var enclosing = "M:MedDBase.CollectionClasses.AccountCollection.UpdateMulti(System.Object)";
        var graph = Graph(Edge(enclosing, "M:N.AccountEntityCollection.UpdateMulti(System.Object)"));
        var effects = new List<DerivedEffect> { Anchor("N.AccountEntityCollection", enclosing) };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec(excludeNs: ["CollectionClasses"]));

        findings.ShouldBeEmpty();
    }

    [Test]
    public void ExcludeEnclosingNamespaceSuffix_does_not_filter_a_non_matching_namespace()
    {
        // Guard: the filter must NOT swallow a real bug site whose namespace does not end with the suffix.
        var enclosing = "M:MedDBase.Importing.AccountImporter.Run(System.Object)";
        var graph = Graph(Edge(enclosing, "M:N.AccountEntityCollection.UpdateMulti(System.Object)"));
        var effects = new List<DerivedEffect> { Anchor("N.AccountEntityCollection", enclosing) };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec(excludeNs: ["CollectionClasses"]));

        findings.Count.ShouldBe(1);
        findings[0].ResourceKey.ShouldBe("Account");
    }

    [Test]
    public void Clean_when_companion_is_forward_reachable_across_two_hops()
    {
        // Run -> Mid -> Invalidator.Bust: the invalidation is two edges downstream. Proves multi-hop reach,
        // not just same-method.
        var graph = Graph(Edge("M:N.Importer.Run", "M:N.Mid.Step"), Edge("M:N.Mid.Step", "M:N.Invalidator.Bust"));
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.Run"),
            Invalidate("N.AccountCache", "M:N.Invalidator.Bust"),
        };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec());

        findings.ShouldBeEmpty();
    }

    [Test]
    public void Invalidation_in_an_UNreachable_method_still_flags()
    {
        // The invalidation exists and has the right key, but its enclosing method is not on Run's forward
        // closure (no path Run -> Other). The anchor stays flagged. Companion-of-the-same-key existing
        // somewhere is NOT enough — it must be reachable.
        var graph = Graph(
            Edge("M:N.Importer.Run", "M:N.AccountEntityCollection.UpdateMulti(System.Object)"),
            Edge("M:N.Unrelated.Caller", "M:N.Invalidator.Bust")
        );
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.Run"),
            Invalidate("N.AccountCache", "M:N.Invalidator.Bust"),
        };

        var findings = FactCorrelationDeriver.Derive(graph, effects, Spec());

        findings.Count.ShouldBe(1);
        findings[0].ResourceKey.ShouldBe("Account");
    }

    [Test]
    public void InScopeKeys_restricts_anchors_to_keys_in_the_map()
    {
        // Two un-invalidated bulk writes, Account and Person; only Account is in scope -> only Account flags.
        var graph = Graph(
            Edge("M:N.Importer.RunA", "M:N.AccountEntityCollection.UpdateMulti(System.Object)"),
            Edge("M:N.Importer.RunP", "M:N.PersonEntityCollection.UpdateMulti(System.Object)")
        );
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.RunA"),
            Anchor("N.PersonEntityCollection", "M:N.Importer.RunP"),
        };

        var spec = Spec() with { InScopeKeys = new Dictionary<string, string>(StringComparer.Ordinal) { ["Account"] = "high" } };
        var findings = FactCorrelationDeriver.Derive(graph, effects, spec);

        findings.Count.ShouldBe(1);
        findings[0].ResourceKey.ShouldBe("Account");
    }

    [Test]
    public void InScopeKeys_tiers_the_certainty_token_onto_the_finding()
    {
        // The declared-vs-inferred tier (high vs medium) rides from the map onto the finding's Certainty.
        var graph = Graph(
            Edge("M:N.Importer.RunA", "M:N.AccountEntityCollection.UpdateMulti(System.Object)"),
            Edge("M:N.Importer.RunO", "M:N.OrderEntityCollection.UpdateMulti(System.Object)")
        );
        var effects = new List<DerivedEffect>
        {
            Anchor("N.AccountEntityCollection", "M:N.Importer.RunA"),
            Anchor("N.OrderEntityCollection", "M:N.Importer.RunO"),
        };

        var spec = Spec() with
        {
            InScopeKeys = new Dictionary<string, string>(StringComparer.Ordinal) { ["Account"] = "high", ["Order"] = "medium" },
        };
        var findings = FactCorrelationDeriver.Derive(graph, effects, spec);

        findings.Single(f => f.ResourceKey == "Account").Certainty.ShouldBe("high");
        findings.Single(f => f.ResourceKey == "Order").Certainty.ShouldBe("medium");
    }
}
