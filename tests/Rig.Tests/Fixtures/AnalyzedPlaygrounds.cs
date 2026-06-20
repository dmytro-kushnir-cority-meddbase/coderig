using Rig.Analysis;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Tests.Fixtures;

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
                    var rules = RuleSetLoader.Load(playground.WorkingDirectory);
                    var result = await SolutionAnalyzer.AnalyzeAsync(playground.SolutionPath, rules);
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

public sealed record AnalyzedPlayground(AnalysisResult Result, string WorkingDirectory, string SolutionPath);
