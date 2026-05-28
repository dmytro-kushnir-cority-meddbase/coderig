namespace Rig.Storage.Storage;

/// <summary>
/// Cross-run symbol lookup: maps every method symbol in every indexed run to the run that
/// contains its source.  When a callgraph node call can't be resolved within the current
/// run (it appears as BOUNDARY external), this table is consulted to find a peer run within
/// the same ProjectIdentity that has the source — enabling callgraph stitching across
/// incremental per-project indexing runs.
/// </summary>
public sealed class SymbolIndexEntity
{
    public string ProjectIdentity { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string RunId { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
}
