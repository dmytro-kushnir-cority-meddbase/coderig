using Xunit;

namespace Rig.Tests.Fixtures;

// The collection is serialized (DisableParallelization) because each test drives a heavy
// MSBuild/Roslyn workspace; the shared AnalyzedPlaygrounds fixture analyzes each playground once
// for the whole collection so tests don't each re-pay that cost.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RoslynIntegrationCollection : ICollectionFixture<AnalyzedPlaygrounds>
{
    public const string Name = "Roslyn integration";
}
