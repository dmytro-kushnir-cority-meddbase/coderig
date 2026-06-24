using Rig.Domain.Data;
using Rig.Domain.Functions;
using Rig.Storage.Queries;
using Rig.Storage.Storage;
using Rig.Tests.Fixtures;
using Shouldly;

namespace Rig.Tests.Domain;

// Phase 4a of static monomorphization (docs/design-dispatch-precision.md): the load-time materialization is
// wired into the SINGLE shaping pass FactPathFinder.ShapeGraph, behind the `monomorphizeSignatures` seam
// (loaders pass it non-null only when RIG_MONOMORPHIZE is on). These tests prove the seam THROUGH the real
// ShapeGraph (not by calling Materialize directly): default (null) is unchanged, non-null produces the
// `~mono` split + narrows to the concrete override, and cut/context rules still attach. Synthetic graph +
// signature map mirror GenericMonomorphizerTests — no DB.
// The two Reads-level tests reuse the session-shared AnalyzedPlaygrounds store (mirrors LoadShapedGraphTests).
[ClassDataSource<AnalyzedPlaygrounds>(Shared = SharedType.PerTestSession)]
public sealed class MonomorphizeShapeGraphTests(AnalyzedPlaygrounds playgrounds)
{
    private const string EntityBase = "N.EntityBase";
    private const string BillingRule = "N.BillingRuleEntity";
    private const string Contact = "N.ContactEntity";
    private const string Company = "N.CompanyEntity";

    private const string SaveServices = "M:N.Repo.SaveServices";

