using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// `rig index --from <csproj>` whose entry-closure resolves to 0 buildable C# projects must fail cleanly,
// not crash at `csharpProjects[0]` with an unhandled IndexOutOfRangeException (regression: surfaced on
// `--from ContractManagement.Messages.csproj`). The guard throws InvalidOperationException, which
// IndexCommands catches → "Failed to load" diagnostic + non-zero exit.
public sealed class IndexableClosureGuardTests
{
    [Test]
    public void Zero_projects_throws_clean_InvalidOperationException_not_index_out_of_range()
    {
        var ex = Should.Throw<InvalidOperationException>(() => SolutionSourceLoader.EnsureIndexableProjects(0));
        ex.Message.ShouldContain("Nothing to index");
    }

    [Test]
    [Arguments(1)]
    [Arguments(7)]
    [Arguments(123)]
    public void Non_empty_closure_does_not_throw(int csharpProjectCount)
    {
        Should.NotThrow(() => SolutionSourceLoader.EnsureIndexableProjects(csharpProjectCount));
    }
}
