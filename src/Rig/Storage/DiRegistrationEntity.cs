namespace Rig.Storage;

public sealed class DiRegistrationEntity
{
    public string RunId { get; set; } = "";

    public int RegistrationIndex { get; set; }

    public string ServiceType { get; set; } = "";

    public string? ImplementationType { get; set; }

    public string Lifetime { get; set; } = "";

    public string RegistrationKind { get; set; } = "";

    public string FilePath { get; set; } = "";

    public int Line { get; set; }

    public string Confidence { get; set; } = "";

    public string Basis { get; set; } = "";

    public string Reason { get; set; } = "";

    public string Evidence { get; set; } = "";
}
