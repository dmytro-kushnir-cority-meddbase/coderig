using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// ResourceKey.Of canonicalizes an effect's resolved resource into a comparable identity, so the two sides
// of a correlation (FR-7: bulk-write receiver vs cache-invalidation declaring type) compare equal. These
// pin the convention-stripping that was hand-rolled in the FR-7 deriver, now data-driven via NormalizeSpec.
public sealed class ResourceKeyTests
{
    private static readonly NormalizeSpec WriteSide = new(SimpleTypeName: true, StripSuffix: ["EntityCollection", "Collection", "DAO"]);
    private static readonly NormalizeSpec CacheSide = new(SimpleTypeName: true, StripSuffix: ["Cache"]);

    [Test]
    public void Write_receiver_and_cache_declaring_type_canonicalize_to_the_same_entity()
    {
        // The whole point: a bulk write on AccountEntityCollection and an invalidation on AccountCache
        // must agree on the resource key so the correlation deriver can pair them.
        ResourceKey.Of("AccountEntityCollection", WriteSide).ShouldBe("Account");
        ResourceKey.Of("AccountCache", CacheSide).ShouldBe("Account");
    }

    [Test]
    public void Longest_matching_suffix_wins_regardless_of_list_order()
    {
        // "EntityCollection" must beat the "Collection" that is a suffix-of-it, no matter the order given.
        ResourceKey.Of("PersonEntityCollection", new NormalizeSpec(StripSuffix: ["Collection", "EntityCollection"])).ShouldBe("Person");
        ResourceKey.Of("PersonCollection", new NormalizeSpec(StripSuffix: ["Collection", "EntityCollection"])).ShouldBe("Person");
    }

    [Test]
    public void Simple_type_name_drops_docid_prefix_namespace_and_generics()
    {
        ResourceKey.Of("T:MedDBase.CompanyEntity", new NormalizeSpec(SimpleTypeName: true)).ShouldBe("CompanyEntity");
        ResourceKey.Of("N.Foo.Account{T:N.X}", new NormalizeSpec(SimpleTypeName: true)).ShouldBe("Account");
        ResourceKey.Of("System.Collections.Generic.List`1", new NormalizeSpec(SimpleTypeName: true)).ShouldBe("List");
    }

    [Test]
    public void Identity_spec_returns_the_resource_unchanged()
    {
        ResourceKey.Of("CatalogContext.CatalogItems", NormalizeSpec.Identity).ShouldBe("CatalogContext.CatalogItems");
    }

    [Test]
    public void No_matching_suffix_passes_through()
    {
        ResourceKey.Of("Account", CacheSide).ShouldBe("Account");
    }

    [Test]
    public void Suffix_never_strips_to_an_empty_stem()
    {
        // "Cache" is exactly the suffix — the strict length guard leaves it intact rather than emptying it.
        ResourceKey.Of("Cache", CacheSide).ShouldBe("Cache");
    }

    [Test]
    public void Null_or_empty_input_yields_null()
    {
        ResourceKey.Of(null, CacheSide).ShouldBeNull();
        ResourceKey.Of("", CacheSide).ShouldBeNull();
    }
}
