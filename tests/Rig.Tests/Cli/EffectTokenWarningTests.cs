using Rig.Cli;
using Rig.Cli.Effects;
using Rig.Domain.Data;
using Shouldly;

namespace Rig.Tests.Cli;

// UX research panel items #1 and #16: silent-failure trust bug — --only/--exclude tokens that match
// no known effect are accepted silently, causing the user to read an empty result as "no such effects"
// when the token was wrong. Pins:
//   (1) KnownProviders / KnownProviderOps derive the valid set from the effective rule set.
//   (2) WarnUnknownFilterTokens emits a non-fatal warning to STDERR for each unrecognised token.
//   (3) A fully-valid filter emits NO warning.
//   (4) `rig derive --list-providers` prints the valid set and exits 0 without opening a store.
public sealed class EffectTokenWarningTests
{
    // --- helpers ---

    // A minimal RuleSet with two effect rules: "http:GET" and "db_command:execute".
    private static RuleSet TwoRuleSet() =>
        new()
        {
            Effects =
            [
                new FactEffectRule(Provider: "http", Operation: "GET", Methods: [], DeclaringTypes: [], ReceiverTypes: []),
                new FactEffectRule(Provider: "db_command", Operation: "execute", Methods: [], DeclaringTypes: [], ReceiverTypes: []),
            ],
        };

    // --- KnownProviders / KnownProviderOps ---

    [Test]
    public void KnownProviders_returns_distinct_providers_from_rule_set()
    {
        var rules = TwoRuleSet();
        var providers = EffectDerivation.KnownProviders(rules);

        providers.ShouldContain("http");
        providers.ShouldContain("db_command");
        providers.Count.ShouldBe(2);
    }

    [Test]
    public void KnownProviders_is_case_insensitive()
    {
        var rules = TwoRuleSet();
        var providers = EffectDerivation.KnownProviders(rules);

        // Lookup with different casing must hit.
        providers.Contains("HTTP").ShouldBeTrue();
        providers.Contains("DB_COMMAND").ShouldBeTrue();
    }

    [Test]
    public void KnownProviderOps_returns_provider_colon_operation_pairs()
    {
        var rules = TwoRuleSet();
        var ops = EffectDerivation.KnownProviderOps(rules);

        ops.ShouldContain("http:GET");
        ops.ShouldContain("db_command:execute");
        ops.Count.ShouldBe(2);
    }

    [Test]
    public void KnownProviderOps_is_case_insensitive()
    {
        var rules = TwoRuleSet();
        var ops = EffectDerivation.KnownProviderOps(rules);

        ops.Contains("HTTP:get").ShouldBeTrue();
        ops.Contains("DB_COMMAND:EXECUTE").ShouldBeTrue();
    }

    [Test]
    public void KnownProviders_returns_empty_set_when_no_rules()
    {
        var rules = new RuleSet { Effects = [] };
        EffectDerivation.KnownProviders(rules).ShouldBeEmpty();
    }

    [Test]
    public void KnownProviders_deduplicates_when_multiple_rules_share_a_provider()
    {
        // Two rules with same provider, different operations.
        var rules = new RuleSet
        {
            Effects =
            [
                new FactEffectRule(Provider: "http", Operation: "GET", Methods: [], DeclaringTypes: [], ReceiverTypes: []),
                new FactEffectRule(Provider: "http", Operation: "POST", Methods: [], DeclaringTypes: [], ReceiverTypes: []),
            ],
        };

        EffectDerivation.KnownProviders(rules).Count.ShouldBe(1);
        EffectDerivation.KnownProviderOps(rules).Count.ShouldBe(2);
    }

    // --- WarnUnknownFilterTokens ---

