namespace Rig.Storage.Storage;

public sealed class SymbolFactEntity
{
    public string RunId { get; set; } = "";
    public int SymbolFactIndex { get; set; }
    public string SymbolId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string? ContainingSymbolId { get; set; }
    public string Modifiers { get; set; } = "";
    public string TypeKind { get; set; } = "";
    public string Signature { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public int EndLine { get; set; }
    public string DefiningAssembly { get; set; } = "";
    public bool IsOverride { get; set; }

    // Deterministic hash of the symbol's declaration text — see SymbolFact.BodyHash. "" when no body / on a
    // pre-fact store. Lets `rig impact` detect an in-place body edit that the reachable-set diff misses.
    public string BodyHash { get; set; } = "";
}
