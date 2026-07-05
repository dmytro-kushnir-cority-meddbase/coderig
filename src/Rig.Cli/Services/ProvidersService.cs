using Rig.Analysis.Rules;
using static Rig.Cli.Effects.EffectDerivation;

namespace Rig.Cli.Services;

// The valid effect-filter tokens for the web explorer's only/exclude control — the SAME set
// `rig derive --list-providers` prints: the bare provider names and the provider:operation pairs known to
// the effective rule set. Rule-only (no store access), so it's cheap and works even before any tree query.
public static class ProvidersService
{
    public sealed record ProviderTokens(IReadOnlyList<string> Providers, IReadOnlyList<string> ProviderOps);

    public static ProviderTokens List(string workingDirectory, IReadOnlyList<string>? extraRules = null)
    {
        var rules = RuleSetLoader.Load(workingDirectory, extraRules ?? []);
        return new ProviderTokens(
            Providers: KnownProviders(rules).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(),
            ProviderOps: KnownProviderOps(rules).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
        );
    }
}
