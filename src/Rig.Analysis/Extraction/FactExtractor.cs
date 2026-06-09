using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Analysis;
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

            var invocation = refKind == "invocation" ? InvocationOf(name) : null;
            var receiverType = refKind == "invocation" ? ReceiverTypeOf(name, model) : null;
            var (firstArgTemplate, firstArgType) = FirstArgumentOf(FirstArgumentExpressionOf(name, refKind, invocation), model);
            var structural = StructuralContextOf(invocation, model);
            AddReference(
                references,
                target,
                refKind,
                EnclosingSymbolId(name, model),
                tree,
                name,
                receiverType,
                firstArgTemplate,
                firstArgType,
                structural);
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

        // --- Throw sites -> "throw" refs (the thrown exception TYPE) ---
        // A `throw` is first-party control flow, so — unlike calls INTO the BCL — we keep throws of
        // runtime exception types too (the throw SITE is ours); allowRuntime bypasses the runtime-
        // assembly filter. The target is the exception TYPE (not its ctor) so error/permission effect
        // rules can gate on the type name / base type. Bare `throw;` rethrows have no operand and are
        // skipped. Structural context (enclosing try/catch + loop) rides along like invocation refs.
        foreach (var thrown in ThrownExpressions(root))
        {
            var type = model.GetTypeInfo(thrown).Type;
            if (type is null or IErrorTypeSymbol)
                continue;
            AddReference(
                references, type, "throw", EnclosingSymbolId(thrown, model), tree, thrown,
                structural: StructuralContextOf(thrown, model), allowRuntime: true);
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
        string? receiverType = null,
        string? firstArgumentTemplate = null,
        string? firstArgumentType = null,
        StructuralContext structural = default,
        bool allowRuntime = false)
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
        // allowRuntime keeps runtime targets that ARE meaningful first-party control flow (throws of
        // System exceptions — the throw site is ours).
        if (!inSource && !allowRuntime && IsRuntimeAssembly(assembly))
            return;

        references.Add(new ReferenceFact(
            TargetSymbolId: docId,
            RefKind: refKind,
            EnclosingSymbolId: enclosingId,
            TargetAssembly: assembly,
            TargetInSource: inSource,
            FilePath: tree.FilePath,
            Line: tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
            ReceiverType: receiverType,
            FirstArgumentTemplate: firstArgumentTemplate,
            FirstArgumentType: firstArgumentType,
            EnclosingLoopKind: structural.LoopKind,
            EnclosingLoopDetail: structural.LoopDetail,
            EnclosingInvocations: structural.EnclosingInvocations,
            EnclosingCatchTypes: structural.CatchTypes
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

    // The first-argument expression whose literal/type becomes a fact: an invocation's first
    // argument (http_argument/string_argument/argument_type, P1b) or — for an attribute usage,
    // which resolves to the attribute constructor and is recorded as a "ctor" ref — the attribute's
    // first positional argument, exposing MVC route literals ([Route("..")], [HttpGet("..")]) to the
    // entry-point deriver (P1d). Null for any other ref shape.
    private static ExpressionSyntax? FirstArgumentExpressionOf(SimpleNameSyntax name, string refKind, InvocationExpressionSyntax? invocation)
    {
        if (refKind == "invocation")
            return invocation?.ArgumentList.Arguments.FirstOrDefault()?.Expression;

        if (refKind == "ctor" && IsAttributeName(name))
            return name.FirstAncestorOrSelf<AttributeSyntax>()?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;

        return null;
    }

    // First-argument facts for the given argument expression: its string template (literal or
    // interpolated, via StringTemplateExtensions — the same helper the Roslyn EffectExtractor uses
    // for http_argument/string_argument) and its static type (open-generic FQN, for argument_type).
    // Returns (null, null) for a null argument.
    private static (string? Template, string? Type) FirstArgumentOf(ExpressionSyntax? argument, SemanticModel model)
    {
        if (argument is null)
            return (null, null);

        var template = argument.GetStringTemplate();
        var type = model.GetTypeInfo(argument).Type?.OriginalDefinition.ToDisplayString();
        return (template, type);
    }

    // Structural-context facts for an invocation (P1c) — the rule-agnostic raw structure the Roslyn
    // EffectObservationExtractor walks ancestors for. Mirrors its three ancestor scans exactly:
    //   * nearest enclosing loop (foreach/for/while) -> looped_effect
    //   * the chain of enclosing (ancestor) member-access invocations, innermost-first -> the
    //     receiver-text/method match for parallel_fanout and the receiver-type/method match for
    //     resilience_retry
    //   * caught exception types of all enclosing try/catch clauses -> concurrency_handled
    // Returns all-null for a null node (non-invocation ref). Generalized to any node so throw
    // operands carry the same loop/try-catch context as invocations.
    private static StructuralContext StructuralContextOf(SyntaxNode? invocation, SemanticModel model)
    {
        if (invocation is null)
            return default;

        string? loopKind = null;
        string? loopDetail = null;
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case ForEachStatementSyntax forEach:
                    loopKind = "foreach";
                    loopDetail = $"{forEach.Identifier.ValueText} in {forEach.Expression}";
                    break;
                case ForStatementSyntax:
                    loopKind = "for";
                    loopDetail = "for";
                    break;
                case WhileStatementSyntax:
                    loopKind = "while";
                    loopDetail = "while";
                    break;
            }

            if (loopKind is not null)
                break;
        }

        var enclosing = new List<FactStructuralContext.EnclosingInvocation>();
        foreach (var ancestor in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var receiverText = memberAccess.Expression.ToString();
            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type?.OriginalDefinition.ToDisplayString() ?? "";
            enclosing.Add(
                new FactStructuralContext.EnclosingInvocation(receiverText, receiverType, memberAccess.Name.Identifier.ValueText)
            );
        }

        var catchTypes = new List<string>();
        foreach (var tryStatement in invocation.Ancestors().OfType<TryStatementSyntax>())
        {
            foreach (var catchClause in tryStatement.Catches)
            {
                if (catchClause.Declaration is not null)
                    catchTypes.Add(model.GetTypeInfo(catchClause.Declaration.Type).Type?.ToDisplayString() ?? "");
            }
        }

        return new StructuralContext(
            loopKind,
            loopDetail,
            FactStructuralContext.EncodeInvocations(enclosing),
            FactStructuralContext.EncodeList(catchTypes)
        );
    }

    private readonly record struct StructuralContext(
        string? LoopKind,
        string? LoopDetail,
        string? EnclosingInvocations,
        string? CatchTypes
    );

    // Operands of throw statements and throw expressions (the thrown value). `throw;` rethrows carry
    // no operand and are skipped — there is no static type to record.
    private static IEnumerable<ExpressionSyntax> ThrownExpressions(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is ThrowStatementSyntax { Expression: { } stmtOperand })
                yield return stmtOperand;
            else if (node is ThrowExpressionSyntax exprThrow)
                yield return exprThrow.Expression;
        }
    }

    // The InvocationExpressionSyntax this name is the invoked method of: `Foo(..)`, `a.Foo(..)`, or
    // `a?.Foo(..)`. Null otherwise (mirrors IsInvoked's shapes, plus the conditional-access form).
    private static InvocationExpressionSyntax? InvocationOf(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax direct && direct.Expression == name)
            return direct;
        if (name.Parent is MemberAccessExpressionSyntax member
            && member.Name == name
            && member.Parent is InvocationExpressionSyntax memberInvocation
            && memberInvocation.Expression == member)
            return memberInvocation;
        if (name.Parent is MemberBindingExpressionSyntax binding
            && binding.Parent is InvocationExpressionSyntax conditionalInvocation)
            return conditionalInvocation;
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
        // Accessibility first (e.g. "public", "private", "internal", "protected internal"). Roslyn's
        // Modifiers previously omitted this; the dead-code finder tiers candidates by visibility
        // (private uncalled = high confidence; public = possible external API), so it's recorded here.
        var access = AccessibilityOf(symbol.DeclaredAccessibility);
        if (access is not null) parts.Add(access);
        if (symbol.IsStatic) parts.Add("static");
        if (symbol.IsAbstract) parts.Add("abstract");
        if (symbol.IsSealed) parts.Add("sealed");
        if (symbol.IsVirtual) parts.Add("virtual");
        if (symbol.IsOverride) parts.Add("override");
        if (symbol is IMethodSymbol { IsAsync: true }) parts.Add("async");
        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true }) parts.Add("readonly");
        return string.Join(' ', parts);
    }

    private static string? AccessibilityOf(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Private => "private",
        Accessibility.Internal => "internal",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => null,
    };

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
