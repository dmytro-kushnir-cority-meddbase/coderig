namespace Rig.Storage.Storage;

public sealed class TypeRelationFactEntity
{
    public string RunId { get; set; } = "";
    public int TypeRelationFactIndex { get; set; }
    public string TypeSymbolId { get; set; } = "";
    public string RelatedSymbolId { get; set; } = "";
    public string RelationKind { get; set; } = "";
}
