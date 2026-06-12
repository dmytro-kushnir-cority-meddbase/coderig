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
    public string DefiningAssembly { get; set; } = "";
    public bool IsOverride { get; set; }
}
