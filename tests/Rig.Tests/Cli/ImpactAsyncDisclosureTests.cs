using Rig.Cli.Commands;
using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Cli;

// Bug A (impact-silent-async-handoff-underreport): the DEFAULT sync-mode impact cuts async/scheduled handoff
// edges, so an effect reachable from an EP only across a handoff is silently excluded — the diff must SAY so
// (as `callers` does) instead of presenting the sync count as the whole picture. The disclosure is SYNC-only:
// the async modes already state their scope in the header's asyncNote, and they exclude nothing to disclose.
// Pure unit over the per-mode text; the header wiring that emits it is exercised by the impact render path.
public sealed class ImpactAsyncDisclosureTests
{
    [Test]
    public void Sync_mode_discloses_the_handoff_exclusion_and_points_to_async()
    {
        var note = ImpactCommand.SyncModeDisclosure(FactPathFinder.TraversalMode.SyncCut);
        note.ShouldNotBeNull();
        note.ShouldContain("SYNC mode");
        note.ShouldContain("handoff");
        note.ShouldContain("--async"); // the actionable remedy
    }

    [Test]
    public void Async_modes_disclose_nothing_extra_nothing_is_excluded()
    {
        // Async walks the handoffs, so there is no hidden exclusion to warn about — the header's own asyncNote
        // states the scope. Returning null keeps the async header free of a spurious "excluded" note.
        ImpactCommand.SyncModeDisclosure(FactPathFinder.TraversalMode.AsyncExact).ShouldBeNull();
        ImpactCommand.SyncModeDisclosure(FactPathFinder.TraversalMode.AsyncInclude).ShouldBeNull();
    }
}
