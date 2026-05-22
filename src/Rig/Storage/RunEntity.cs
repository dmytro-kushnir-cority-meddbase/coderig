namespace Rig.Storage;

public sealed class RunEntity
{
    public string Id { get; set; } = "";

    public string CreatedAtUtcText { get; set; } = "";

    public string SolutionPath { get; set; } = "";

    public int EntryPointCount { get; set; }

    public int EffectCount { get; set; }

    public int DiRegistrationCount { get; set; }

    public int MethodObservationCount { get; set; }

    public int InvocationObservationCount { get; set; }

    public string AnalysisResultJson { get; set; } = "";
}
