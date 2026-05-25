using Rig.Analysis;
using Shouldly;

namespace Rig.Tests.Analysis;

public sealed class RuleTypeMatcherTests
{
    [Theory]
    [InlineData("System.Net.Http.HttpClient", "System.Net.Http.HttpClient")]
    [InlineData("Microsoft.EntityFrameworkCore.DbSet<EntryPointEffects.Api.Data.Team>", "Microsoft.EntityFrameworkCore.DbSet")]
    [InlineData("EntryPointEffects.Api.Services.TeamRepository", "TeamRepository")]
    [InlineData("Ardalis.SharedKernel.IRepository<EntryPointEffects.Api.Data.Team>", "Ardalis.SharedKernel.IRepository")]
    public void MatchesDisplayName_matches_exact_generic_and_simple_type_names(string actualType, string ruleType)
    {
        RuleTypeMatcher.MatchesDisplayName(actualType, ruleType).ShouldBeTrue();
    }

    [Fact]
    public void MatchesDisplayName_requires_explicit_substring_matching_for_loose_dispatch_rules()
    {
        RuleTypeMatcher.MatchesDisplayName("MediatR.ISender", "Sender").ShouldBeFalse();
        RuleTypeMatcher.MatchesDisplayName("MediatR.ISender", "Sender", allowSubstring: true).ShouldBeTrue();
    }

    [Fact]
    public void MatchesDisplayName_is_case_sensitive()
    {
        RuleTypeMatcher.MatchesDisplayName("System.Net.Http.HttpClient", "httpclient").ShouldBeFalse();
    }
}
