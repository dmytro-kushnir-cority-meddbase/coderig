namespace Rig.Storage;

public sealed class InvocationObservationEntity
{
    public string RunId { get; set; } = "";

    public int InvocationIndex { get; set; }

    public string ContainingMethodSymbol { get; set; } = "";

    public string TargetSymbol { get; set; } = "";

    public string TargetDisplayName { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";
}