    [Test]
    public void Warn_emits_nothing_when_both_sets_are_empty()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );
        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void Warn_emits_nothing_for_a_valid_bare_provider_token()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["http"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );
        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void Warn_emits_nothing_for_a_valid_provider_colon_operation_token()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["http:GET"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );
        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void Warn_emits_nothing_for_valid_tokens_in_exclude()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(["db_command"], StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );
        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void Warn_emits_warning_for_an_unrecognised_bare_token()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["webhook"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );

        var msg = error.ToString();
        msg.ShouldContain("warning:");
        msg.ShouldContain("webhook");
        // Must list at least one known provider so the user knows what to use instead.
        msg.ShouldContain("http");
        // Must hint at --list-providers for the full set.
        msg.ShouldContain("--list-providers");
    }

    [Test]
    public void Warn_emits_warning_for_an_unrecognised_provider_colon_operation_token()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["http:PATCH"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );

        var msg = error.ToString();
        msg.ShouldContain("warning:");
        msg.ShouldContain("http:PATCH");
    }

    [Test]
    public void Warn_emits_warning_for_each_unrecognised_token_in_both_sets()
    {
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["llm"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(["io:write"], StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );

        var msg = error.ToString();
        msg.ShouldContain("llm");
        msg.ShouldContain("io:write");
        // Two separate warning lines.
        msg.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("warning:")).ShouldBe(2);
    }

    [Test]
    public void Warn_does_not_warn_for_a_valid_bare_provider_even_when_colon_op_is_different_case()
    {
        // Token "HTTP" should match bare provider "http" — case-insensitive.
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["HTTP"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );
        error.ToString().ShouldBeEmpty();
    }

    [Test]
    public void Warn_warns_for_colon_op_token_when_provider_exists_but_operation_does_not()
    {
        // "http" is a known provider, but "http:DELETE" is not a known op — must warn for the precise token.
        var error = new StringWriter();
        EffectDerivation.WarnUnknownFilterTokens(
            only: new HashSet<string>(["http:DELETE"], StringComparer.OrdinalIgnoreCase),
            exclude: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            rules: TwoRuleSet(),
            errorWriter: error
        );

        var msg = error.ToString();
        msg.ShouldContain("warning:");
        msg.ShouldContain("http:DELETE");
    }

    [Test]
    public void Warn_does_not_change_exit_code_or_suppress_filter()
    {
        // Smoke: WarnUnknownFilterTokens must not throw and must not touch the effects list.
        var error = new StringWriter();
        // Should run without exception even with an entirely bogus token set.
        Should.NotThrow(() =>
            EffectDerivation.WarnUnknownFilterTokens(
                only: new HashSet<string>(["bogus1", "bogus2:nope"], StringComparer.OrdinalIgnoreCase),
                exclude: new HashSet<string>(["webhook", "llm:turbo"], StringComparer.OrdinalIgnoreCase),
                rules: TwoRuleSet(),
                errorWriter: error
            )
        );
        // All four unknown tokens warned.
        error.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("warning:")).ShouldBe(4);
    }

    // --- --list-providers integration ---

    // `rig derive --list-providers` must print the known provider set and exit 0 without opening a
    // store — so it works in an empty directory (the builtin rules are embedded in the assembly).
    [Test]
    public async Task List_providers_prints_known_providers_and_exits_0_without_a_store()
    {
        var emptyDir = Path.Combine(Path.GetTempPath(), "rig-list-providers-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyDir);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = await CliApplication.RunAsync(["derive", "--list-providers"], output, error, emptyDir);

            exitCode.ShouldBe(0);
            var text = output.ToString();

            // Must show the section headers.
            text.ShouldContain("Known effect providers");
            text.ShouldContain("Known provider:operation pairs");

            // At least a few well-known built-in providers must appear.
            text.ShouldContain("http");
            text.ShouldContain("db_command");
            text.ShouldContain("io");

            // STDERR must be empty — --list-providers is not an error condition.
            error.ToString().ShouldBeEmpty();
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    // `--list-providers` must appear in `rig derive --help` output.
    [Test]
    public async Task Derive_help_mentions_list_providers()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        await CliApplication.RunAsync(["derive", "--help"], output, error);

        output.ToString().ShouldContain("--list-providers");
    }
}
