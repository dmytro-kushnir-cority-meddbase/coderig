namespace Rig.Storage;

public sealed class MethodObservationEntity
{
    public string RunId { get; set; } = "";

    public int MethodIndex { get; set; }

    public string Symbol { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }

    public string ProjectName { get; set; } = "";
}
