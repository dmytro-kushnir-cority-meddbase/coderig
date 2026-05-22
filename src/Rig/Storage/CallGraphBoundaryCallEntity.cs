namespace Rig.Storage;

internal sealed class CallGraphBoundaryCallEntity
{
    public string RunId { get; set; } = "";

    public int GraphIndex { get; set; }

    public int NodeIndex { get; set; }

    public int BoundaryCallIndex { get; set; }

    public string Kind { get; set; } = "";

    public string Target { get; set; } = "";

    public string Method { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";
}