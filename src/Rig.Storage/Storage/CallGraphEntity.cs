namespace Rig.Storage.Storage;

public sealed class CallGraphEntity
{
    public string RunId { get; set; } = "";

    public int GraphIndex { get; set; }

    public string EntryPoint { get; set; } = "";
}
