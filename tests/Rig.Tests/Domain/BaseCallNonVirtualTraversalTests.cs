using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// A non-virtual `base.M()` call (CallEdge.NonVirtual=true) binds to exactly the base implementation and can
// never dispatch to a SIBLING override. The traversal must therefore resolve it to its static callee only,
// contributing NO override fan — FORWARD (reach from the sibling that makes the base call does not include
// the other sibling) and REVERSE (the base call does not make its source a reverse-reacher of the other
// sibling). It stays a real direct edge into the base BODY in both directions. This is the cheaper,
// provably-correct sub-fix for the reverse over-reach (docs/bug-callers-reverse-overreach.md, 2026-06-24).
public sealed class BaseCallNonVirtualTraversalTests
{
    // Base.M (virtual) overridden by Derived1.M and Derived2.M. Mined override dispatch facts make Base.M
    // CHA-fan to both. Derived1.M calls `base.M()` (the non-virtual edge under test, parameterised). A normal
    // virtual caller (Caller.Go -> Base.M) reaches Base.M so the base method has a genuine polymorphic caller.
    private static FactGraphData Graph(bool nonVirtualBaseCall)
    {
        var edges = new[]
        {
            // The base.M() call from inside Derived1.M's override — the edge under test.
            new CallEdge("M:N.Derived1.M", "M:N.Base.M", "invocation", "f.cs", 3, NonVirtual: nonVirtualBaseCall),
            // A genuinely polymorphic virtual caller of Base.M (no receiver type => CHA).
            new CallEdge("M:N.Caller.Go", "M:N.Base.M", "invocation", "f.cs", 9),
        };
        var bases = new[] { new BaseEdge("T:N.Derived1", "T:N.Base"), new BaseEdge("T:N.Derived2", "T:N.Base") };
        var methods = new[]
        {
            new MethodRef("M:N.Caller.Go", "Go", "T:N.Caller"),
            new MethodRef("M:N.Base.M", "M", "T:N.Base"),
            new MethodRef("M:N.Derived1.M", "M", "T:N.Derived1", IsOverride: true),
            new MethodRef("M:N.Derived2.M", "M", "T:N.Derived2", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.Base.M", "M:N.Derived1.M", "override"),
            new DispatchFact("M:N.Base.M", "M:N.Derived2.M", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    // ---- the precondition: the fan DOES exist (so suppressing it is a real change) ------------------

    [Test]
    public void Without_the_flag_the_base_call_reverse_fans_to_the_sibling_override()
    {
        // Old behaviour (NonVirtual=false): the base call is an ordinary invocation, so reverse traversal
        // climbs Derived2.M -> Base.M (override-dispatch) -> Derived1.M (the base.M() caller). The fan exists.
        var callers = FactPathFinder.ReachedBy(Graph(nonVirtualBaseCall: false), "M:N.Derived2.M");

        callers.Keys.ShouldContain("M:N.Derived1.M");
    }

    [Test]
    public void Without_the_flag_the_base_call_forward_fans_to_the_sibling_override()
    {
        // Old behaviour: reach from Derived1.M crosses the base call to Base.M, which then fans to ALL
        // overrides including the sibling Derived2.M.
        var reach = FactPathFinder.Reaches(Graph(nonVirtualBaseCall: false), "M:N.Derived1.M");

        reach.Keys.ShouldContain("M:N.Base.M");
        reach.Keys.ShouldContain("M:N.Derived2.M");
    }

    // ---- the fix: a non-virtual base call neither forward- nor reverse-fans to the sibling ----------

    [Test]
    public void Reverse_a_non_virtual_base_call_does_not_reach_the_sibling_override()
    {
        var callers = FactPathFinder.ReachedBy(Graph(nonVirtualBaseCall: true), "M:N.Derived2.M");

        callers.Keys.ShouldNotContain("M:N.Derived1.M");
    }

    [Test]
    public void Reverse_the_base_method_still_lists_its_base_caller_directly()
    {
        // Precision: keep the direct edge. `callers(Base.M)` is NOT reached via the override-dispatch fan,
        // so the base call IS a real direct caller of the base body and must still be listed.
        var callers = FactPathFinder.ReachedBy(Graph(nonVirtualBaseCall: true), "M:N.Base.M");

        callers.Keys.ShouldContain("M:N.Derived1.M");
        callers.Keys.ShouldContain("M:N.Caller.Go");
    }

    [Test]
    public void Forward_a_non_virtual_base_call_reaches_the_base_body_but_not_the_sibling()
    {
        var reach = FactPathFinder.Reaches(Graph(nonVirtualBaseCall: true), "M:N.Derived1.M");

        reach.Keys.ShouldContain("M:N.Base.M"); // the base body is still reached (direct edge kept)
        reach.Keys.ShouldNotContain("M:N.Derived2.M"); // but no sibling-override fan
    }

    [Test]
    public void Forward_a_genuinely_virtual_caller_still_fans_to_all_overrides()
    {
        // Recall-safe: Caller.Go's ordinary virtual call to Base.M is NOT non-virtual, so it still fans to
        // every override — the fix only suppresses the base-call edge, not the polymorphic call site.
        var reach = FactPathFinder.Reaches(Graph(nonVirtualBaseCall: true), "M:N.Caller.Go");

        reach.Keys.ShouldContain("M:N.Derived1.M");
        reach.Keys.ShouldContain("M:N.Derived2.M");
    }
}
