using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Domain.Data;

namespace Rig.Analysis.Extraction;

// Stage-1 fact extraction (see docs/fact-layer-refactor.md). Rule-agnostic, resolved
// structural facts: declared symbols, references (find-all-references), and type-relation
// edges. Global identity is the DocumentationCommentId (DocID). Lambdas/locals get no
// global id (host-context only) — they are simply not emitted as symbols here.
internal static class FactExtractor
{
    public static FactExtractionResult Extract(SourceModel source)
    {
        var model = source.SemanticModel;
        var root = source.Root;
        var tree = source.Tree;

        var symbols = new List<SymbolFact>();
        var references = new List<ReferenceFact>();
        var relations = new List<TypeRelationFact>();

        // --- Declarations -> SymbolFact (+ TypeRelation for type base/interface edges) ---
        foreach (var decl in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var symbol = model.GetDeclaredSymbol(decl);
            if (symbol is null)
                continue;

            // Field/event declarations declare one symbol per variable; handle below.
            if (decl is BaseFieldDeclarationSyntax fieldDecl)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable) is { } fieldSymbol)
                        AddSymbol(symbols, fieldSymbol, tree, variable);
                }
                continue;
            }

            var docId = symbol.GetDocumentationCommentId();
            if (docId is null)
                continue;

            AddSymbol(symbols, symbol, tree, decl);

            if (symbol is INamedTypeSymbol typeSymbol)
                AddTypeRelations(relations, typeSymbol, docId);
        }

        // --- References -> ReferenceFact (one pass over every simple name) ---
        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var target = model.GetSymbolInfo(name).Symbol;
            if (target is null || target is INamespaceSymbol)
                continue;

            var refKind = ClassifyReference(name, target);
            if (refKind is null)
                continue;

            var receiverType = refKind == "invocation" ? ReceiverTypeOf(name, model) : null;
            AddReference(references, target, refKind, EnclosingSymbolId(name, model), tree, name, receiverType);
        }

        // --- Object creations -> ctor refs ---
        // GetSymbolInfo on a type *name* resolves to the type (recorded as typeUse above), never the
        // constructor — so `new XxxEntity(pk)` would otherwise carry no constructor/argument fact.
        // Resolve the invoked constructor here so ctor-matched effect rules (the llblgen entity-ctor
        // fetch, gap G5) can see the constructed type and its argument count from the ctor DocID.
        foreach (var creation in root.DescendantNodes().OfType<BaseObjectCreationExpressionSyntax>())
        {
            if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
                AddReference(references, ctor, "ctor", EnclosingSymbolId(creation, model), tree, creation);
        }

        return new FactExtractionResult(symbols, references, relations);
    }

    private static void AddSymbol(List<SymbolFact> symbols, ISymbol symbol, SyntaxTree tree, SyntaxNode node)
    {
        var docId = symbol.GetDocumentationCommentId();
        if (docId is null)
            return;

        symbols.Add(new SymbolFact(
            SymbolId: docId,
            Kind: KindOf(symbol),
            Name: symbol.Name,
            Namespace: symbol.ContainingNamespace?.ToDisplayString() ?? "",
            ContainingSymbolId: symbol.ContainingSymbol?.GetDocumentationCommentId(),
            Modifiers: ModifiersOf(symbol),
            TypeKind: symbol is INamedTypeSymbol t ? t.TypeKind.ToString().ToLowerInvariant() : "",
            Signature: symbol.ToDisplayString(),
            FilePath: tree.FilePath,
            Line: tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
            DefiningAssembly: symbol.ContainingAssembly?.Name ?? "",
            IsOverride: symbol.IsOverride
        ));
    }

    private static void AddTypeRelations(List<TypeRelationFact> relations, INamedTypeSymbol type, string typeDocId)
    {
        if (type.BaseType is { SpecialType: SpecialType.None } baseType
            && baseType.GetDocumentationCommentId() is { } baseDocId)
        {
            relations.Add(new TypeRelationFact(typeDocId, baseDocId, "base"));
        }

        foreach (var iface in type.Interfaces)
        {
            if (iface.GetDocumentationCommentId() is { } ifaceDocId)
                relations.Add(new TypeRelationFact(typeDocId, ifaceDocId, "interface"));
        }
    }

    private static void AddReference(
        List<ReferenceFact> references,
        ISymbol target,
        string refKind,
        string? enclosingId,
        SyntaxTree tree,
        SyntaxNode node,
        string? receiverType = null)
    {
        // For constructors, point the reference at the constructor's containing type's ctor DocID;
        // for everything else use the symbol's own DocID. Reduced extension methods resolve to the
        // original definition so the DocID matches the declaration.
        var resolved = target is IMethodSymbol method ? (method.ReducedFrom ?? method).OriginalDefinition : target.OriginalDefinition;
        var docId = resolved.GetDocumentationCommentId();
        if (docId is null)
            return;

        var inSource = resolved.Locations.Any(loc => loc.IsInSource);
        var assembly = resolved.ContainingAssembly?.Name ?? "";

        // First-party always indexed; drop the explosive BCL/runtime noise when not first-party.
        if (!inSource && IsRuntimeAssembly(assembly))
            return;

        references.Add(new ReferenceFact(
            TargetSymbolId: docId,
            RefKind: refKind,
            EnclosingSymbolId: enclosingId,
            TargetAssembly: assembly,
            TargetInSource: inSource,
            FilePath: tree.FilePath,
            Line: tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
            ReceiverType: receiverType
        ));
    }

    // Static type of an invocation's receiver: `a.Foo()` -> type of `a` (open-generic FQN).
    // Bare `Foo()` (implicit this) and other shapes return null — only explicit member-access
    // receivers carry a receiver-type fact.
    private static string? ReceiverTypeOf(SimpleNameSyntax name, SemanticModel model)
    {
        if (name.Parent is MemberAccessExpressionSyntax member && member.Name == name)
            return model.GetTypeInfo(member.Expression).Type?.OriginalDefinition.ToDisplayString();

        if (name.Parent is MemberBindingExpressionSyntax binding
            && binding.Parent is ConditionalAccessExpressionSyntax conditional)
            return model.GetTypeInfo(conditional.Expression).Type?.OriginalDefinition.ToDisplayString();

        return null;
    }

    private static string? ClassifyReference(SimpleNameSyntax name, ISymbol target) =>
        target switch
        {
            IMethodSymbol { MethodKind: MethodKind.Constructor } => "ctor",
            IMethodSymbol => IsInvoked(name) ? "invocation" : "methodGroup",
            INamedTypeSymbol or ITypeParameterSymbol => IsAttributeName(name) ? "attributeUse" : "typeUse",
            IPropertySymbol or IFieldSymbol => IsWriteTarget(name) ? "write" : "read",
            IEventSymbol => "read",
            _ => null,
        };

    // True when this name is the method being invoked (a.Foo() or Foo()), as opposed to a
    // method group passed as a delegate (the background-worker handoff case).
    private static bool IsInvoked(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax direct && direct.Expression == name)
            return true;
        return name.Parent is MemberAccessExpressionSyntax member
            && member.Name == name
            && member.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == member;
    }

    private static bool IsAttributeName(SimpleNameSyntax name) =>
        name.FirstAncestorOrSelf<AttributeSyntax>() is { } attr
        && (attr.Name == name || (attr.Name is QualifiedNameSyntax q && q.Right == name));

    private static bool IsWriteTarget(SimpleNameSyntax name)
    {
        var expr = name.Parent is MemberAccessExpressionSyntax m && m.Name == name ? (ExpressionSyntax)m : name;
        return expr.Parent is AssignmentExpressionSyntax assignment && assignment.Left == expr;
    }

    private static string? EnclosingSymbolId(SyntaxNode node, SemanticModel model)
    {
        var member = node.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        if (member is null)
            return null;
        if (member is BaseFieldDeclarationSyntax field)
        {
            var first = field.Declaration.Variables.FirstOrDefault();
            return first is null ? null : model.GetDeclaredSymbol(first)?.GetDocumentationCommentId();
        }
        return model.GetDeclaredSymbol(member)?.GetDocumentationCommentId();
    }

    private static string KindOf(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol => "type",
            IMethodSymbol => "method",
            IPropertySymbol => "property",
            IFieldSymbol => "field",
            IEventSymbol => "event",
            _ => symbol.Kind.ToString().ToLowerInvariant(),
        };

    private static string ModifiersOf(ISymbol symbol)
    {
        var parts = new List<string>();
        if (symbol.IsStatic) parts.Add("static");
        if (symbol.IsAbstract) parts.Add("abstract");
        if (symbol.IsSealed) parts.Add("sealed");
        if (symbol.IsVirtual) parts.Add("virtual");
        if (symbol.IsOverride) parts.Add("override");
        if (symbol is IMethodSymbol { IsAsync: true }) parts.Add("async");
        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true }) parts.Add("readonly");
        return string.Join(' ', parts);
    }

    private static bool IsRuntimeAssembly(string assembly) =>
        assembly.StartsWith("System", StringComparison.Ordinal)
        || assembly is "mscorlib" or "netstandard" or "WindowsBase"
        || assembly.StartsWith("PresentationCore", StringComparison.Ordinal)
        || assembly.StartsWith("PresentationFramework", StringComparison.Ordinal);
}

internal sealed record FactExtractionResult(
    IReadOnlyList<SymbolFact> Symbols,
    IReadOnlyList<ReferenceFact> References,
    IReadOnlyList<TypeRelationFact> TypeRelations
);
