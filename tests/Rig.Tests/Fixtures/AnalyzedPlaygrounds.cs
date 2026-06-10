using Rig.Analysis;
using Rig.Domain.Data;

namespace Rig.Tests.Fixtures;

// Collection-scoped cache of analyzed playgrounds. SolutionAnalyzer.AnalyzeAsync spins up a full
// MSBuild/Roslyn workspace and compiles the solution (seconds each); the read-only AnalysisResult it
// returns is identical for every test that uses the same playground. Without this, the suite paid
// that cost ~18 times for just two playgrounds (LegacyNet48Web analyzed 12×, EntryPointEffects 5×).
//
// Each playground is copied, restored, and analyzed at most ONCE for the whole Roslyn integration
// collection, lazily (only playgrounds a run actually touches), and the result is shared. Tests MUST
// treat the AnalysisResult and the on-disk copy as read-only — anything that writes (e.g. `rig index`
// into .rig) must keep creating its own TempPlayground instead of using this fixture.
public sealed class AnalyzedPlaygrounds : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Lazy<Task<AnalyzedPlayground>>> _cache = new(StringComparer.Ordinal);
    private readonly List<TempPlayground> _owned = new();

    public Task<AnalyzedPlayground> LegacyNet48Async() => GetAsync(nameof(LegacyNet48Async), TempPlayground.CreateLegacyNet48Async);

    public Task<AnalyzedPlayground> EntryPointEffectsAsync() =>
        GetAsync(nameof(EntryPointEffectsAsync), TempPlayground.CreateEntryPointEffectsAsync);

    private Task<AnalyzedPlayground> GetAsync(string key, Func<Task<TempPlayground>> create)
    {
        Lazy<Task<AnalyzedPlayground>> lazy;
        lock (_gate)
        {
            if (!_cache.TryGetValue(key, out lazy!))
            {
                lazy = new Lazy<Task<AnalyzedPlayground>>(async () =>
                {
                    var playground = await create();
                    lock (_gate)
                    {
                        _owned.Add(playground);
                    }
                    var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath);
                    return new AnalyzedPlayground(result, playground.WorkingDirectory, playground.SolutionPath);
                });
                _cache[key] = lazy;
            }
        }

        return lazy.Value;
    }

    public void Dispose()
    {
        foreach (var playground in _owned)
        {
            playground.Dispose();
        }
    }
}

// A playground analyzed once and shared: its read-only AnalysisResult plus the directory holding its
// copied source + rig.rules.json (for the fact rule providers) and the solution path.
public sealed record AnalyzedPlayground(AnalysisResult Result, string WorkingDirectory, string SolutionPath);