    // Mirror of GenericMonomorphizerTests.MethodGenericGraph: a generic method `SaveServices<TEntity, Tv>`
    // whose body virtual-calls EntityBase.Delete with a type-param receiver ("TEntity"). EntityBase has 3
    // concrete overriders (BillingRule/Contact/Company decoys). The caller binds TEntity=BillingRuleEntity.
    private static FactGraphData MethodGenericGraph()
    {
        var edges = new[]
        {
            new CallEdge(
                "M:N.Caller.DoIt",
                SaveServices,
                "invocation",
                "f.cs",
                1,
                MethodTypeArgBinding: "[\"C:" + BillingRule + "\",\"C:int\"]"
            ),
            new CallEdge(SaveServices, "M:N.EntityBase.Delete", "invocation", "f.cs", 9, ReceiverType: "TEntity"),
        };
        var bases = new[]
        {
            new BaseEdge("T:" + BillingRule, "T:" + EntityBase),
            new BaseEdge("T:" + Contact, "T:" + EntityBase),
            new BaseEdge("T:" + Company, "T:" + EntityBase),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.DoIt", "DoIt", "T:N.Caller"),
            new MethodRef(SaveServices, "SaveServices", "T:N.Repo"),
            new MethodRef("M:N.EntityBase.Delete", "Delete", "T:" + EntityBase),
            new MethodRef("M:N.BillingRuleEntity.Delete", "Delete", "T:" + BillingRule, IsOverride: true),
            new MethodRef("M:N.ContactEntity.Delete", "Delete", "T:" + Contact, IsOverride: true),
            new MethodRef("M:N.CompanyEntity.Delete", "Delete", "T:" + Company, IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.EntityBase.Delete", "M:N.BillingRuleEntity.Delete", "override"),
            new DispatchFact("M:N.EntityBase.Delete", "M:N.ContactEntity.Delete", "override"),
            new DispatchFact("M:N.EntityBase.Delete", "M:N.CompanyEntity.Delete", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    // The signature map ShapeGraph would mine from symbol_facts.Signature when the flag is on: the generic
    // method's signature carries the ordered type-params <TEntity, Tv> before its first top-level `(`.
    private static IReadOnlyDictionary<string, string> SaveServicesSignatures() =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [SaveServices] = "void N.Repo.SaveServices<TEntity, Tv>(TEntity entity, Tv value)",
        };

    // ---- (1) default path (null seam) is unchanged ---------------------------------------------------

    [Test]
    public void Default_null_seam_produces_no_mono_nodes_and_preserves_the_edge_set()
    {
        var graph = MethodGenericGraph();

        var shaped = FactPathFinder.ShapeGraph(graph, [], [], [], monomorphizeSignatures: null);

        // No materialization: no node id carries the ~mono marker.
        shaped.CallEdges.ShouldNotContain(e => MonomorphizedNodeId.IsMonomorphized(e.Caller));
        shaped.CallEdges.ShouldNotContain(e => MonomorphizedNodeId.IsMonomorphized(e.Callee));

        // With no factory/cut/context rules and a null seam, the call-edge set equals the unshaped graph's.
        shaped.CallEdges.ShouldBe(graph.CallEdges);
    }

    // ---- (2) non-null seam materializes + narrows through the real ShapeGraph + Phase-3 collapse --------

    [Test]
    public void Nonnull_seam_materializes_then_narrows_to_the_single_concrete_override()
    {
        var graph = MethodGenericGraph();

        var shaped = FactPathFinder.ShapeGraph(graph, [], [], [], monomorphizeSignatures: SaveServicesSignatures());

        // The seam produced ~mono split nodes.
        shaped.CallEdges.ShouldContain(e => MonomorphizedNodeId.IsMonomorphized(e.Caller));

        // The full Phase-2+3+4 path: walk the REAL ReachesWithFanout over the shaped graph, then collapse the
        // ~mono ids back to base. The body's receiver is now concrete BillingRuleEntity, so dispatch narrows
        // to ONLY its override; the Contact/Company decoys are absent.
        var reach = FactPathFinder.ReachesWithFanout(shaped, "M:N.Caller.DoIt");
        var collapsed = MonomorphCollapse.CollapseReachInfo(reach);

        collapsed.Keys.ShouldContain("M:N.BillingRuleEntity.Delete");
        collapsed.Keys.ShouldNotContain("M:N.ContactEntity.Delete");
        collapsed.Keys.ShouldNotContain("M:N.CompanyEntity.Delete");

        // Phase-3 collapse folds every ~mono id back to its base — no ~mono key survives into the result.
        collapsed.Keys.ShouldNotContain(k => MonomorphizedNodeId.IsMonomorphized(k));
    }

    // ---- (3) cut/context rules still attach when the seam is non-null --------------------------------

    [Test]
    public void Cut_rule_still_attaches_when_monomorphize_seam_is_nonnull()
    {
        var graph = MethodGenericGraph();
        var cutRules = new[] { new FactTraversalCutRule("M:N.Some.Reflection", "reflection") };

        var shaped = FactPathFinder.ShapeGraph(graph, [], cutRules, [], monomorphizeSignatures: SaveServicesSignatures());

        // Materialization happened AND the cut rule landed on the returned graph.
        shaped.CallEdges.ShouldContain(e => MonomorphizedNodeId.IsMonomorphized(e.Caller));
        shaped.CutRules.ShouldNotBeNull();
        shaped.CutRules!.ShouldContain(cutRules[0]);
    }

    // ---- (4) Reads-level signature + flag wiring (real store-backed, mirrors LoadShapedGraphTests) -----

    // LoadSymbolSignaturesAsync returns an id->Signature map over method+type symbols; the playground has
    // many methods, so the map is non-empty and every key is a known M:/T: symbol with a (possibly empty)
    // signature value.
    [Test]
    public async Task LoadSymbolSignatures_returns_method_and_type_signature_rows()
    {
        var playground = await playgrounds.LegacyNet48Async();
        var signatures = await LoadFromStoreAsync(playground.Result, context => Reads.LoadSymbolSignaturesAsync(context));

        signatures.ShouldNotBeEmpty();
        signatures.Keys.ShouldContain(k => k.StartsWith("M:", StringComparison.Ordinal) || k.StartsWith("T:", StringComparison.Ordinal));
    }

    // LoadMonomorphizationSignaturesAsync returns NULL when RIG_MONOMORPHIZE is unset — the default-OFF
    // guarantee (no DB query, no materialization). The env var is unset in the test process.
    [Test]
    public async Task LoadMonomorphizationSignatures_is_null_when_flag_unset()
    {
        Environment.GetEnvironmentVariable("RIG_MONOMORPHIZE").ShouldBeNullOrEmpty();

        var playground = await playgrounds.LegacyNet48Async();
        var monoSigs = await LoadFromStoreAsync(playground.Result, context => Reads.LoadMonomorphizationSignaturesAsync(context));

        monoSigs.ShouldBeNull();
    }

    private static async Task<T> LoadFromStoreAsync<T>(AnalysisResult result, Func<RigDbContext, Task<T>> load)
    {
        var dir = Path.Combine(Path.GetTempPath(), "rig-monoseam-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        var dbPath = Path.Combine(dir, "rig.db");
        try
        {
            await using (var write = new RigDbContext(dbPath, pooling: false))
            {
                await Writes.SaveAsync(write, result);
            }

            await using var read = new RigDbContext(dbPath, pooling: false);
            return await load(read);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch
            { /* best-effort */
            }
        }
    }
}
