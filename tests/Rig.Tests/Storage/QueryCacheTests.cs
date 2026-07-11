using Rig.Domain.Data;
using Rig.Storage.Queries;
using Shouldly;

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
        {
            node = new($"M:N{i}", EdgeKinds.Invocation, null, null, Children: [node]);
        }

        var blob = TreeCacheCodec.Encode(new TreeCachePayload([node], []));
        var back = TreeCacheCodec.Decode(blob);

        back.ShouldNotBeNull();
        var d = 0;
        for (var n = back.Forest[0]; ; n = n.Children[0], d++)
        {
            if (n.Children.Count == 0)
            {
                break;
            }
        }

        d.ShouldBe(depth);
    }

    [Test]
    public void EpSite_codec_round_trips_the_site_map()
    {
        var map = new Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>
        {
            [("Foo.cs", 10)] = ("echoactor", ["FrontEnd", "BackEnd"]),
            [("Bar.cs", 20)] = ("page", null),
        };

        var back = EpSiteCacheCodec.Decode(EpSiteCacheCodec.Encode(map));

        back.ShouldNotBeNull();
        back.Count.ShouldBe(2);
        back[("Foo.cs", 10)].Kind.ShouldBe("echoactor");
        back[("Foo.cs", 10)].Requires.ShouldBe(["FrontEnd", "BackEnd"]);
        back[("Bar.cs", 20)].Kind.ShouldBe("page");
        back[("Bar.cs", 20)].Requires.ShouldBeNull();
    }

    // Render data is split into two filter-keyed cache entries (see TreeCommand): SeamCodec (seam summaries,
    // filter-dependent) and LocationsCodec (file:line, filter-independent). Each round-trips independently.
    [Test]
    public void Seam_codec_round_trips_seam_effects()
    {
        var seam = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["M:App.Hub"] = ["💾 db:read ×3", "🌐 http:get ×1"],
            ["M:App.Hub2"] = [],
        };

        var back = SeamCodec.Decode(SeamCodec.Encode(seam));

        back.ShouldNotBeNull();
        back!["M:App.Hub"].ShouldBe(["💾 db:read ×3", "🌐 http:get ×1"]);
        back["M:App.Hub2"].ShouldBeEmpty();
    }

    [Test]
    public void Locations_codec_round_trips_file_and_line()
    {
        var locations = new Dictionary<string, (string? File, int Line)>(StringComparer.Ordinal)
        {
            ["M:App.Run"] = ("App/Run.cs", 12),
            ["M:App.Missing"] = (null, 0),
        };

        var back = LocationsCodec.Decode(LocationsCodec.Encode(locations));

        back.ShouldNotBeNull();
        back!["M:App.Run"].ShouldBe(("App/Run.cs", 12));
        back["M:App.Missing"].ShouldBe(((string?)null, 0));
    }

    [Test]
    public void Render_codecs_return_null_for_garbage()
    {
        SeamCodec.Decode([9, 9, 9]).ShouldBeNull();
        LocationsCodec.Decode([9, 9, 9]).ShouldBeNull();
        LibCallsCodec.Decode([9, 9, 9]).ShouldBeNull();
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
            {
                reopened.Get("key1").ShouldBe(blob);
            }

            // A reindex changes the store identity → Open purges the stale entry (auto-invalidation).
            using (var afterReindex = QueryCache.Open(dir, "store-B").ShouldNotBeNull())
            {
                afterReindex.Get("key1").ShouldBeNull();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
