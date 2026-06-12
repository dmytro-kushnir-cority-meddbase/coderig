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
    public string? FirstArgumentTemplate { get; set; }
    public string? FirstArgumentType { get; set; }
    public string? EnclosingLoopKind { get; set; }
    public string? EnclosingLoopDetail { get; set; }
    public string? EnclosingInvocations { get; set; }
    public string? EnclosingCatchTypes { get; set; }
    public string? TypeArguments { get; set; }
    public string? FirstArgumentName { get; set; }
    public string? DelegateConsumer { get; set; }
}
