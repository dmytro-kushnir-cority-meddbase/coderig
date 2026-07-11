using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Pins the rules-only builtin-rules.json additions from docs/backlog/todo/rules-only-effect-gaps.md
// (VS-G9, VS-G14). These construct FactEffectRule/FactInvocation fixtures directly rather than reading
// builtin-rules.json, so they exercise the SAME matching machinery (FactEffectDeriver.Derive) the real
// rules run through, without depending on the exact JSON shape.
public sealed class RulesOnlyEffectGapsTests
{
    // VS-G9: Microsoft.Extensions.Caching.Memory.CacheExtensions.Get/Set/GetOrCreate called directly on
    // an IMemoryCache (not via a first-party wrapper) must be classified the same as the wrapper's own
    // calls onto MemoryCache/IMemoryCache. Mirrors the shape of the shipped inproc_cache rules.
    private static readonly FactEffectRule CacheExtensionsWriteRule = new(
        "inproc_cache",
        "write",
        Methods: ["Set", "GetOrCreate", "GetOrCreateAsync"],
        DeclaringTypes: ["Microsoft.Extensions.Caching.Memory.CacheExtensions"],
        ReceiverTypes: [],
        Resource: "declaring_type"
    );

    private static readonly FactEffectRule CacheExtensionsReadRule = new(
        "inproc_cache",
        "read",
        Methods: ["Get", "TryGetValue"],
        DeclaringTypes: ["Microsoft.Extensions.Caching.Memory.CacheExtensions"],
        ReceiverTypes: [],
        Resource: "declaring_type"
    );

    private static FactInvocation Inv(string target) => new(target, "M:App.Caller", "f.cs", 1);

    [Test]
    public void CacheExtensions_Set_called_directly_on_IMemoryCache_is_an_inproc_cache_write()
    {
        var effect = FactEffectDeriver
            .Derive(
                [
                    Inv(
                        "M:Microsoft.Extensions.Caching.Memory.CacheExtensions.Set``1(Microsoft.Extensions.Caching.Memory.IMemoryCache,System.Object,``0)"
                    ),
                ],
                [CacheExtensionsWriteRule]
            )
            .ShouldHaveSingleItem();

        effect.Provider.ShouldBe("inproc_cache");
        effect.Operation.ShouldBe("write");
    }

    [Test]
    public void CacheExtensions_GetOrCreate_called_directly_on_IMemoryCache_is_an_inproc_cache_write()
    {
        // GetOrCreate both reads and may write on miss; the shipped wrapper rule classifies its own
        // GetOrCreate-equivalent as write, so the direct-call BCL path mirrors that split.
        var effect = FactEffectDeriver
            .Derive(
                [
                    Inv(
                        "M:Microsoft.Extensions.Caching.Memory.CacheExtensions.GetOrCreate``1(Microsoft.Extensions.Caching.Memory.IMemoryCache,System.Object,System.Func{Microsoft.Extensions.Caching.Memory.ICacheEntry,``0})"
                    ),
                ],
                [CacheExtensionsWriteRule]
            )
            .ShouldHaveSingleItem();

        effect.Operation.ShouldBe("write");
    }

    [Test]
    public void CacheExtensions_Get_called_directly_on_IMemoryCache_is_an_inproc_cache_read()
    {
        var effect = FactEffectDeriver
            .Derive(
                [
                    Inv(
                        "M:Microsoft.Extensions.Caching.Memory.CacheExtensions.Get(Microsoft.Extensions.Caching.Memory.IMemoryCache,System.Object)"
                    ),
                ],
                [CacheExtensionsReadRule]
            )
            .ShouldHaveSingleItem();

        effect.Operation.ShouldBe("read");
    }

    // VS-G14 (LanguageExt.HashMap as a process cache, e.g. PersonCache.GetPerson) was attempted and
    // REVERTED: calibrated against the real MedDBase store, LanguageExt.HashMap.Find/AddOrUpdate fired
    // 350 times, dominated by ordinary functional-collection usage (QueryGenerator.GetConditions,
    // EchoClusterConfProvider.Load, …) with no fact-based way to distinguish a cache-backing HashMap from
    // a plain one without flow analysis — out of scope. See docs/backlog/todo/rules-only-effect-gaps.md.
}
