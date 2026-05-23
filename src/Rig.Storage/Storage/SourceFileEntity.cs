namespace Rig.Storage;

public sealed class SourceFileEntity
{
    public string RunId { get; set; } = "";

    public int FileIndex { get; set; }

    public string ProjectName { get; set; } = "";

    public string FilePath { get; set; } = "";

    public string Status { get; set; } = "";

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";

    public string Evidence { get; set; } = "";
}
