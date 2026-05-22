namespace Rig.Storage;

internal sealed class CallGraphNodeCallEntity
{
    public string RunId { get; set; } = "";

    public int GraphIndex { get; set; }

    public int NodeIndex { get; set; }

    public int CallIndex { get; set; }

    public string TargetSymbol { get; set; } = "";
}