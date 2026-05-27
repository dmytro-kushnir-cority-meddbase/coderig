using Rig.Domain.Functions;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("Generated/GeneratedEndpoint.g.cs", "**/Generated/*.g.cs")]
    [InlineData("EntryPointEffects.Api/Generated/GeneratedEndpoint.g.cs", "**/Generated/*.g.cs")]
    [InlineData("EntryPointEffects.Api\\Generated\\GeneratedEndpoint.g.cs", "**/Generated/*.g.cs")]
    public void IsMatch_matches_generated_file_globs_across_path_shapes(string path, string glob)
    {
        GlobMatcher.IsMatch(path, glob).ShouldBeTrue();
    }

    [Fact]
    public void IsMatch_does_not_let_single_star_cross_directories()
    {
        GlobMatcher.IsMatch("Generated/Nested/Endpoint.g.cs", "Generated/*.g.cs").ShouldBeFalse();
    }

    [Fact]
    public void IsMatch_is_case_insensitive_for_windows_friendly_profiles()
    {
        GlobMatcher.IsMatch("generated/endpoint.G.CS", "Generated/*.g.cs").ShouldBeTrue();
    }
}
