namespace Rig.Storage.Storage;

public sealed class CallGraphNodeEffectEntity
{
    public string RunId { get; set; } = "";

    public int GraphIndex { get; set; }

    public int NodeIndex { get; set; }

    public int LinkIndex { get; set; }

    public int EffectIndex { get; set; }
}
