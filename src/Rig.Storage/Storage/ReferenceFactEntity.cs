namespace Rig.Storage.Storage;

public sealed class ReferenceFactEntity
{
    public string RunId { get; set; } = "";
    public int ReferenceFactIndex { get; set; }
    public string TargetSymbolId { get; set; } = "";
    public string RefKind { get; set; } = "";
    public string? EnclosingSymbolId { get; set; }
    public string TargetAssembly { get; set; } = "";
    public bool TargetInSource { get; set; }
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string? ReceiverType { get; set; }
}
