using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

public sealed class GenericDispatchSeamTests
{
    private const string UseFuncCache = "M:N.Zoo.UseFuncCache()";
    private const string UseNestedFuncCache = "M:N.Zoo.UseNestedFuncCache()";
    private const string UseRealCache = "M:N.Zoo.UseRealCache()";
    private const string CacheFuncGetResults = "M:N.CacheFunc.GetResults(System.Int32)";
    private const string CacheGetResults = "M:N.Cache.GetResults(System.Int32)";
    private const string CacheGetResult = "M:N.Cache.GetResult(System.Int32)";
    private const string InvoiceGetResult = "M:N.InvoiceCache.GetResult(System.Int32)";
    private const string AssemblyGetResult = "M:N.AssemblyCache.GetResult(System.Int32)";

    private static FactGraphData Graph()
    {
        var bases = new[]
        {
            new BaseEdge("T:N.Cache", "T:N.CacheBase"),
            new BaseEdge("T:N.CacheFunc", "T:N.CacheBase"),
            new BaseEdge("T:N.TxCache", "T:N.CacheFunc"),
            new BaseEdge("T:N.TxCache.Sub", "T:N.CacheFunc"),
            new BaseEdge("T:N.InvoiceCache", "T:N.Cache"),
            new BaseEdge("T:N.AssemblyCache", "T:N.Cache"),
        };
        var methods = new[]
        {
            new MethodRef(UseFuncCache, "UseFuncCache", "T:N.Zoo"),
            new MethodRef(UseNestedFuncCache, "UseNestedFuncCache", "T:N.Zoo"),
            new MethodRef(UseRealCache, "UseRealCache", "T:N.Zoo"),
            new MethodRef("M:N.CacheBase.Provide(System.Int32)", "Provide", "T:N.CacheBase"),
            new MethodRef("M:N.CacheBase.GetResults(System.Int32)", "GetResults", "T:N.CacheBase"),
            new MethodRef(CacheGetResults, "GetResults", "T:N.Cache", IsOverride: true),
            new MethodRef(CacheFuncGetResults, "GetResults", "T:N.CacheFunc", IsOverride: true),
            new MethodRef(CacheGetResult, "GetResult", "T:N.Cache"),
            new MethodRef(InvoiceGetResult, "GetResult", "T:N.InvoiceCache", IsOverride: true),
            new MethodRef(AssemblyGetResult, "GetResult", "T:N.AssemblyCache", IsOverride: true),
        };
        var dispatch = new[]
        {
            new DispatchFact("M:N.CacheBase.GetResults(System.Int32)", CacheGetResults, "override"),
            new DispatchFact("M:N.CacheBase.GetResults(System.Int32)", CacheFuncGetResults, "override"),
            new DispatchFact(CacheGetResult, InvoiceGetResult, "override"),
            new DispatchFact(CacheGetResult, AssemblyGetResult, "override"),
        };
        var edges = new[]
        {
            new CallEdge(UseFuncCache, "M:N.CacheBase.Provide(System.Int32)", "invocation", "f.cs", 10, ReceiverType: "N.TxCache<T, R>"),
            new CallEdge(
                UseNestedFuncCache,
                "M:N.CacheBase.Provide(System.Int32)",
                "invocation",
                "f.cs",
                20,
                ReceiverType: "N.TxCache<T, R>.Sub"
            ),
            new CallEdge(UseRealCache, "M:N.CacheBase.Provide(System.Int32)", "invocation", "f.cs", 30, ReceiverType: "N.InvoiceCache"),
            new CallEdge("M:N.CacheBase.Provide(System.Int32)", "M:N.CacheBase.GetResults(System.Int32)", "invocation", "f.cs", 40),
            new CallEdge(CacheGetResults, CacheGetResult, "invocation", "f.cs", 50),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, dispatch);
    }

    [Test]
    [Arguments(UseFuncCache)]
    [Arguments(UseNestedFuncCache)]
    public void A_CacheFunc_receiver_reaches_only_the_inherited_override_not_the_sibling(string entry)
    {
        var reach = FactPathFinder.Reaches(Graph(), entry);

        reach.Keys.ShouldContain(CacheFuncGetResults);
        reach.Keys.ShouldNotContain(CacheGetResults);
        reach.Keys.ShouldNotContain(CacheGetResult);
        reach.Keys.ShouldNotContain(InvoiceGetResult);
    }

    [Test]
    public void A_Cache_receiver_reaches_its_own_override_through_both_dispatch_seams()
    {
        // The receiver IS a concrete Cache subtype that INHERITS GetResults from Cache but OVERRIDES
        // GetResult. Two seams narrow: (1) CacheBase.GetResults -> the inherited Cache.GetResults (not
        // the CacheFunc sibling); (2) the dispatch must seed the concrete InvoiceCache as `this` for the
        // Cache.GetResults frame (not the less-derived declaring type Cache), so the deeper
        // Cache.GetResult dispatch narrows to InvoiceCache.GetResult — NOT the sibling AssemblyCache.
        var reach = FactPathFinder.Reaches(Graph(), UseRealCache);

        reach.Keys.ShouldContain(CacheGetResults);
        reach.Keys.ShouldContain(CacheGetResult);
        reach.Keys.ShouldContain(InvoiceGetResult); // its own override — reachable
        reach.Keys.ShouldNotContain(AssemblyGetResult); // sibling Cache override — seed must keep it out
        reach.Keys.ShouldNotContain(CacheFuncGetResults); // sibling of the GetResults seam — excluded
    }
}
