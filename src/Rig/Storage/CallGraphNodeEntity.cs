namespace Rig.Storage;

internal sealed class CallGraphNodeEntity
{
    public string RunId { get; set; } = "";

    public int GraphIndex { get; set; }

    public int NodeIndex { get; set; }

    public string Symbol { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";
}