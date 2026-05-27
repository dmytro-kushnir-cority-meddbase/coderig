namespace Rig.Storage.Storage;

public sealed class EffectEntity
{
    public string RunId { get; set; } = "";

    public int EffectIndex { get; set; }

    public string Provider { get; set; } = "";

    public string Operation { get; set; } = "";

    public string Resource { get; set; } = "";

    public string Method { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";
}
