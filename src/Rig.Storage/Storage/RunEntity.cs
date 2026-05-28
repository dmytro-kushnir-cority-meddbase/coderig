namespace Rig.Storage.Storage;

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

    // Groups incremental per-project runs of the same solution so cross-run callgraph
    // stitching knows which symbol index entries belong together.
    public string? ProjectIdentity { get; set; }

    // The specific .csproj path indexed in this run (null for solution-level runs).
    public string? SourceProjectPath { get; set; }
}
