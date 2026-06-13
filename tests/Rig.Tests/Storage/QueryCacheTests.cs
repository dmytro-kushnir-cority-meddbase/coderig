using Rig.Domain.Data;
using Rig.Storage.Queries;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Storage;

public sealed class QueryCacheTests
{
    private static TreeCachePayload SamplePayload()
    {
        var forest = new TraceNode[]
        {
            new(
                "M:App.Run",
                EdgeKinds.Invocation,
                LoopKind: null,
                LoopDetail: null,
                Children:
                [
                    new(
                        "M:App.Do",
                        EdgeKinds.Invocation,
                        LoopKind: "foreach",
                        LoopDetail: "x in xs",
                        Children: [],
                        Fanout: 2,
                        CallSites: 3,
                        DispatchBasis: "roslyn"
                    ),
                ]
            ),
        };
        var effects = new DerivedEffect[]
        {
            new(
                "db",
                "read",
                "T:App.Thing",
                "M:App.Do",
                "App/Do.cs",
                42,
                [new EffectObservationInfo("looped_effect", "ctx", "detail", "high", "compilation", "reason")]
            ),
        };
        return new TreeCachePayload(forest, effects);
    }

    [Test]
    public void Codec_round_trips_a_forest_and_effects()
    {
        var payload = SamplePayload();

        var blob = TreeCacheCodec.Encode(payload);
        var back = TreeCacheCodec.Decode(blob);

        back.ShouldNotBeNull();
        back.Forest.Count.ShouldBe(1);
        var root = back.Forest[0];
        root.SymbolId.ShouldBe("M:App.Run");
        root.Children.Count.ShouldBe(1);
        var child = root.Children[0];
        child.SymbolId.ShouldBe("M:App.Do");
        child.LoopKind.ShouldBe("foreach");
        child.Fanout.ShouldBe(2);
        child.CallSites.ShouldBe(3);
        back.Effects.Count.ShouldBe(1);
        back.Effects[0].Provider.ShouldBe("db");
        back.Effects[0].Observations.ShouldNotBeNull().Count.ShouldBe(1);
        // Re-encoding the decoded payload reproduces the blob byte-for-byte (GZip is deterministic here).
        TreeCacheCodec.Encode(back).ShouldBe(blob);
    }

    [Test]
    public void Decode_returns_null_for_garbage_so_a_bad_blob_is_a_miss()
    {
        TreeCacheCodec.Decode([1, 2, 3, 4]).ShouldBeNull();
    }

    [Test]
    public void Codec_round_trips_a_forest_deeper_than_the_default_json_depth_limit()
    {
        // A call tree nesting >32 levels exceeds System.Text.Json's default MaxDepth of 64 (each tree
        // level = 2 JSON levels). Regression for the crash that surfaced on a deep real-world tree.
        const int depth = 200;
        TraceNode node = new("M:Leaf", EdgeKinds.Invocation, null, null, Children: []);
        for (var i = depth; i > 0; i--)
            node = new($"M:N{i}", EdgeKinds.Invocation, null, null, Children: [node]);

        var blob = TreeCacheCodec.Encode(new TreeCachePayload([node], []));
        var back = TreeCacheCodec.Decode(blob);

        back.ShouldNotBeNull();
        var d = 0;
        for (var n = back.Forest[0]; ; n = n.Children[0], d++)
            if (n.Children.Count == 0)
                break;
        d.ShouldBe(depth);
    }

    [Test]
    public void Cache_stores_and_retrieves_across_reopen_then_invalidates_on_store_change()
    {
        var dir = Directory.CreateTempSubdirectory("rig-cache-").FullName;
        try
        {
            var blob = TreeCacheCodec.Encode(SamplePayload());

            using (var cache = QueryCache.Open(dir, "store-A").ShouldNotBeNull())
            {
                cache.Put("key1", blob);
                cache.Get("key1").ShouldBe(blob);
            }

            // Same store identity → entry survives a reopen.
            using (var reopened = QueryCache.Open(dir, "store-A").ShouldNotBeNull())
                reopened.Get("key1").ShouldBe(blob);

            // A reindex changes the store identity → Open purges the stale entry (auto-invalidation).
            using (var afterReindex = QueryCache.Open(dir, "store-B").ShouldNotBeNull())
                afterReindex.Get("key1").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
