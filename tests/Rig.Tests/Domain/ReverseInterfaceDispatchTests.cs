using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Reverse-dispatch narrowing across a cleanly-typed INTERFACE-dispatched call. The reverse mirror of
// InterfaceReceiverNarrowingTests: forward reach narrows an interface receiver to its own implementers
// via InReceiverScope's interface arm; the reverse narrowing (ReverseDispatchReaches) only checked class
// base-edges (Descendants/DescendantsContainStripped), so when the call-site receiver is an interface
// (e.g. `IHealthcodeService`) and the dispatch target's declaring type relates to it via an IMPLEMENTS
// edge (`Master : IHealthcodeService`), the base-edge geometry found no relationship and pruned the
// legitimate reverse-dispatch edge — under-reporting every entry point whose only path to a target
// crossed that interface call. Fixed by adding the interface arm (ImplementsInterface) to the reverse
// narrowing, mirroring the forward path. Real case: InvoiceMain handlers -> IHealthcodeService.Add... ->
// Master.Add... -> CompanyCache.IfCanView were absent from `rig callers ... --entrypoints`.
public sealed class ReverseInterfaceDispatchTests
{
    //   interface IService { Add }        class Master : IService { Add }
    //   Entry.Handle -> IService.Add  (receiver typed IService) ; Master.Add -> Effect.Leaf
    // `receiver` sets the receiver type carried on the Entry.Handle -> IService.Add edge.
    private static FactGraphData InterfaceDispatchShape(string? receiver)
    {
        var edges = new[]
        {
            new CallEdge("M:N.Entry.Handle", "M:N.IService.Add", "invocation", "f.cs", 1, ReceiverType: receiver),
            new CallEdge("M:N.Master.Add", "M:N.Effect.Leaf", "invocation", "f.cs", 2),
        };
        // ImplementsEdge models the class->interface relation: Master implements IService.
        var impls = new[] { new ImplementsEdge("T:N.Master", "T:N.IService") };
        var methods = new[]
        {
            new MethodRef("M:N.Entry.Handle", "Handle", "T:N.Entry"),
            new MethodRef("M:N.IService.Add", "Add", "T:N.IService"),
            new MethodRef("M:N.Master.Add", "Add", "T:N.Master"),
            new MethodRef("M:N.Effect.Leaf", "Leaf", "T:N.Effect"),
        };
        var mined = new[] { new DispatchFact("M:N.IService.Add", "M:N.Master.Add", "impl") };
        return new FactGraphData(edges, impls, methods, null, mined);
    }

    [Test]
    public void Reverse_reach_crosses_a_cleanly_typed_interface_call_to_find_the_entry_point()
    {
        // Receiver is the interface IService (a clean, resolved type — not unreliable), so the reverse walk
        // takes the NARROWING path, not the CHA fallback. From the leaf, the reverse closure must climb
        // through Master.Add -> IService.Add (reverse dispatch, gated by ReverseDispatchReaches) -> the
        // entry point Entry.Handle. This is the edge the missing interface arm used to prune.
        var reached = FactPathFinder.ReachedBy(InterfaceDispatchShape("N.IService"), "M:N.Effect.Leaf", narrowDispatch: true);

        reached.Keys.ShouldContain("M:N.Master.Add"); // direct caller of the leaf
        reached.Keys.ShouldContain("M:N.IService.Add"); // reverse dispatch: impl reaches its interface decl
        reached.Keys.ShouldContain("M:N.Entry.Handle"); // ...and the entry point above the interface call
    }

    [Test]
    public void Without_the_interface_arm_the_narrowing_path_prunes_the_entry_point()
    {
        // The symmetric assertion that the test exercises the interface-receiver NARROWING path (not the
        // AnyUnreliable fallback): with narrowing OFF the full CHA reverse closure trivially keeps the edge,
        // so the with/without-narrowing results must AGREE here. They do only because the fix's interface
        // arm now lets the cleanly-typed interface receiver narrow-and-keep instead of prune. (Before the
        // fix, narrow-on dropped IService.Add + Entry.Handle while narrow-off kept them — the asymmetry
        // that was the bug.)
        var graph = InterfaceDispatchShape("N.IService");
        var narrowed = FactPathFinder.ReachedBy(graph, "M:N.Effect.Leaf", narrowDispatch: true);
        var cha = FactPathFinder.ReachedBy(graph, "M:N.Effect.Leaf", narrowDispatch: false);

        cha.Keys.ShouldContain("M:N.Entry.Handle"); // CHA superset always keeps it
        narrowed.Keys.ShouldContain("M:N.Entry.Handle"); // narrowing must now agree (the fix)
    }

    [Test]
    public void Unreliable_receiver_keeps_the_entry_point_via_the_cha_fallback()
    {
        // Null receiver -> AnyUnreliable -> ReverseDispatchReaches returns true (CHA fallback). Recall is
        // preserved on unresolved receivers regardless of the interface arm; the bug only ever bit
        // cleanly-typed interface receivers.
        var reached = FactPathFinder.ReachedBy(InterfaceDispatchShape(receiver: null), "M:N.Effect.Leaf", narrowDispatch: true);

        reached.Keys.ShouldContain("M:N.Entry.Handle");
    }
}
