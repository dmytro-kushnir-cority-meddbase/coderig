using System.Linq;
using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;
using TUnit.Core;

namespace Rig.Tests.Domain;

public sealed class ContextDispatchNarrowingTests
{
    private static FactGraphData TwoFamilyGraph(string callerType)
    {
        var calls = new[]
        {
            new CallEdge("M:N.Root.Run", $"M:N.{callerType}.Init", "invocation", "f.cs", 10, ReceiverType: $"N.{callerType}"),
            new CallEdge($"M:N.{callerType}.Init", "M:N.IState.RegisterEvents", "invocation", "f.cs", 20, ReceiverType: "N.IState"),
        };
        var impls = new[] { new ImplementsEdge("T:N.S1", "T:N.IState"), new ImplementsEdge("T:N.S2", "T:N.IState") };
        var bases = new[] { new BaseEdge("T:N.S1", "T:N.StateBase{N.C1}"), new BaseEdge("T:N.S2", "T:N.StateBase{N.C2}") };
        var methods = new[]
        {
            new MethodRef("M:N.Root.Run", "Run", "T:N.Root"),
            new MethodRef("M:N.C1.Init", "Init", "T:N.C1"),
            new MethodRef("M:N.C2.Init", "Init", "T:N.C2"),
            new MethodRef("M:N.IState.RegisterEvents", "RegisterEvents", "T:N.IState"),
            new MethodRef("M:N.S1.RegisterEvents", "RegisterEvents", "T:N.S1"),
            new MethodRef("M:N.S2.RegisterEvents", "RegisterEvents", "T:N.S2"),
        };
        return new FactGraphData(calls, impls, methods, bases);
    }

    private static readonly FactContextDispatchRule[] StateRule = [new FactContextDispatchRule("IState", "StateBase")];

    private static TraceNode RegisterEventsNode(IReadOnlyList<TraceNode> roots)
    {
        TraceNode? found = null;
        void Walk(TraceNode n)
        {
            if (n.SymbolId == "M:N.IState.RegisterEvents")
            {
                found = n;
            }

            foreach (var c in n.Children)
            {
                Walk(c);
            }
        }
        foreach (var r in roots)
        {
            Walk(r);
        }

        found.ShouldNotBeNull();
        return found!;
    }

    [Test]
    public void Dispatch_narrows_to_the_enclosing_controllers_state_family()
    {
        var roots = FactPathFinder.BuildTree(TwoFamilyGraph("C1") with { ContextRules = StateRule }, "M:N.Root.Run");

        var reg = RegisterEventsNode(roots);
        reg.Children.ShouldContain(c => c.SymbolId == "M:N.S1.RegisterEvents");
        reg.Children.ShouldNotContain(c => c.SymbolId == "M:N.S2.RegisterEvents");
    }

    [Test]
    public void A_different_controller_narrows_to_its_own_family()
    {
        var roots = FactPathFinder.BuildTree(TwoFamilyGraph("C2") with { ContextRules = StateRule }, "M:N.Root.Run");

        var reg = RegisterEventsNode(roots);
        reg.Children.ShouldContain(c => c.SymbolId == "M:N.S2.RegisterEvents");
        reg.Children.ShouldNotContain(c => c.SymbolId == "M:N.S1.RegisterEvents");
    }

    [Test]
    public void Without_the_rule_dispatch_fans_to_all_implementers()
    {
        var roots = FactPathFinder.BuildTree(TwoFamilyGraph("C1"), "M:N.Root.Run");

        var reg = RegisterEventsNode(roots);
        reg.Children.ShouldContain(c => c.SymbolId == "M:N.S1.RegisterEvents");
        reg.Children.ShouldContain(c => c.SymbolId == "M:N.S2.RegisterEvents");
    }
}
