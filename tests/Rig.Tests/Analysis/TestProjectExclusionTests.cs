using Rig.Analysis.Inventory;
using Shouldly;

namespace Rig.Tests.Analysis;

// --no-tests drops projects matched by this name convention before their design-time build, so test
// methods don't surface as entry points and test-only references don't inflate the graph.
public sealed class TestProjectExclusionTests
{
    [Test]
    [Arguments("src/a/Foo.Tests/Foo.Tests.csproj", true)]
    [Arguments("src/a/Foo.UnitTests/Foo.UnitTests.csproj", true)]
    [Arguments("src/a/Foo.IntegrationTests/Foo.IntegrationTests.csproj", true)]
    [Arguments("src/a/Foo.Tests.Common/Foo.Tests.Common.csproj", true)]
    [Arguments("src/a/MedDBase.ServiceTier/MedDBase.ServiceLayer.csproj", false)]
    [Arguments("src/a/MMS.Data.Standard/MMS.Data.Standard.csproj", false)]
    [Arguments("src/a/Contestant/Contestant.csproj", false)]
    public void Classifies_test_projects_by_name_convention(string path, bool isTest)
    {
        SolutionSourceLoader.IsTestProjectPath(path).ShouldBe(isTest);
    }
}
