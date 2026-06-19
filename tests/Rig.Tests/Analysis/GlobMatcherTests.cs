using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class GlobMatcherTests
{
    [Test]
    [Arguments("Generated/GeneratedEndpoint.g.cs", "**/Generated/*.g.cs")]
    [Arguments("EntryPointEffects.Api/Generated/GeneratedEndpoint.g.cs", "**/Generated/*.g.cs")]
    [Arguments("EntryPointEffects.Api\\Generated\\GeneratedEndpoint.g.cs", "**/Generated/*.g.cs")]
    public void IsMatch_matches_generated_file_globs_across_path_shapes(string path, string glob)
    {
        GlobMatcher.IsMatch(path, glob).ShouldBeTrue();
    }

    [Test]
    public void IsMatch_does_not_let_single_star_cross_directories()
    {
        GlobMatcher.IsMatch("Generated/Nested/Endpoint.g.cs", "Generated/*.g.cs").ShouldBeFalse();
    }

    [Test]
    public void IsMatch_is_case_insensitive_for_windows_friendly_profiles()
    {
        GlobMatcher.IsMatch("generated/endpoint.G.CS", "Generated/*.g.cs").ShouldBeTrue();
    }
}
