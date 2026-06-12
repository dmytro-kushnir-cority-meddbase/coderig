using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        var dispatch = new List<DispatchFact>();
        var dispatchSeen = new HashSet<(string, string, string)>();

        // --- Declarations -> SymbolFact (+ TypeRelation for type base/interface edges, DispatchFact
        //     for exact member-level dispatch) ---
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
            {
                AddTypeRelations(relations, typeSymbol, docId);
                AddInterfaceDispatchFacts(dispatch, dispatchSeen, typeSymbol);
            }

            // Property/indexer accessors with a real body are first-class callable methods: emit them
            // as method symbols (so they become graph NODES — renderable, dispatch-resolvable) and
            // carry their override edges, exactly like ordinary methods. Auto-property accessors (no
            // body) are skipped: no effect to walk, and emitting them would bloat the graph with trivial
            // get_/set_ leaves. The CALL edges into these accessors are emitted at the access sites below.
            if (symbol is IPropertySymbol property)
            {
                foreach (var accessor in Accessors(property))
                {
                    if (!HasAccessorBody(accessor))
                        continue;
                    AddSymbol(symbols, accessor, tree, AccessorNode(accessor) ?? decl);
                    if (accessor.OverriddenMethod is { } overriddenAccessor)
                        AddDispatchFact(dispatch, dispatchSeen, overriddenAccessor, accessor, "override");
                }
            }

            // EXACT override edge: the immediate base→override hop, resolved by Roslyn (no name/arity
            // guessing). The transitive chain (A.M ← B.M ← C.M) is reconstructed by forward closure at
            // query time, so only the immediate hop is stored.
            if (symbol is IMethodSymbol { OverriddenMethod: { } overridden } overrideMethod)
                AddDispatchFact(dispatch, dispatchSeen, overridden, overrideMethod, "override");
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
            var (firstArgTemplate, firstArgType, firstArgName) = FirstArgumentOf(
                FirstArgumentExpressionOf(name, refKind, invocation),
                model
            );
            var structural = StructuralContextOf(invocation, model);
            var delegateConsumer = refKind == "methodGroup" ? DelegateConsumerOf(name, model) : null;
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
                structural,
                firstArgumentName: firstArgName,
                delegateConsumer: delegateConsumer
            );

            // A property/indexer access is, semantically, a call to its get_/set_ accessor. The
            // read/write ref above records the data-flow touch; this records the call EDGE into a bodied
            // accessor so reach walks its effects (a setter that validates/persists, a lazy getter that
            // fetches). See AddAccessorInvocations for the body-only selectivity.
            if (target is IPropertySymbol propertyAccess && refKind is "read" or "write")
                AddAccessorInvocations(references, propertyAccess, name, model, tree);
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
                references,
                type,
                "throw",
                EnclosingSymbolId(thrown, model),
                tree,
                thrown,
                structural: StructuralContextOf(thrown, model),
                allowRuntime: true
            );
        }

        return new FactExtractionResult(symbols, references, relations, dispatch);
    }

    // EXACT interface-impl dispatch edges for a declared type: for every interface the type implements
    // (AllInterfaces — direct AND inherited, so a call through a base interface still finds the impl),
    // for every ordinary interface method, FindImplementationForInterfaceMember resolves the EXACT
    // implementing method — signature-correct and generic-correct (IFoo`1.M(`0) → Bar.M(System.Int32)),
    // including explicit interface implementations and impls inherited from a base class — everything
    // name/arity matching guesses at. The SOURCE may be a framework interface (kept: a first-party call
    // can resolve to it); the TARGET must be first-party (only first-party methods are graph nodes).
    private static void AddInterfaceDispatchFacts(
        List<DispatchFact> dispatch,
        HashSet<(string, string, string)> seen,
        INamedTypeSymbol type
    )
    {
        if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            return;

        foreach (var iface in type.AllInterfaces)
        foreach (var member in iface.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } interfaceMethod:
                    if (type.FindImplementationForInterfaceMember(interfaceMethod) is IMethodSymbol impl)
                        AddDispatchFact(dispatch, seen, interfaceMethod, impl, "impl");
                    break;

                // Interface PROPERTY members resolve to the impl property's accessors — the same typed
                // dispatch as methods (IFoo.Bar setter → Bar.set on the concrete impl). Only bodied impl
                // accessors are wired (auto-property impls have no effect; their get_/set_ leaves would
                // bloat the graph and are never call-edge targets, since access sites only emit edges to
                // bodied accessors).
                case IPropertySymbol interfaceProperty
                    when type.FindImplementationForInterfaceMember(interfaceProperty) is IPropertySymbol implProperty:
                    AddAccessorImplDispatch(dispatch, seen, interfaceProperty.GetMethod, implProperty.GetMethod);
                    AddAccessorImplDispatch(dispatch, seen, interfaceProperty.SetMethod, implProperty.SetMethod);
                    break;
            }
        }
    }

    private static void AddAccessorImplDispatch(
        List<DispatchFact> dispatch,
        HashSet<(string, string, string)> seen,
        IMethodSymbol? interfaceAccessor,
        IMethodSymbol? implAccessor
    )
    {
        if (interfaceAccessor is not null && implAccessor is not null && HasAccessorBody(implAccessor))
            AddDispatchFact(dispatch, seen, interfaceAccessor, implAccessor, "impl");
    }

    // Emits one deduped (Source, Target, Kind) dispatch fact keyed by OriginalDefinition DocIDs (the
    // same identity call edges use, so generic instantiations join). Dedup is per-file; cross-file
    // duplicates (partial types, subtypes re-walking inherited interfaces) collapse at load time.
    private static void AddDispatchFact(
        List<DispatchFact> dispatch,
        HashSet<(string, string, string)> seen,
        IMethodSymbol source,
        IMethodSymbol target,
        string kind
    )
    {
        var resolvedTarget = target.OriginalDefinition;
        // Only first-party targets become graph nodes; a metadata-only impl/override can't carry facts.
        if (!resolvedTarget.Locations.Any(location => location.IsInSource))
            return;

        var sourceId = source.OriginalDefinition.GetDocumentationCommentId();
        var targetId = resolvedTarget.GetDocumentationCommentId();
        if (sourceId is null || targetId is null || sourceId == targetId)
            return;

        if (seen.Add((sourceId, targetId, kind)))
            dispatch.Add(new DispatchFact(sourceId, targetId, kind));
    }

    private static void AddSymbol(List<SymbolFact> symbols, ISymbol symbol, SyntaxTree tree, SyntaxNode node)
    {
        var docId = symbol.GetDocumentationCommentId();
        if (docId is null)
            return;

        symbols.Add(
            new SymbolFact(
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
            )
        );
    }

    private static void AddTypeRelations(List<TypeRelationFact> relations, INamedTypeSymbol type, string typeDocId)
    {
        if (type.BaseType is { SpecialType: SpecialType.None } baseType && baseType.GetDocumentationCommentId() is { } baseDocId)
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
        bool allowRuntime = false,
        string? firstArgumentName = null,
        string? delegateConsumer = null
    )
    {
        // Generic type arguments at the CALL SITE — read from the constructed `target` BEFORE
        // OriginalDefinition strips them below (e.g. `ask<PaymentGatewayResponse<T>>` → that type).
        var typeArguments = target is IMethodSymbol { TypeArguments.Length: > 0 } generic
            ? string.Join(",", generic.TypeArguments.Select(t => t.ToDisplayString()))
            : null;

        // For constructors, point the reference at the constructor's containing type's ctor DocID;
        // for everything else use the symbol's own DocID. Reduced extension methods resolve to the
        // original definition so the DocID matches the declaration.
        var resolved = target is IMethodSymbol method ? (method.ReducedFrom ?? method).OriginalDefinition : target.OriginalDefinition;
        var docId = resolved.GetDocumentationCommentId();
        if (docId is null)
            return;

        var inSource = resolved.Locations.Any(loc => loc.IsInSource);
        var assembly = resolved.ContainingAssembly?.Name ?? "";

        // Keep ALL method-call facts (invocation/ctor) regardless of assembly — they are the complete
        // set any future effect rule (incl. BCL: HttpClient, System.IO, sockets, locks, caches, …) can
        // match WITHOUT a re-mine. Storage is cheap; re-extraction is the expensive thing, so capture
        // once and filter at query time (the call graph keeps only first-party callees — see
        // Reads.LoadFactGraphAsync — so reaches/tree stay clean; derive matches over the full set).
        // For the NON-effect ref kinds (typeUse/read/write/methodGroup) the runtime/BCL drop still
        // applies: those are pervasive pure noise (every `string`, `.Count`, `.ToString` group) with no
        // effect consumer. allowRuntime additionally keeps runtime throws (the throw site is ours).
        var isCallFact = refKind is "invocation" or "ctor";
        if (!inSource && !allowRuntime && !isCallFact && IsRuntimeAssembly(assembly))
            return;

        references.Add(
            new ReferenceFact(
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
                EnclosingCatchTypes: structural.CatchTypes,
                TypeArguments: typeArguments,
                FirstArgumentName: firstArgumentName,
                DelegateConsumer: delegateConsumer
            )
        );
    }

    // Static type of an invocation's receiver: `a.Foo()` -> type of `a` (open-generic FQN).
    // Bare `Foo()` (implicit this) and other shapes return null — only explicit member-access
    // receivers carry a receiver-type fact.
    private static string? ReceiverTypeOf(SimpleNameSyntax name, SemanticModel model)
    {
        if (name.Parent is MemberAccessExpressionSyntax member && member.Name == name)
            return model.GetTypeInfo(member.Expression).Type?.OriginalDefinition.ToDisplayString();

        if (name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is ConditionalAccessExpressionSyntax conditional)
            return model.GetTypeInfo(conditional.Expression).Type?.OriginalDefinition.ToDisplayString();

        return null;
    }

    // The first-argument expression whose literal/type becomes a fact: an invocation's first
    // argument (http_argument/string_argument/argument_type, P1b) or — for an attribute usage,
    // which resolves to the attribute constructor and is recorded as a "ctor" ref — the attribute's
    // first positional argument, exposing MVC route literals ([Route("..")], [HttpGet("..")]) to the
    // entry-point deriver (P1d). Null for any other ref shape.
    private static ExpressionSyntax? FirstArgumentExpressionOf(
        SimpleNameSyntax name,
        string refKind,
        InvocationExpressionSyntax? invocation
    )
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
    private static (string? Template, string? Type, string? Name) FirstArgumentOf(ExpressionSyntax? argument, SemanticModel model)
    {
        if (argument is null)
            return (null, null, null);

        var template = argument.GetStringTemplate();
        var type = model.GetTypeInfo(argument).Type?.OriginalDefinition.ToDisplayString();
        // Member/identifier path of the argument (the routing target / discriminator, e.g.
        // `PaymentGatewayProcessDns.AccountService`); null for literals and other expression shapes.
        var name = argument is MemberAccessExpressionSyntax or IdentifierNameSyntax ? argument.ToString() : null;
        return (template, type, name);
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
        if (
            name.Parent is MemberAccessExpressionSyntax member
            && member.Name == name
            && member.Parent is InvocationExpressionSyntax memberInvocation
            && memberInvocation.Expression == member
        )
            return memberInvocation;
        if (name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is InvocationExpressionSyntax conditionalInvocation)
            return conditionalInvocation;
        return null;
    }

    // For a method-group `name` handed as an ARGUMENT to a call/`new`, the DocID of that consuming
    // invocation/constructor — the delegate's CONSUMER. Found by walking ancestors through the
    // transparent wrappers between a method-group and the argument list it sits in (member access,
    // conditional access, cast, parens, the argument + argument-list nodes) to the first enclosing
    // InvocationExpression or object-creation. Any other intervening node (an assignment for `+=`, an
    // equals-value clause for a delegate field/local, a lambda body, a statement) means the method-group
    // is NOT a call argument, so it returns null and the edge stays unclassified — the recall rail.
    // Line-placement-agnostic by construction: a multi-line `new(\n .., Callback,\n ..)` resolves the
    // same consumer as a single-line one, which the exact-same-line co-location heuristic missed.
    private static string? DelegateConsumerOf(SimpleNameSyntax name, SemanticModel model)
    {
        foreach (var ancestor in name.Ancestors())
        {
            switch (ancestor)
            {
                case InvocationExpressionSyntax invocation:
                    return ConsumerDocId(model.GetSymbolInfo(invocation).Symbol);
                case BaseObjectCreationExpressionSyntax creation:
                    return ConsumerDocId(model.GetSymbolInfo(creation).Symbol);
                case MemberAccessExpressionSyntax:
                case MemberBindingExpressionSyntax:
                case ConditionalAccessExpressionSyntax:
                case ParenthesizedExpressionSyntax:
                case CastExpressionSyntax:
                case ArgumentSyntax:
                case ArgumentListSyntax:
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    // The consuming method/constructor's DocID, resolved to its original definition so it matches the
    // ctor/invocation TargetSymbolId the rest of the extractor records (handoff ConsumerPatterns
    // substring-match this). Null if the symbol didn't bind.
    private static string? ConsumerDocId(ISymbol? symbol)
    {
        if (symbol is not IMethodSymbol method)
            return symbol?.OriginalDefinition.GetDocumentationCommentId();
        return (method.ReducedFrom ?? method).OriginalDefinition.GetDocumentationCommentId();
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

    // Emits the call edge(s) into a property/indexer's accessor(s) for one access site. Only first-party
    // accessors with a real body are emitted — auto-property accessors (`get;`/`set;`) carry no effect, so
    // walking them adds nothing but width. The receiver type and structural context ride along exactly as
    // for ordinary invocations, so typed/virtual property dispatch narrows and looped accessor effects show.
    private static void AddAccessorInvocations(
        List<ReferenceFact> references,
        IPropertySymbol property,
        SimpleNameSyntax name,
        SemanticModel model,
        SyntaxTree tree
    )
    {
        var (reads, writes) = AccessShape(name);
        var getter = reads && property.GetMethod is { } g && HasAccessorBody(g) ? g : null;
        var setter = writes && property.SetMethod is { } s && HasAccessorBody(s) ? s : null;
        if (getter is null && setter is null)
            return;

        var enclosing = EnclosingSymbolId(name, model);
        var receiver = ReceiverTypeOf(name, model);
        var structural = StructuralContextOf(name, model);
        if (getter is not null)
            AddReference(references, getter, "invocation", enclosing, tree, name, receiver, structural: structural);
        if (setter is not null)
            AddReference(references, setter, "invocation", enclosing, tree, name, receiver, structural: structural);
    }

    // Read/write shape of a property access: a plain read -> (read); a simple `=` assignment -> (write
    // only, the prior value is discarded); a compound assignment (`+=`) and increment/decrement -> both
    // (the get_ and set_ accessors both run). Mirrors the access forms IsWriteTarget collapses to "write".
    private static (bool Read, bool Write) AccessShape(SimpleNameSyntax name)
    {
        var expr = name.Parent is MemberAccessExpressionSyntax m && m.Name == name ? (ExpressionSyntax)m : name;
        return expr.Parent switch
        {
            AssignmentExpressionSyntax assignment when assignment.Left == expr => (
                !assignment.OperatorToken.IsKind(SyntaxKind.EqualsToken),
                true
            ),
            PrefixUnaryExpressionSyntax prefix
                when prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression) => (true, true),
            PostfixUnaryExpressionSyntax postfix
                when postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression) => (
                true,
                true
            ),
            _ => (true, false),
        };
    }

    private static IEnumerable<IMethodSymbol> Accessors(IPropertySymbol property)
    {
        if (property.GetMethod is { } getter)
            yield return getter;
        if (property.SetMethod is { } setter)
            yield return setter;
    }

    // True for a first-party accessor with a REAL body: a full `get {…}`/`set {…}`, an expression-bodied
    // accessor (`get => …`), or an expression-bodied read-only property/indexer (`public int P => …`).
    // Auto-property accessors (`get;`/`set;`/`init;`) and metadata accessors (no DeclaringSyntaxReferences)
    // return false — there is nothing to walk, and emitting them would bloat the graph.
    private static bool HasAccessorBody(IMethodSymbol accessor)
    {
        foreach (var reference in accessor.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax())
            {
                case AccessorDeclarationSyntax { Body: not null }:
                case AccessorDeclarationSyntax { ExpressionBody: not null }:
                case ArrowExpressionClauseSyntax:
                case PropertyDeclarationSyntax { ExpressionBody: not null }:
                case IndexerDeclarationSyntax { ExpressionBody: not null }:
                    return true;
            }
        }
        return false;
    }

    private static SyntaxNode? AccessorNode(IMethodSymbol accessor) => accessor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

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
        if (access is not null)
            parts.Add(access);
        if (symbol.IsStatic)
            parts.Add("static");
        if (symbol.IsAbstract)
            parts.Add("abstract");
        if (symbol.IsSealed)
            parts.Add("sealed");
        if (symbol.IsVirtual)
            parts.Add("virtual");
        if (symbol.IsOverride)
            parts.Add("override");
        if (symbol is IMethodSymbol { IsAsync: true })
            parts.Add("async");
        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true })
            parts.Add("readonly");
        return string.Join(' ', parts);
    }

    private static string? AccessibilityOf(Accessibility accessibility) =>
        accessibility switch
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
    IReadOnlyList<TypeRelationFact> TypeRelations,
    IReadOnlyList<DispatchFact> Dispatch
);
