namespace Rig.Storage.Storage;

public sealed class ReferenceFactEntity
{
    public string RunId { get; set; } = "";
    public int ReferenceFactIndex { get; set; }
    public string TargetSymbolId { get; set; } = "";
    public string RefKind { get; set; } = "";
    public string? EnclosingSymbolId { get; set; }
    public string TargetAssembly { get; set; } = "";
    public bool TargetInSource { get; set; }
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string? ReceiverType { get; set; }
    public string? FirstArgumentTemplate { get; set; }
    public string? FirstArgumentType { get; set; }
    public string? EnclosingLoopKind { get; set; }
    public string? EnclosingLoopDetail { get; set; }
    public string? EnclosingInvocations { get; set; }
    public string? EnclosingCatchTypes { get; set; }
    public string? TypeArguments { get; set; }
    public string? FirstArgumentName { get; set; }
    public string? DelegateConsumer { get; set; }
    public string? EnclosingScopes { get; set; }
    public string? ArgumentTemplates { get; set; }
    public string? ArgumentNames { get; set; }
    public string? DeclaringTypeArgBinding { get; set; }
    public string? MethodTypeArgBinding { get; set; }

    // True when this reference is a non-virtual `base.M(...)` call (ReferenceFact.NonVirtual). Default
    // false; old stores read it as false so behavior is unchanged until re-indexed.
    public bool NonVirtual { get; set; }

    // CFG-derived control-dependence guard set of this call-site within its method (branch-aware-effects):
    // encoded predicate-text/polarity (FactStructuralContext.DecodeGuards), null == must-run. EnsureCreated
    // adds this column on a fresh index; old stores lack it and read as null (no guards).
    public string? EnclosingGuards { get; set; }
}
