namespace Rig.Storage.Storage;

public sealed class AllocationFactEntity
{
    public string RunId { get; set; } = "";
    public int AllocationFactIndex { get; set; }
    public string Operation { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string EnclosingSymbolId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string? EnclosingLoopKind { get; set; }
    public string? EnclosingLoopDetail { get; set; }
    public string? EnclosingGuards { get; set; }
}
