namespace Contracts;

// Flows TRANSITIVELY up the chain (ApiGateway + Web mention it in signatures although neither
// references Contracts directly). The loader's transitive in-set closure must keep this one assembly
// identity, or the calls whose signatures mention it silently fail to bind and the edge is dropped.
public sealed class PatientDto
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}
