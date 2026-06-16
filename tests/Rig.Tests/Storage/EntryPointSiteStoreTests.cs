using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Shouldly;

namespace Rig.Tests.Storage;

public sealed class EntryPointSiteStoreTests
{
    [Test]
    public async Task Persist_then_load_round_trips_under_matching_rules_hash_and_misses_on_mismatch()
    {
        var dir = Directory.CreateTempSubdirectory("rig-epsites-").FullName;
        var dbPath = Path.Combine(dir, "rig.db");
        try
        {
            var sites = new Dictionary<(string File, int Line), (string Kind, IReadOnlyList<string>? Requires)>
            {
                [("Pages/Home.cs", 10)] = ("page", ["FrontEnd"]),
                [("Services/Sync.cs", 42)] = ("background", ["DataServer", "FrontEnd"]),
                [("Util/Helper.cs", 7)] = ("action", null),
            };

            await using (var write = new RigDbContext(dbPath, pooling: false))
            {
                await EntryPointSiteStore.PersistAsync(write, sites, "hash-A");
            }

            await using (var read = new RigDbContext(dbPath, pooling: false))
            {
                var back = (await EntryPointSiteStore.LoadAsync(read, "hash-A")).ShouldNotBeNull();
                back.Count.ShouldBe(3);
                back[("Pages/Home.cs", 10)].Kind.ShouldBe("page");
                back[("Pages/Home.cs", 10)].Requires.ShouldBe(["FrontEnd"]);
                back[("Services/Sync.cs", 42)].Requires.ShouldBe(["DataServer", "FrontEnd"]);
                back[("Util/Helper.cs", 7)].Requires.ShouldBeNull();

                // A different effective rule set (e.g. a --rules query) must MISS so the caller derives live.
                (await EntryPointSiteStore.LoadAsync(read, "hash-B")).ShouldBeNull();
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_returns_null_when_not_materialized()
    {
        var dir = Directory.CreateTempSubdirectory("rig-epsites-none-").FullName;
        try
        {
            await using var ctx = new RigDbContext(Path.Combine(dir, "rig.db"), pooling: false);
            (await EntryPointSiteStore.LoadAsync(ctx, "any")).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
