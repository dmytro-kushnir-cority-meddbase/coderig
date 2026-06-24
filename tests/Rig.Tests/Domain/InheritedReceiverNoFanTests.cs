using Rig.Domain.Data;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Domain;

// Mechanism #3 (docs/bug-callers-reverse-overreach.md): a virtual call `x.M()` where the receiver `x` is a
// reliable first-party type that has NO first-party override of `M` on its inheritance line — it INHERITS the
// member from the (often external) declaring base. Receiver-narrowing used to fall back to full CHA in this
// case (NarrowByReceiver: empty subtree + empty ancestors -> return all candidates), fanning the call to
// every sibling override (e.g. `login.Delete()` -> all ~49 entity Delete overrides incl. ContactEntity).
// The fix returns EMPTY there: no first-party override on the line means the inherited impl runs, and that is
// reached via the DIRECT call edge — so the sibling-override fan is spurious and dropped.
public sealed class InheritedReceiverNoFanTests
{
    // Base.M (virtual) overridden by Sibling1.M and Sibling2.M (mined override facts make Base.M CHA-fan to
    // both). Inheritor : Base does NOT override M (it inherits Base.M). Three call sites to Base.M with
    // different receivers: Inheritor (no override on its line), none (CHA), Sibling1 (has its own override).
    private static FactGraphData Graph()
    {
        var edges = new[]
        {
            new CallEdge("M:N.InhCaller.Go", "M:N.Base.M", "invocation", "f.cs", 1, ReceiverType: "N.Inheritor"),
            new CallEdge("M:N.ChaCaller.Go", "M:N.Base.M", "invocation", "f.cs", 2), // no receiver => CHA
            new CallEdge("M:N.SibCaller.Go", "M:N.Base.M", "invocation", "f.cs", 3, ReceiverType: "N.Sibling1"),
        };
        var bases = new[]
        {
            new BaseEdge("T:N.Sibling1", "T:N.Base"),
            new BaseEdge("T:N.Sibling2", "T:N.Base"),
            new BaseEdge("T:N.Inheritor", "T:N.Base"), // Inheritor derives from Base but adds no M override
        };
        var methods = new[]
        {
            new MethodRef("M:N.InhCaller.Go", "Go", "T:N.InhCaller"),
            new MethodRef("M:N.ChaCaller.Go", "Go", "T:N.ChaCaller"),
            new MethodRef("M:N.SibCaller.Go", "Go", "T:N.SibCaller"),
            new MethodRef("M:N.Base.M", "M", "T:N.Base"),
            new MethodRef("M:N.Sibling1.M", "M", "T:N.Sibling1", IsOverride: true),
            new MethodRef("M:N.Sibling2.M", "M", "T:N.Sibling2", IsOverride: true),
            // NOTE: no M:N.Inheritor.M — Inheritor inherits Base.M.
        };
        var mined = new[]
        {
            new DispatchFact("M:N.Base.M", "M:N.Sibling1.M", "override"),
            new DispatchFact("M:N.Base.M", "M:N.Sibling2.M", "override"),
        };
        return new FactGraphData(edges, Array.Empty<ImplementsEdge>(), methods, bases, mined);
    }

    [Test]
    public void Inherited_receiver_does_not_fan_to_sibling_overrides()
    {
        // Inheritor has no override of M -> the call inherits Base.M; the spurious sibling fan is suppressed.
        var reach = FactPathFinder.Reaches(Graph(), "M:N.InhCaller.Go");

        reach.Keys.ShouldContain("M:N.Base.M"); // inherited impl reached via the DIRECT edge
        reach.Keys.ShouldNotContain("M:N.Sibling1.M");
        reach.Keys.ShouldNotContain("M:N.Sibling2.M");
    }

    [Test]
    public void Unreliable_cha_receiver_still_fans_to_all_overrides()
    {
        // Precondition + recall: a receiver-less (CHA) call still fans to every override — the fix only
        // touches the RELIABLE-receiver-with-no-override case, not unreliable/base/interface receivers.
        var reach = FactPathFinder.Reaches(Graph(), "M:N.ChaCaller.Go");

        reach.Keys.ShouldContain("M:N.Sibling1.M");
        reach.Keys.ShouldContain("M:N.Sibling2.M");
    }

    [Test]
    public void Receiver_with_its_own_override_narrows_to_it()
    {
        // Recall: a receiver that DOES override M narrows to its own override, not the sibling — unchanged.
        var reach = FactPathFinder.Reaches(Graph(), "M:N.SibCaller.Go");

        reach.Keys.ShouldContain("M:N.Sibling1.M");
        reach.Keys.ShouldNotContain("M:N.Sibling2.M");
    }
}
