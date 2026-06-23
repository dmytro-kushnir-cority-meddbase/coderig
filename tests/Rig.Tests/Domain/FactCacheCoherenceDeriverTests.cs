using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

public sealed class FactCacheCoherenceDeriverTests
{
    private static MethodRef M(string id) => new(id, id, null);

    private static FactGraphData Graph(params CallEdge[] edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.Caller, e.Callee }).Distinct(StringComparer.Ordinal).Select(M).ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), nodes);
    }

    [Test]
    public void Flags_a_bulk_write_with_no_reachable_invalidation()
    {
        var graph = Graph(
            new CallEdge(
                Caller: "M:N.Importer.Run",
                Callee: "M:N.AccountEntityCollection.UpdateMulti(System.Object)",
                Kind: "invocation",
                FilePath: "f.cs",
                Line: 42,
                ReceiverType: "N.AccountEntityCollection"
            )
        );

        var findings = FactCacheCoherenceDeriver.DeriveCacheCoherence(
            graph,
            cachedEntities: new HashSet<string>(StringComparer.Ordinal) { "Account" },
            bulkWriteMethods: ["UpdateMulti", "DeleteMulti"],
            invalidationMethods: ["Remove", "Clear"]
        );

        findings.Count.ShouldBe(1);
        findings[0].Entity.ShouldBe("Account");
        findings[0].Method.ShouldContain("Importer.Run");
        findings[0].Line.ShouldBe(42);
    }

    [Test]
    public void Clean_when_an_invalidation_is_reachable()
    {
        var graph = Graph(
            new CallEdge(
                Caller: "M:N.Importer.Run",
                Callee: "M:N.AccountEntityCollection.UpdateMulti(System.Object)",
                Kind: "invocation",
                FilePath: "f.cs",
                Line: 42,
                ReceiverType: "N.AccountEntityCollection"
            ),
            new CallEdge(
                Caller: "M:N.Importer.Run",
                Callee: "M:N.AccountCache.Clear",
                Kind: "invocation",
                FilePath: "f.cs",
                Line: 43,
                ReceiverType: "N.AccountCache"
            )
        );

        var findings = FactCacheCoherenceDeriver.DeriveCacheCoherence(
            graph,
            cachedEntities: new HashSet<string>(StringComparer.Ordinal) { "Account" },
            bulkWriteMethods: ["UpdateMulti", "DeleteMulti"],
            invalidationMethods: ["Remove", "Clear"]
        );

        findings.ShouldBeEmpty();
    }

    [Test]
    public void A_bulk_write_of_a_non_cached_entity_is_not_flagged()
    {
        var graph = Graph(
            new CallEdge(
                Caller: "M:N.Importer.Run",
                Callee: "M:N.PersonEventDAO.DeleteMulti(System.Object)",
                Kind: "invocation",
                FilePath: "f.cs",
                Line: 17,
                ReceiverType: "N.PersonEventDAO"
            )
        );

        var findings = FactCacheCoherenceDeriver.DeriveCacheCoherence(
            graph,
            cachedEntities: new HashSet<string>(StringComparer.Ordinal) { "Account" },
            bulkWriteMethods: ["UpdateMulti", "DeleteMulti"],
            invalidationMethods: ["Remove", "Clear"]
        );

        findings.ShouldBeEmpty();
    }
}
