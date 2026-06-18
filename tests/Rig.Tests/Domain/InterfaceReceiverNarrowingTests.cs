using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

// Interface-receiver dispatch narrowing. When a method is declared on a BASE interface but called through
// a more-derived SUB-interface receiver, the call binds to the base-interface member — so CHA fans out to
// EVERY implementer of the base interface, including implementers of unrelated SIBLING sub-interfaces.
// The static receiver type is known (the sub-interface), so dispatch must narrow to the receiver
// interface's own implementers. Real case: `IConfiguration : IPersistentState`; a `config.GetItem(...)`
// call (config : IConfiguration) bound to `IPersistentState.GetItem` over-fanned to WebConfiguration /
// PersistentApplicationConfiguration / WebApplicationState (which implement sibling IPersistent*State
// interfaces, not IConfiguration). `ResolveNarrowRoot` already returns the sub-interface as the narrow
// root; the gap was `NarrowByReceiver` testing membership via class base-edges only (`InNarrowSubtree`),
// which never matches interface implementers — fixed by `InReceiverScope` (the interface arm).
public sealed class InterfaceReceiverNarrowingTests
{
    //   interface IBase { M }            interface IDerived : IBase     interface ISibling : IBase
    //   class A : IDerived { M }         class B : ISibling { M }
    //   Caller.Go -> IBase.M  (mined impls: IBase.M -> A.M, IBase.M -> B.M)
    // `extraReceiver` sets the receiver type carried on the Caller.Go -> IBase.M edge (null = unknown).
    private static FactGraphData ThreeInterfaceShape(string? receiver)
    {
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.IBase.M", "invocation", "f.cs", 1, ReceiverType: receiver) };
        // ImplementsEdge models both class->interface and interface->interface ("interface" relations).
        var impls = new[]
        {
            new ImplementsEdge("T:N.A", "T:N.IDerived"),
            new ImplementsEdge("T:N.B", "T:N.ISibling"),
            new ImplementsEdge("T:N.IDerived", "T:N.IBase"),
            new ImplementsEdge("T:N.ISibling", "T:N.IBase"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.IBase.M", "M", "T:N.IBase"),
            new MethodRef("M:N.A.M", "M", "T:N.A"),
            new MethodRef("M:N.B.M", "M", "T:N.B"),
        };
        var mined = new[] { new DispatchFact("M:N.IBase.M", "M:N.A.M", "impl"), new DispatchFact("M:N.IBase.M", "M:N.B.M", "impl") };
        return new FactGraphData(edges, impls, methods, null, mined);
    }

    [Test]
    public void Sub_interface_receiver_narrows_to_the_receiver_interfaces_own_implementers()
    {
        // Receiver is IDerived; the call binds to IBase.M. Only A (implements IDerived) is a possible
        // runtime target — B (implements the SIBLING ISibling) must be dropped.
        var reach = FactPathFinder.Reaches(ThreeInterfaceShape("N.IDerived"), "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.A.M");
        reach.Keys.ShouldNotContain("M:N.B.M");
    }

    [Test]
    public void Unknown_receiver_keeps_the_full_cha_fan_out()
    {
        // No static receiver type → can't narrow → both implementers stay (recall-safe CHA superset).
        var reach = FactPathFinder.Reaches(ThreeInterfaceShape(receiver: null), "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.A.M");
        reach.Keys.ShouldContain("M:N.B.M");
    }

    [Test]
    public void Base_interface_receiver_does_not_narrow()
    {
        // Receiver typed as the declaring base interface itself: a call typed IBase genuinely could hit any
        // implementer, so no narrowing — full fan-out stands.
        var reach = FactPathFinder.Reaches(ThreeInterfaceShape("N.IBase"), "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.A.M");
        reach.Keys.ShouldContain("M:N.B.M");
    }

    [Test]
    public void BuildTree_attaches_only_the_receiver_interface_implementer()
    {
        var tree = FactPathFinder.BuildTree(ThreeInterfaceShape("N.IDerived"), "M:N.Caller.Go");

        var ibase = tree.Single(n => n.SymbolId == "M:N.Caller.Go").Children.Single(c => c.SymbolId == "M:N.IBase.M");
        ibase.Children.Select(c => c.SymbolId).ShouldContain("M:N.A.M");
        ibase.Children.Select(c => c.SymbolId).ShouldNotContain("M:N.B.M");
    }

    [Test]
    public void Subclass_of_a_receiver_interface_implementer_is_kept()
    {
        // C : A, A : IDerived — C implements IDerived transitively (via its base class). A receiver of
        // IDerived must keep C.M (a real runtime target), proving the interface arm composes with the
        // class base-edge subtree (ImplementsInterface walks base-edge descendants of an implementer).
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.IBase.M", "invocation", "f.cs", 1, ReceiverType: "N.IDerived") };
        var impls = new[]
        {
            new ImplementsEdge("T:N.A", "T:N.IDerived"),
            new ImplementsEdge("T:N.B", "T:N.ISibling"),
            new ImplementsEdge("T:N.IDerived", "T:N.IBase"),
            new ImplementsEdge("T:N.ISibling", "T:N.IBase"),
        };
        var bases = new[] { new BaseEdge("T:N.C", "T:N.A") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.IBase.M", "M", "T:N.IBase"),
            new MethodRef("M:N.A.M", "M", "T:N.A"),
            new MethodRef("M:N.B.M", "M", "T:N.B"),
            new MethodRef("M:N.C.M", "M", "T:N.C", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.IBase.M", "M:N.A.M", "impl"),
            new DispatchFact("M:N.IBase.M", "M:N.B.M", "impl"),
            new DispatchFact("M:N.IBase.M", "M:N.C.M", "impl"),
        };
        var reach = FactPathFinder.Reaches(new FactGraphData(edges, impls, methods, bases, mined), "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.A.M");
        reach.Keys.ShouldContain("M:N.C.M"); // subtype of the IDerived implementer — a real target
        reach.Keys.ShouldNotContain("M:N.B.M"); // sibling-interface implementer — dropped
    }

    [Test]
    public void Class_receiver_narrowing_is_unaffected()
    {
        // Regression: a CLASS receiver still narrows via base-edges exactly as before (the interface arm
        // is inert when narrowRoot is a class — ImplsByInterface has no entry for it).
        var edges = new[] { new CallEdge("M:N.Caller.Go", "M:N.Base.V", "invocation", "f.cs", 1, ReceiverType: "N.Leaf") };
        var bases = new[] { new BaseEdge("T:N.Mid", "T:N.Base"), new BaseEdge("T:N.Leaf", "T:N.Mid") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.V", "V", "T:N.Base"),
            new MethodRef("M:N.Mid.V", "V", "T:N.Mid", IsOverride: true),
            new MethodRef("M:N.Leaf.V", "V", "T:N.Leaf", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.Base.V", "M:N.Mid.V", "override"),
            new DispatchFact("M:N.Mid.V", "M:N.Leaf.V", "override"),
        };
        var reach = FactPathFinder.Reaches(new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined), "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Leaf.V");
        reach.Keys.ShouldNotContain("M:N.Mid.V");
    }
}
