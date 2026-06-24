using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Per-EDGE reverse-dispatch narrowing (the god-seam fix, docs/bug-callers-reverse-overreach.md). The
// reverse `callers`/`--entrypoints` traversal used to narrow dispatch PER-METHOD-AGGREGATE: it pooled
// every receiver across ALL call sites of a hub method, so for a base method overridden by N types the
// reverse fan flooded ALL direct callers of the hub up to EVERY override — the 3,307-spurious-caller
// over-approximation. The fix builds the reverse-dispatch map by INVERTING forward's own per-call-edge
// DispatchTargets(B, R): a caller is attributed only to the override ITS receiver resolves to. This is
// the precise mirror of the forward Successors narrowing. These synthetic-graph tests pin the unit-level
// behaviour: wrong-receiver callers are excluded; a null/unreliable receiver falls back to full CHA
// (both callers kept); a base.M() NonVirtual call never reverse-reaches a sibling override; and the
// receiver-BLIND (narrowDispatch:false) superset still yields the full hub-fan unchanged.
public sealed class ReverseReceiverNarrowingTests
{
    //   class Animal { virtual Speak }   class Dog : Animal { override Speak }   class Cat : Animal { override Speak }
    //   CallerA.Go -> Animal.Speak  (receiver = Dog)     CallerB.Go -> Animal.Speak  (receiver = Cat)
    // Mined override facts make Animal.Speak CHA-fan to both Dog.Speak and Cat.Speak. `dogReceiver` /
    // `catReceiver` parameterise the receiver type carried on each caller's edge (null => CHA fallback);
    // `callerANonVirtual` makes CallerA's edge a non-virtual `base.Speak()` call.
    private static FactGraphData Graph(string? dogReceiver = "N.Dog", string? catReceiver = "N.Cat", bool callerANonVirtual = false)
    {
        var edges = new[]
        {
            new CallEdge(
                "M:N.CallerA.Go",
                "M:N.Animal.Speak",
                "invocation",
                "f.cs",
                1,
                ReceiverType: dogReceiver,
                NonVirtual: callerANonVirtual
            ),
            new CallEdge("M:N.CallerB.Go", "M:N.Animal.Speak", "invocation", "f.cs", 2, ReceiverType: catReceiver),
        };
        var bases = new[] { new BaseEdge("T:N.Dog", "T:N.Animal"), new BaseEdge("T:N.Cat", "T:N.Animal") };
        var methods = new[]
        {
            new MethodRef("M:N.CallerA.Go", "Go", "T:N.CallerA"),
            new MethodRef("M:N.CallerB.Go", "Go", "T:N.CallerB"),
            new MethodRef("M:N.Animal.Speak", "Speak", "T:N.Animal"),
            new MethodRef("M:N.Dog.Speak", "Speak", "T:N.Dog", IsOverride: true),
            new MethodRef("M:N.Cat.Speak", "Speak", "T:N.Cat", IsOverride: true),
        };
        var mined = new[]
        {
            new DispatchFact("M:N.Animal.Speak", "M:N.Dog.Speak", "override"),
            new DispatchFact("M:N.Animal.Speak", "M:N.Cat.Speak", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    [Test]
    public void Per_edge_narrowing_includes_the_matching_receiver_caller()
    {
        // CallerA's receiver is Dog, so it reverse-reaches Dog.Speak via the one dispatch hop.
        var callers = FactPathFinder.ReachedBy(Graph(), "M:N.Dog.Speak", narrowDispatch: true);

        callers.Keys.ShouldContain("M:N.CallerA.Go");
    }

    [Test]
    public void Per_edge_narrowing_excludes_the_wrong_receiver_caller()
    {
        // The god-seam fix: CallerB's receiver is Cat, so it must NOT reverse-reach Dog.Speak. Under the old
        // per-method-aggregate narrowing it rode the hub fan and was wrongly reported.
        var callers = FactPathFinder.ReachedBy(Graph(), "M:N.Dog.Speak", narrowDispatch: true);

        callers.Keys.ShouldNotContain("M:N.CallerB.Go");
    }

    [Test]
    public void The_other_override_gets_its_own_matching_caller_only()
    {
        // Symmetric: Cat.Speak is reverse-reached by CallerB (receiver Cat) and not CallerA (receiver Dog).
        var callers = FactPathFinder.ReachedBy(Graph(), "M:N.Cat.Speak", narrowDispatch: true);

        callers.Keys.ShouldContain("M:N.CallerB.Go");
        callers.Keys.ShouldNotContain("M:N.CallerA.Go");
    }

    [Test]
    public void Null_receiver_falls_back_to_cha_and_keeps_both_callers()
    {
        // CallerA has a null receiver (unresolved) => DispatchTargets(Animal.Speak, null) = full CHA, so it
        // reverse-reaches BOTH overrides. Recall is preserved exactly as the forward walk does on a null
        // receiver. (CallerB keeps its Cat receiver and stays narrowed to Cat.Speak.)
        var callers = FactPathFinder.ReachedBy(Graph(dogReceiver: null), "M:N.Dog.Speak", narrowDispatch: true);

        callers.Keys.ShouldContain("M:N.CallerA.Go"); // CHA fallback keeps the unresolved-receiver caller
        callers.Keys.ShouldNotContain("M:N.CallerB.Go"); // CallerB's Cat receiver still excludes it from Dog.Speak
    }

    [Test]
    public void Non_virtual_base_call_does_not_reverse_reach_a_sibling_override()
    {
        // CallerA's edge into Animal.Speak is a non-virtual `base.Speak()` (NonVirtual=true). A base call binds
        // to exactly the base body and can never run a sibling override, so it contributes NO reverse-dispatch
        // fan — CallerA must not reverse-reach Cat.Speak (nor, here, Dog.Speak via the fan).
        var callersOfCat = FactPathFinder.ReachedBy(Graph(callerANonVirtual: true), "M:N.Cat.Speak", narrowDispatch: true);
        callersOfCat.Keys.ShouldNotContain("M:N.CallerA.Go");

        // But the base BODY is still a direct caller of Animal.Speak (the direct edge is kept).
        var callersOfAnimal = FactPathFinder.ReachedBy(Graph(callerANonVirtual: true), "M:N.Animal.Speak", narrowDispatch: true);
        callersOfAnimal.Keys.ShouldContain("M:N.CallerA.Go");
    }

    [Test]
    public void Receiver_blind_mode_yields_the_full_hub_fan_unchanged()
    {
        // narrowDispatch:false is the sound superset (blast-radius/dead-code/SQL-equivalence oracle). It must
        // keep the receiver-BLIND per-node hub fan: BOTH callers reverse-reach BOTH overrides, regardless of
        // their distinct receivers — the same behaviour as before the per-edge narrowing fix.
        var callersOfDog = FactPathFinder.ReachedBy(Graph(), "M:N.Dog.Speak", narrowDispatch: false);
        callersOfDog.Keys.ShouldContain("M:N.CallerA.Go");
        callersOfDog.Keys.ShouldContain("M:N.CallerB.Go"); // blind mode keeps the wrong-receiver caller (full fan)

        var callersOfCat = FactPathFinder.ReachedBy(Graph(), "M:N.Cat.Speak", narrowDispatch: false);
        callersOfCat.Keys.ShouldContain("M:N.CallerA.Go");
        callersOfCat.Keys.ShouldContain("M:N.CallerB.Go");
    }
}
