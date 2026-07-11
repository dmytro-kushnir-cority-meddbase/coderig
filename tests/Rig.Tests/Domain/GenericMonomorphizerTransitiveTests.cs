using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Phase 4 rework (docs/design-dispatch-precision.md "Revised plan, session 2"): TRANSITIVE + LAMBDA-CLOSURE
// monomorphization — the two real-world shapes v1 (direct concrete binding on the immediate edge into a
// generic; clone immediate body edges) could NOT reach:
//   (A) MULTI-HOP: a concrete binding at the root flows through a generic CALLER into a nested generic whose
//       binding is a FORWARDED token ("M:0"), and the type-param dispatch lives in that nested method's body.
//   (B) LAMBDA-CLOSURE: the type-param dispatch lives inside a LAMBDA sub-node of the generic method (the
//       dominant real shape — e.g. ExternalId.cs `items.Select(x => x.Save())`), so cloning only the
//       method's IMMEDIATE body edges misses it.
// Both assert the same narrowing contract as GenericMonomorphizerTests: the materialized graph reaches ONLY
// the concrete override bound at the root, not the decoy overrides. Bare display-FQN receiver convention.
public sealed class GenericMonomorphizerTransitiveTests
{
    private const string Animal = "N.Animal";
    private const string Cat = "N.Cat";
    private const string Dog = "N.Dog";

    private static BaseEdge[] AnimalBases() => new[] { new BaseEdge("T:" + Cat, "T:" + Animal), new BaseEdge("T:" + Dog, "T:" + Animal) };

    private static DispatchFact[] SpeakMined() =>
        new[]
        {
            new DispatchFact("M:N.Animal.Speak", "M:N.Cat.Speak", "override"),
            new DispatchFact("M:N.Animal.Speak", "M:N.Dog.Speak", "override"),
        };

    private static MethodRef[] SpeakMethods() =>
        new[]
        {
            new MethodRef("M:N.Animal.Speak", "Speak", "T:" + Animal),
            new MethodRef("M:N.Cat.Speak", "Speak", "T:" + Cat, IsOverride: true),
            new MethodRef("M:N.Dog.Speak", "Speak", "T:" + Dog, IsOverride: true),
        };

    private static FactGraphData Materialized(FactGraphData graph, Func<string, IReadOnlyList<string>> names)
    {
        var inventory = GenericInstantiationInventory.Build(graph);
        return GenericMonomorphizer.Materialize(graph, inventory, names);
    }

    // ---- (A) Transitive multi-hop: Root -> Outer<Cat> -> Inner<M:0=Cat> -> x.Speak() (receiver TInner) ----

    private static FactGraphData MultiHopGraph()
    {
        var edges = new[]
        {
            // Root calls the generic CALLER Outer<Cat> with a concrete method binding.
            new CallEdge("M:N.Root.Run", "M:N.Lib.Outer", "invocation", "f.cs", 1, MethodTypeArgBinding: "[\"C:" + Cat + "\"]"),
            // Outer's body calls the nested generic Inner, FORWARDING its own type-param (M:0) to Inner.
            new CallEdge("M:N.Lib.Outer", "M:N.Lib.Inner", "invocation", "f.cs", 2, MethodTypeArgBinding: "[\"M:0\"]"),
            // Inner's body: a virtual call into Animal.Speak whose receiver is Inner's type-param.
            new CallEdge("M:N.Lib.Inner", "M:N.Animal.Speak", "invocation", "f.cs", 3, ReceiverType: "TInner"),
        };
        var methods = new[]
        {
            new MethodRef("M:N.Root.Run", "Run", "T:N.Root"),
            new MethodRef("M:N.Lib.Outer", "Outer", "T:N.Lib"),
            new MethodRef("M:N.Lib.Inner", "Inner", "T:N.Lib"),
        }
            .Concat(SpeakMethods())
            .ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, AnimalBases(), SpeakMined());
    }

    private static IReadOnlyList<string> MultiHopNames(string id) =>
        id switch
        {
            "M:N.Lib.Outer" => new[] { "TOuter" },
            "M:N.Lib.Inner" => new[] { "TInner" },
            _ => Array.Empty<string>(),
        };

    [Test]
    public void MultiHop_original_graph_fans_to_all_decoys()
    {
        var reach = FactPathFinder.Reaches(MultiHopGraph(), "M:N.Root.Run");
        reach.Keys.ShouldContain("M:N.Cat.Speak");
        reach.Keys.ShouldContain("M:N.Dog.Speak");
    }

    [Test]
    public void MultiHop_materialized_narrows_through_the_forwarded_binding()
    {
        var reach = FactPathFinder.Reaches(Materialized(MultiHopGraph(), MultiHopNames), "M:N.Root.Run");

        // Cat flowed Root -> Outer<Cat> -> Inner<Cat> (forwarded), so Inner's body Speak narrows to Cat.Speak.
        reach.Keys.ShouldContain("M:N.Cat.Speak");
        reach.Keys.ShouldNotContain("M:N.Dog.Speak");
    }

    // ---- (B) Lambda-closure: Root -> M<Cat>; M's body has a lambda whose body calls x.Speak() (receiver T) --

    private static FactGraphData LambdaGraph()
    {
        var edges = new[]
        {
            new CallEdge("M:N.Root.Run", "M:N.Lib.M", "invocation", "f.cs", 1, MethodTypeArgBinding: "[\"C:" + Cat + "\"]"),
            // M's body hands a lambda (its sub-node M:N.Lib.M~λ0) to some enumerator (a methodGroup edge).
            new CallEdge("M:N.Lib.M", "M:N.Lib.M~λ0", "methodGroup", "f.cs", 2),
            // The LAMBDA body does the type-param dispatch (receiver is M's type-param, closed over).
            new CallEdge("M:N.Lib.M~λ0", "M:N.Animal.Speak", "invocation", "f.cs", 3, ReceiverType: "T"),
        };
        var methods = new[] { new MethodRef("M:N.Root.Run", "Run", "T:N.Root"), new MethodRef("M:N.Lib.M", "M", "T:N.Lib") }
            .Concat(SpeakMethods())
            .ToArray();
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, AnimalBases(), SpeakMined());
    }

    private static IReadOnlyList<string> LambdaNames(string id) => id == "M:N.Lib.M" ? new[] { "T" } : Array.Empty<string>();

    [Test]
    public void Lambda_original_graph_fans_to_all_decoys()
    {
        var reach = FactPathFinder.Reaches(LambdaGraph(), "M:N.Root.Run");
        reach.Keys.ShouldContain("M:N.Cat.Speak");
        reach.Keys.ShouldContain("M:N.Dog.Speak");
    }

    [Test]
    public void Lambda_materialized_narrows_inside_the_lambda_closure()
    {
        var reach = FactPathFinder.Reaches(Materialized(LambdaGraph(), LambdaNames), "M:N.Root.Run");

        // The lambda closes over M's type-param T=Cat, so the lambda body's Speak narrows to Cat.Speak.
        reach.Keys.ShouldContain("M:N.Cat.Speak");
        reach.Keys.ShouldNotContain("M:N.Dog.Speak");
    }
}
