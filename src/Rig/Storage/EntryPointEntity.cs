namespace Rig.Storage;

internal sealed class EntryPointEntity
{
    public string RunId { get; set; } = "";

    public int EntryPointIndex { get; set; }

    public string Kind { get; set; } = "";

    public string Method { get; set; } = "";

    public string Route { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }
}