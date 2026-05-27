namespace Rig.Storage.Storage;

public sealed class EffectObservationEntity
{
    public string RunId { get; set; } = "";

    public int EffectIndex { get; set; }

    public int ObservationIndex { get; set; }

    public string Type { get; set; } = "";

    public string Context { get; set; } = "";

    public string Detail { get; set; } = "";

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";
}
