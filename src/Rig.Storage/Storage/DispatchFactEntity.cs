namespace Rig.Storage.Storage;

// An exact Roslyn-mined member-level dispatch edge (dispatch_facts): SourceMember (base virtual /
// interface method DocID) dispatches to TargetMember (override / implementing method DocID).
// Kind = "override" | "impl". See Rig.Domain.Data.DispatchFact.
public sealed class DispatchFactEntity
{
    public string RunId { get; set; } = "";
    public int DispatchFactIndex { get; set; }
    public string SourceMember { get; set; } = "";
    public string TargetMember { get; set; } = "";
    public string Kind { get; set; } = "";
}
