using System.Text.Json;
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

        // --- Lambda identity (18b): synthesize a symbol + handoff edge for each argument-passed lambda
        //     BEFORE the reference pass, so EnclosingSymbolId can re-root the lambda body's facts. ---
        var lambdaIds = CollectLambdaSymbols(root, model, tree, symbols, references);

        // --- Declarations -> SymbolFact (+ TypeRelation for type base/interface edges, DispatchFact
        //     for exact member-level dispatch) ---
        void OnDeclaration(MemberDeclarationSyntax decl)
        {
            var symbol = model.GetDeclaredSymbol(decl);
            if (symbol is null)
            {
                return;
            }

            // Field/event declarations declare one symbol per variable; handle below.
            if (decl is BaseFieldDeclarationSyntax fieldDecl)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable) is { } fieldSymbol)
                    {
                        AddSymbol(symbols, fieldSymbol, tree, variable);
                    }
                }
                return;
            }

            var docId = symbol.GetDocumentationCommentId();
            if (docId is null)
            {
                return;
            }

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
                    {
                        continue;
                    }

                    AddSymbol(symbols, accessor, tree, AccessorNode(accessor) ?? decl);
                    if (accessor.OverriddenMethod is { } overriddenAccessor)
                    {
                        AddDispatchFact(dispatch, dispatchSeen, source: overriddenAccessor, target: accessor, kind: DispatchKinds.Override);
                    }
                }
            }

            // EXACT override edge: the immediate base→override hop, resolved by Roslyn (no name/arity
            // guessing). The transitive chain (A.M ← B.M ← C.M) is reconstructed by forward closure at
            // query time, so only the immediate hop is stored.
            if (symbol is IMethodSymbol { OverriddenMethod: { } overridden } overrideMethod)
            {
                AddDispatchFact(dispatch, dispatchSeen, source: overridden, target: overrideMethod, kind: DispatchKinds.Override);
            }
        }

        // --- References -> ReferenceFact (one pass over every simple name) ---
        void OnName(SimpleNameSyntax name)
        {
            // Fall back to a candidate symbol when Roslyn can't fully bind. Under net48 cross-assembly
            // partial binding (`!:` DocIDs) a real, in-source call often resolves only to a CandidateSymbol
            // (CandidateReason.OverloadResolutionFailure et al.) — dropping it silently loses effect-bearing
            // edges (F1b: e.g. first-party `FileExt.Move` in a monadic query). Overloads of the same method
            // share declaring type + name (all the effect/EP rules key on), so the first candidate is a safe
            // proxy for reachability. RoslynSymbolHelpers already does this for dispatch resolution.
            var symbolInfo = model.GetSymbolInfo(name);
            var target = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
            if (target is null || target is INamespaceSymbol)
            {
                return;
            }

            var refKind = ClassifyReference(name, target);
            if (refKind is null)
            {
                return;
            }

            var invocation = refKind == RefKinds.Invocation ? InvocationOf(name) : null;
            var receiverType = refKind == RefKinds.Invocation ? ReceiverTypeOf(name, model) : null;
            var (firstArgTemplate, firstArgType, firstArgName) = FirstArgumentOf(
                FirstArgumentExpressionOf(name, refKind, invocation),
                model
            );
            var (argumentTemplates, argumentNames) = ArgumentListOf(refKind, invocation, model);
            var structural = StructuralContextOf(invocation, model);
            var delegateConsumer = refKind == RefKinds.MethodGroup ? DelegateConsumerOf(name, model) : null;
            AddReference(
                references,
                target,
                refKind: refKind,
                enclosingId: EnclosingSymbolId(name, model, lambdaIds),
                tree: tree,
                node: name,
                receiverType: receiverType,
                firstArgumentTemplate: firstArgTemplate,
                firstArgumentType: firstArgType,
                structural: structural,
                firstArgumentName: firstArgName,
                delegateConsumer: delegateConsumer,
                argumentTemplates: argumentTemplates,
                argumentNames: argumentNames
            );

            // 18c: a method-group ASSIGNED to a delegate field/property/event (not passed as an
            // argument — that's the 18b handoff) is a binding. Emit a delegate_bind dispatch fact
            // (slot -> bound target) so the seam resolver can resolve `slot()` to its target.
            if (refKind == RefKinds.MethodGroup && DelegateBindSlotOf(name, model) is { } slot)
            {
                var resolvedTarget = target is IMethodSymbol bound
                    ? (bound.ReducedFrom ?? bound).OriginalDefinition
                    : target.OriginalDefinition;
                if (
                    resolvedTarget.GetDocumentationCommentId() is { } boundId
                    && dispatchSeen.Add((slot, boundId, DispatchKinds.DelegateBind))
                )
                {
                    dispatch.Add(new DispatchFact(SourceMember: slot, TargetMember: boundId, Kind: DispatchKinds.DelegateBind));
                }
            }

            // A property/indexer access is, semantically, a call to its get_/set_ accessor. The
            // read/write ref above records the data-flow touch; this records the call EDGE into a bodied
            // accessor so reach walks its effects (a setter that validates/persists, a lazy getter that
            // fetches). See AddAccessorInvocations for the body-only selectivity.
            if (target is IPropertySymbol propertyAccess && refKind is RefKinds.Read or RefKinds.Write)
            {
                AddAccessorInvocations(references, propertyAccess, name, model, tree, lambdaIds);
            }
        }

        // --- Object creations -> ctor refs ---
        // GetSymbolInfo on a type *name* resolves to the type (recorded as typeUse above), never the
        // constructor — so `new XxxEntity(pk)` would otherwise carry no constructor/argument fact.
        // Resolve the invoked constructor here so ctor-matched effect rules (the llblgen entity-ctor
        // fetch, gap G5) can see the constructed type and its argument count from the ctor DocID.
        void OnCreation(BaseObjectCreationExpressionSyntax creation)
        {
            if (model.GetSymbolInfo(creation).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            {
                AddReference(
                    references,
                    ctor,
                    refKind: RefKinds.Ctor,
                    enclosingId: EnclosingSymbolId(creation, model, lambdaIds),
                    tree: tree,
                    node: creation
                );
            }
        }

        // --- 18c: delegate-slot INVOCATIONS -> an invocation edge to the SLOT ---
        // `_handler()` / `Prop()` invokes a delegate via its slot's (field/property/event) Invoke; the
        // SimpleName pass only records a field READ, so the call target is otherwise invisible. Emit an
        // invocation edge enclosing -> slot so the seam resolver can dispatch the slot to its bound
        // target(s) via the delegate_bind facts (the delegate-as-degenerate-interface hop).
        void OnInvocation(InvocationExpressionSyntax invocation)
        {
            if (DelegateSlotDocId(model.GetSymbolInfo(invocation.Expression).Symbol) is not { } slot)
            {
                return;
            }

            references.Add(
                new ReferenceFact(
                    TargetSymbolId: slot,
                    RefKind: RefKinds.Invocation,
                    EnclosingSymbolId: EnclosingSymbolId(invocation, model, lambdaIds),
                    TargetAssembly: model.Compilation.AssemblyName ?? "",
                    TargetInSource: true,
                    FilePath: tree.FilePath,
                    Line: tree.GetLineSpan(invocation.Span).StartLinePosition.Line + 1
                )
            );
        }

        // --- Throw sites -> "throw" refs (the thrown exception TYPE) ---
        // A `throw` is first-party control flow, so — unlike calls INTO the BCL — we keep throws of
        // runtime exception types too (the throw SITE is ours); allowRuntime bypasses the runtime-
        // assembly filter. The target is the exception TYPE (not its ctor) so error/permission effect
        // rules can gate on the type name / base type. Bare `throw;` rethrows have no operand and are
        // skipped. Structural context (enclosing try/catch + loop) rides along like invocation refs.
        void OnThrow(ExpressionSyntax thrown)
        {
            var type = model.GetTypeInfo(thrown).Type;
            if (type is null or IErrorTypeSymbol)
            {
                return;
            }

            AddReference(
                references,
                type,
                refKind: RefKinds.Throw,
                enclosingId: EnclosingSymbolId(thrown, model, lambdaIds),
                tree: tree,
                node: thrown,
                structural: StructuralContextOf(thrown, model),
                allowRuntime: true
            );
        }

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case BaseObjectCreationExpressionSyntax creation:
                    OnCreation(creation);
                    break;
                case InvocationExpressionSyntax invocation:
                    OnInvocation(invocation);
                    break;
                case MemberDeclarationSyntax decl:
                    OnDeclaration(decl);
                    break;
                case SimpleNameSyntax name:
                    OnName(name);
                    break;

                case ThrowStatementSyntax { Expression: { } stmtOperand }:
                    OnThrow(stmtOperand);
                    break;
                case ThisExpressionSyntax exprThrow:
                    OnThrow(exprThrow);
                    break;

                default:
                    continue;
            }
        }

        // --- lock(x){} statements -> synthetic Monitor.Enter/Exit invocation refs ---
        // The C# language spec DEFINES `lock (x) S` to lower to
        //   Monitor.Enter(x, ref f); try { S } finally { if (f) Monitor.Exit(x); }
        // — but the lock keyword carries no invocation SYNTAX, so the SimpleName pass above never sees
        // these calls. Without this, a `lock {}` block carries NO lock effect, even though an explicit
        // `Monitor.Enter(x)` call in the same body would (the lock-acquire rule already matches it).
        // We record the spec-guaranteed lowered calls — acquire at the lock keyword, release at the
        // body's closing brace — and let the existing data-driven lock rules classify them. The
        // DETECTION stays in rules (builtin-rules.json); this only records a structural fact the
        // language guarantees, exactly as the ctor/throw passes record their constructs.
        AddLockStatementRefs(references, root, model, tree, lambdaIds);

        return new FactExtractionResult(symbols, references, relations, dispatch);
    }

    // Emit synthetic Monitor.Enter (acquire) and Monitor.Exit (release) invocation refs for every
    // `lock (x) {}` statement, resolving the real Monitor method symbols from the compilation so the
    // refs carry genuine DocIds (the same the lock rule's declaringTypes gate matches). The release is
    // pinned to the body's closing-brace line so the acquire/release straddle the locked body — the
    // lexical span the ordering work (transaction/lock-held-across-IO) will read.
    private static void AddLockStatementRefs(
        List<ReferenceFact> references,
        SyntaxNode root,
        SemanticModel model,
        SyntaxTree tree,
        IReadOnlyDictionary<SyntaxNode, string> lambdaIds
    )
    {
        var locks = root.DescendantNodes().OfType<LockStatementSyntax>().ToArray();
        if (locks.Length == 0)
        {
            return;
        }

        var monitor = model.Compilation.GetTypeByMetadataName("System.Threading.Monitor");
        var enter = monitor?.GetMembers("Enter").OfType<IMethodSymbol>().FirstOrDefault();
        var exit = monitor?.GetMembers("Exit").OfType<IMethodSymbol>().FirstOrDefault();
        if (enter is null || exit is null)
        {
            return; // no Monitor in this compilation's references — nothing to lower against.
        }

        foreach (var lockStmt in locks)
        {
            var enclosing = EnclosingSymbolId(lockStmt, model, lambdaIds);
            var structural = StructuralContextOf(lockStmt, model);

            // acquire: at the `lock` keyword / locked expression. allowRuntime keeps the BCL ref.
            AddReference(
                references,
                enter,
                refKind: RefKinds.Invocation,
                enclosingId: enclosing,
                tree: tree,
                node: lockStmt.Expression,
                structural: structural,
                allowRuntime: true
            );

            // release: at the closing brace of the block (or the embedded statement's last line).
            var releaseLine = tree.GetLineSpan(lockStmt.Statement.Span).EndLinePosition.Line + 1;
            AddReference(
                references,
                exit,
                refKind: RefKinds.Invocation,
                enclosingId: enclosing,
                tree: tree,
                node: lockStmt.Expression,
                structural: structural,
                allowRuntime: true,
                lineOverride: releaseLine
            );
        }
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
        {
            return;
        }

        foreach (var iface in type.AllInterfaces)
        foreach (var member in iface.GetMembers())
        {
            switch (member)
            {
                case IMethodSymbol { MethodKind: MethodKind.Ordinary } interfaceMethod:
                    if (type.FindImplementationForInterfaceMember(interfaceMethod) is IMethodSymbol impl)
                    {
                        AddDispatchFact(dispatch, seen, source: interfaceMethod, target: impl, kind: DispatchKinds.Impl);
                    }

                    break;

                // Interface PROPERTY members resolve to the impl property's accessors — the same typed
                // dispatch as methods (IFoo.Bar setter → Bar.set on the concrete impl). Only bodied impl
                // accessors are wired (auto-property impls have no effect; their get_/set_ leaves would
                // bloat the graph and are never call-edge targets, since access sites only emit edges to
                // bodied accessors).
                case IPropertySymbol interfaceProperty
                    when type.FindImplementationForInterfaceMember(interfaceProperty) is IPropertySymbol implProperty:
                    AddAccessorImplDispatch(
                        dispatch,
                        seen,
                        interfaceAccessor: interfaceProperty.GetMethod,
                        implAccessor: implProperty.GetMethod
                    );
                    AddAccessorImplDispatch(
                        dispatch,
                        seen,
                        interfaceAccessor: interfaceProperty.SetMethod,
                        implAccessor: implProperty.SetMethod
                    );
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
        {
            AddDispatchFact(dispatch, seen, source: interfaceAccessor, target: implAccessor, kind: DispatchKinds.Impl);
        }
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
        {
            return;
        }

        var sourceId = source.OriginalDefinition.GetDocumentationCommentId();
        var targetId = resolvedTarget.GetDocumentationCommentId();
        if (sourceId is null || targetId is null || sourceId == targetId)
        {
            return;
        }

        if (seen.Add((sourceId, targetId, kind)))
        {
            dispatch.Add(new DispatchFact(SourceMember: sourceId, TargetMember: targetId, Kind: kind));
        }
    }

    private static void AddSymbol(List<SymbolFact> symbols, ISymbol symbol, SyntaxTree tree, SyntaxNode node)
    {
        var docId = symbol.GetDocumentationCommentId();
        if (docId is null)
        {
            return;
        }

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
            relations.Add(new TypeRelationFact(TypeSymbolId: typeDocId, RelatedSymbolId: baseDocId, RelationKind: "base"));
        }

        foreach (var iface in type.Interfaces)
        {
            if (iface.GetDocumentationCommentId() is { } ifaceDocId)
            {
                relations.Add(new TypeRelationFact(TypeSymbolId: typeDocId, RelatedSymbolId: ifaceDocId, RelationKind: "interface"));
            }
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
        string? delegateConsumer = null,
        int? lineOverride = null,
        string? argumentTemplates = null,
        string? argumentNames = null
    )
    {
        // Generic type arguments at the CALL SITE — read from the constructed `target` BEFORE
        // OriginalDefinition strips them below (e.g. `ask<PaymentGatewayResponse<T>>` → that type).
        var typeArguments = target is IMethodSymbol { TypeArguments.Length: > 0 } generic
            ? string.Join(',', generic.TypeArguments.Select(t => t.ToDisplayString()))
            : null;

        // Generic monomorphization bindings (RENDERING only) — see ReferenceFact. The DECLARING binding is
        // the callee's containing-type instantiation at this site (receiver/qualifier for a call, the
        // constructed type for a ctor, the owning type for a property/field read — e.g. `pipeline.Enumerate`
        // where Enumerate is a `Func<…>` property on QueryPipeline<TRecord, TColumn>); the METHOD binding is
        // the callee's own type args. Each position is encoded C:/T:/M:/? so the renderer can resolve
        // forwarded params against the parent's binding.
        var constructed = target as IMethodSymbol;
        var declaringContainer = constructed is not null ? (constructed.ReducedFrom ?? constructed).ContainingType : target.ContainingType;
        var declaringTypeArgBinding = GenericArgBinding(declaringContainer?.TypeArguments);
        var methodTypeArgBinding = GenericArgBinding(constructed?.TypeArguments);

        // For constructors, point the reference at the constructor's containing type's ctor DocID;
        // for everything else use the symbol's own DocID. Reduced extension methods resolve to the
        // original definition so the DocID matches the declaration.
        var resolved = target is IMethodSymbol method ? (method.ReducedFrom ?? method).OriginalDefinition : target.OriginalDefinition;
        var docId = resolved.GetDocumentationCommentId();
        if (docId is null)
        {
            return;
        }

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
        var isCallFact = refKind is RefKinds.Invocation or RefKinds.Ctor;
        if (!inSource && !allowRuntime && !isCallFact && IsRuntimeAssembly(assembly))
        {
            return;
        }

        references.Add(
            new ReferenceFact(
                TargetSymbolId: docId,
                RefKind: refKind,
                EnclosingSymbolId: enclosingId,
                TargetAssembly: assembly,
                TargetInSource: inSource,
                FilePath: tree.FilePath,
                Line: lineOverride ?? tree.GetLineSpan(node.Span).StartLinePosition.Line + 1,
                ReceiverType: receiverType,
                FirstArgumentTemplate: firstArgumentTemplate,
                FirstArgumentType: firstArgumentType,
                EnclosingLoopKind: structural.LoopKind,
                EnclosingLoopDetail: structural.LoopDetail,
                EnclosingInvocations: structural.EnclosingInvocations,
                EnclosingCatchTypes: structural.CatchTypes,
                TypeArguments: typeArguments,
                FirstArgumentName: firstArgumentName,
                DelegateConsumer: delegateConsumer,
                EnclosingScopes: structural.EnclosingScopes,
                ArgumentTemplates: argumentTemplates,
                ArgumentNames: argumentNames,
                // First-party gate: only first-party nodes are rendered, so a BCL callee's binding (List<int>
                // .Add) would be dead storage. C: concrete tokens still carry BCL type NAMES — that's fine,
                // they appear as the substituted args of a first-party generic.
                DeclaringTypeArgBinding: inSource ? declaringTypeArgBinding : null,
                MethodTypeArgBinding: inSource ? methodTypeArgBinding : null
            )
        );
    }

    // All positional arguments' string templates (literal/interpolated, via GetStringTemplate — the
    // same shape FirstArgumentOf captures for arg 0) and member/identifier name paths, index-aligned
    // with the call's argument list and each serialized as a JSON string?[]. JSON (not the
    // TypeArguments comma-join) because an argument string literal can itself contain commas, which a
    // top-level-comma split would mis-segment; the deriver reads back the index-th element over a
    // stack buffer (FactEffectDeriver.NthJsonString). Feeds nth-argument resource resolution
    // (FactEffectRule.ArgumentIndex). Returns (null, null) for non-invocation refs and zero-arg calls.
    private static (string? Templates, string? Names) ArgumentListOf(
        string refKind,
        InvocationExpressionSyntax? invocation,
        SemanticModel model
    )
    {
        if (refKind != RefKinds.Invocation || invocation is null)
        {
            return (null, null);
        }

        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count == 0)
        {
            return (null, null);
        }

        var templates = new string?[arguments.Count];
        var names = new string?[arguments.Count];
        for (var i = 0; i < arguments.Count; i++)
        {
            var expression = arguments[i].Expression;
            templates[i] = StringValueOf(expression, model);
            names[i] = expression is MemberAccessExpressionSyntax or IdentifierNameSyntax ? expression.ToString() : null;
        }

        return (JsonSerializer.Serialize(templates), JsonSerializer.Serialize(names));
    }

    // The argument's string VALUE for the string_argument resource: its inline string template
    // (literal/interpolated) when it has one, else its compile-time CONSTANT string value when the
    // argument is a `const string` reference. GetConstantValue folds const fields/locals and constant
    // expressions — covering the LLBLGen `const string connectionKeyString = "…"` connection key and
    // `Roles.* = "Patient.Create"` permission constants, which carry the real resource even though the
    // call site only names the constant. (static-readonly tables like ProcessNames are NOT compile-time
    // constants — handled separately when that surface is wired.) Confined to the nth-argument lists,
    // so the unindexed FirstArgumentTemplate fast path — and every existing derivation — is unchanged;
    // a new rule opts into const-resolved values via ArgumentIndex. Null when neither applies.
    private static string? StringValueOf(ExpressionSyntax expression, SemanticModel model)
    {
        var template = expression.GetStringTemplate();
        if (template is not null)
        {
            return template;
        }

        return model.GetConstantValue(expression) is { HasValue: true, Value: string constant } ? constant : null;
    }

    // Static type of an invocation's receiver: `a.Foo()` -> type of `a` (open-generic FQN).
    // Bare `Foo()` (implicit this) and other shapes return null — only explicit member-access
    // receivers carry a receiver-type fact.
    private static string? ReceiverTypeOf(SimpleNameSyntax name, SemanticModel model)
    {
        if (name.Parent is MemberAccessExpressionSyntax member && member.Name == name)
        {
            return model.GetTypeInfo(member.Expression).Type?.OriginalDefinition.ToDisplayString();
        }

        if (name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is ConditionalAccessExpressionSyntax conditional)
        {
            return model.GetTypeInfo(conditional.Expression).Type?.OriginalDefinition.ToDisplayString();
        }

        return null;
    }

    // Encodes a callee's generic type arguments at the call site into the RENDERING binding (see
    // ReferenceFact.DeclaringTypeArgBinding / MethodTypeArgBinding): a JSON string[] of per-position tokens,
    //   "C:<fqn>" concrete · "T:<ord>" enclosing-TYPE param · "M:<ord>" enclosing-METHOD param · "?" composite.
    // T:/M: tokens are emitted purely from each arg's TypeParameterKind + Ordinal — no symbol identity needed,
    // because the renderer resolves them against the PARENT node's declaring/method concretes (whose param
    // spaces are exactly the enclosing method's containing type + the enclosing method itself). Returns null
    // for a null/empty arg list (non-generic). A `Seq<T>`-style composite arg yields "?" (placeholder kept).
    private static string? GenericArgBinding(System.Collections.Immutable.ImmutableArray<ITypeSymbol>? args)
    {
        if (args is not { Length: > 0 } list)
        {
            return null;
        }

        var tokens = new string[list.Length];
        for (var i = 0; i < list.Length; i++)
        {
            tokens[i] = list[i] switch
            {
                ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Method } m => $"M:{m.Ordinal}",
                ITypeParameterSymbol t => $"T:{t.Ordinal}",
                var concrete when !HasTypeParameter(concrete) => $"C:{concrete.ToDisplayString()}",
                _ => "?",
            };
        }
        return JsonSerializer.Serialize(tokens);
    }

    // True when `type` is a type PARAMETER, or a constructed generic / array that contains one at any depth
    // (so `List<T>`, `Dictionary<string, T>`, `T[]` are all "still open"). Used to reject open-typed generic
    // receivers when capturing the concrete receiver type for rendering.
    private static bool HasTypeParameter(ITypeSymbol type)
    {
        if (type.TypeKind == TypeKind.TypeParameter)
        {
            return true;
        }

        if (type is IArrayTypeSymbol array)
        {
            return HasTypeParameter(array.ElementType);
        }

        if (type is INamedTypeSymbol named)
        {
            foreach (var arg in named.TypeArguments)
            {
                if (HasTypeParameter(arg))
                {
                    return true;
                }
            }
        }

        return false;
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
        if (refKind == RefKinds.Invocation)
        {
            return invocation?.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        }

        if (refKind == RefKinds.Ctor && IsAttributeName(name))
        {
            return name.FirstAncestorOrSelf<AttributeSyntax>()?.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        }

        return null;
    }

    // First-argument facts for the given argument expression: its string template (literal or
    // interpolated, via StringTemplateExtensions — the same helper the Roslyn EffectExtractor uses
    // for http_argument/string_argument) and its static type (open-generic FQN, for argument_type).
    // Returns (null, null) for a null argument.
    private static (string? Template, string? Type, string? Name) FirstArgumentOf(ExpressionSyntax? argument, SemanticModel model)
    {
        if (argument is null)
        {
            return (null, null, null);
        }

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
        {
            return default;
        }

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
            {
                break;
            }
        }

        var enclosing = new List<FactStructuralContext.EnclosingInvocation>();
        foreach (var ancestor in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var receiverText = memberAccess.Expression.ToString();
            var receiverType = model.GetTypeInfo(memberAccess.Expression).Type?.OriginalDefinition.ToDisplayString() ?? "";
            enclosing.Add(
                new FactStructuralContext.EnclosingInvocation(
                    ReceiverText: receiverText,
                    ReceiverType: receiverType,
                    MethodName: memberAccess.Name.Identifier.ValueText
                )
            );
        }

        var catchTypes = new List<string>();
        foreach (var tryStatement in invocation.Ancestors().OfType<TryStatementSyntax>())
        {
            foreach (var catchClause in tryStatement.Catches)
            {
                if (catchClause.Declaration is not null)
                {
                    catchTypes.Add(model.GetTypeInfo(catchClause.Declaration.Type).Type?.ToDisplayString() ?? "");
                }
            }
        }

        // Enclosing held-resource scopes (innermost-first): `using`/`lock` ancestors. A `using` carries
        // its resource type (the disposed object — a transaction, connection, …); a `lock` carries the
        // locked expression's type (or "" if unresolved). Feeds resource_span: a network/IO effect
        // nested in a transaction-using or a lock is held across that effect.
        var scopes = new List<FactStructuralContext.EnclosingScope>();
        foreach (var ancestor in invocation.Ancestors())
        {
            if (ancestor is LockStatementSyntax lockStmt)
            {
                scopes.Add(new FactStructuralContext.EnclosingScope(Kind: "lock", Type: TypeDisplayOf(lockStmt.Expression, model)));
            }
            else if (ancestor is UsingStatementSyntax usingStmt)
            {
                scopes.Add(new FactStructuralContext.EnclosingScope(Kind: "using", Type: UsingResourceType(usingStmt, model)));
            }
            else if (ancestor is LocalDeclarationStatementSyntax local && local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword))
            {
                scopes.Add(new FactStructuralContext.EnclosingScope(Kind: "using", Type: DeclarationType(local.Declaration, model)));
            }
        }

        return new StructuralContext(
            LoopKind: loopKind,
            LoopDetail: loopDetail,
            EnclosingInvocations: FactStructuralContext.EncodeInvocations(enclosing),
            CatchTypes: FactStructuralContext.EncodeList(catchTypes),
            EnclosingScopes: FactStructuralContext.EncodeScopes(scopes)
        );
    }

    // The resource type of a `using` statement: the declared variable's type for
    // `using (var x = expr)` / `using (Resource x = expr)`, or the expression's type for
    // `using (expr)`. Open-generic FQN; "" when unresolved.
    private static string UsingResourceType(UsingStatementSyntax usingStmt, SemanticModel model)
    {
        if (usingStmt.Declaration is { } declaration)
        {
            return DeclarationType(declaration, model);
        }

        if (usingStmt.Expression is { } expression)
        {
            return TypeDisplayOf(expression, model);
        }

        return "";
    }

    // The declared type of a variable declaration; for `var` Roslyn resolves the inferred type from
    // the declaration's type syntax, falling back to the first initializer's type. Open-generic FQN.
    private static string DeclarationType(VariableDeclarationSyntax declaration, SemanticModel model)
    {
        var type = model.GetTypeInfo(declaration.Type).Type;
        if (type is null or IErrorTypeSymbol && declaration.Variables.FirstOrDefault()?.Initializer?.Value is { } initializer)
        {
            type = model.GetTypeInfo(initializer).Type;
        }

        return type?.OriginalDefinition.ToDisplayString() ?? "";
    }

    private static string TypeDisplayOf(ExpressionSyntax expression, SemanticModel model) =>
        model.GetTypeInfo(expression).Type?.OriginalDefinition.ToDisplayString() ?? "";

    private readonly record struct StructuralContext(
        string? LoopKind,
        string? LoopDetail,
        string? EnclosingInvocations,
        string? CatchTypes,
        string? EnclosingScopes = null
    );

    // The InvocationExpressionSyntax this name is the invoked method of: `Foo(..)`, `a.Foo(..)`, or
    // `a?.Foo(..)`. Null otherwise (mirrors IsInvoked's shapes, plus the conditional-access form).
    private static InvocationExpressionSyntax? InvocationOf(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax direct && direct.Expression == name)
        {
            return direct;
        }

        if (
            name.Parent is MemberAccessExpressionSyntax member
            && member.Name == name
            && member.Parent is InvocationExpressionSyntax memberInvocation
            && memberInvocation.Expression == member
        )
        {
            return memberInvocation;
        }

        if (name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is InvocationExpressionSyntax conditionalInvocation)
        {
            return conditionalInvocation;
        }

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
        {
            return symbol?.OriginalDefinition.GetDocumentationCommentId();
        }

        return (method.ReducedFrom ?? method).OriginalDefinition.GetDocumentationCommentId();
    }

    // 18c: the delegate FIELD/PROPERTY/EVENT a method-group is assigned to (the bind SLOT), or null
    // when the method-group is not a delegate assignment (it's a call argument — 18b handoff — or
    // something else). Walks the same transparent wrappers as DelegateConsumerOf, but stops at an
    // assignment (`slot = handler` / `slot += handler`) or an initializer (`Action _h = handler;`);
    // an Argument/ArgumentList means it's an argument, not a bind, so it returns null.
    private static string? DelegateBindSlotOf(SimpleNameSyntax name, SemanticModel model)
    {
        foreach (var ancestor in name.Ancestors())
        {
            switch (ancestor)
            {
                case AssignmentExpressionSyntax assign:
                    return DelegateSlotDocId(model.GetSymbolInfo(assign.Left).Symbol);
                case EqualsValueClauseSyntax equals:
                    return equals.Parent switch
                    {
                        VariableDeclaratorSyntax v => DelegateSlotDocId(model.GetDeclaredSymbol(v)),
                        PropertyDeclarationSyntax p => DelegateSlotDocId(model.GetDeclaredSymbol(p)),
                        _ => null,
                    };
                case MemberAccessExpressionSyntax:
                case MemberBindingExpressionSyntax:
                case ConditionalAccessExpressionSyntax:
                case ParenthesizedExpressionSyntax:
                case CastExpressionSyntax:
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    // The DocID of a delegate-typed slot symbol (field/property of delegate type, or any event — events
    // are always delegate-typed), or null for any other symbol. The bind source the seam resolver keys on.
    private static string? DelegateSlotDocId(ISymbol? symbol) =>
        symbol switch
        {
            IFieldSymbol { Type.TypeKind: TypeKind.Delegate } field => field.GetDocumentationCommentId(),
            IPropertySymbol { Type.TypeKind: TypeKind.Delegate } prop => prop.GetDocumentationCommentId(),
            IEventSymbol e => e.GetDocumentationCommentId(),
            _ => null,
        };

    private static string? ClassifyReference(SimpleNameSyntax name, ISymbol target) =>
        // A name inside `nameof(...)` is a compile-time string, NOT a use of the symbol — never a call,
        // delegate bind, or data touch. Classify it as the benign, non-traversable NameOf kind BEFORE
        // the use-based switch so a `nameof(Method)` (e.g. in a static menu map) does NOT emit a
        // methodGroup call edge that path/callers would walk as a real call. Checked first so it wins
        // over the IMethodSymbol -> MethodGroup arm. Real method-group conversions (`Foo.Bar` passed as
        // a delegate) are NOT inside nameof, so they still fall through to MethodGroup below.
        IsNameOfArgument(name)
            ? RefKinds.NameOf
            : target switch
            {
                IMethodSymbol { MethodKind: MethodKind.Constructor } => RefKinds.Ctor,
                IMethodSymbol => IsInvoked(name) ? RefKinds.Invocation : RefKinds.MethodGroup,
                INamedTypeSymbol or ITypeParameterSymbol => IsAttributeName(name) ? RefKinds.AttributeUse : RefKinds.TypeUse,
                IPropertySymbol or IFieldSymbol => IsWriteTarget(name) ? RefKinds.Write : RefKinds.Read,
                IEventSymbol => RefKinds.Read,
                _ => null,
            };

    // True when this name is (an inner part of) the operand of a `nameof(...)` expression. `nameof` is a
    // contextual keyword, not a real method, so its invocation binds to NO symbol — we detect it by an
    // enclosing InvocationExpression whose callee is the bare identifier `nameof` that does not resolve to
    // a method symbol. Walking ancestors (not just the immediate parent) covers `nameof(A.B.Method)`,
    // where the Method name sits under MemberAccessExpressions inside the nameof argument.
    private static bool IsNameOfArgument(SimpleNameSyntax name)
    {
        foreach (var ancestor in name.Ancestors())
        {
            switch (ancestor)
            {
                // The nameof operand is a (possibly dotted) name/member-access wrapped in the single
                // Argument/ArgumentList of the call; keep climbing through those structural nodes.
                case SimpleNameSyntax:
                case MemberAccessExpressionSyntax:
                case ArgumentSyntax:
                case ArgumentListSyntax:
                    continue;
                // `nameof(<operand>)` — a `nameof`-shaped invocation whose callee is the contextual
                // identifier `nameof` (which does not bind to any user method). The argument we climbed
                // out of is exactly the operand, so this name is inside nameof.
                case InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } } invocation:
                    return invocation.ArgumentList.Arguments.Count == 1;
                // Anything else terminates the operand chain — not a nameof argument.
                default:
                    return false;
            }
        }

        return false;
    }

    // True when this name is the method being invoked (a.Foo() or Foo()), as opposed to a
    // method group passed as a delegate (the background-worker handoff case).
    private static bool IsInvoked(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax direct && direct.Expression == name)
        {
            return true;
        }

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
        SyntaxTree tree,
        IReadOnlyDictionary<SyntaxNode, string> lambdaIds
    )
    {
        var (reads, writes) = AccessShape(name);
        var getter = reads && property.GetMethod is { } g && HasAccessorBody(g) ? g : null;
        var setter = writes && property.SetMethod is { } s && HasAccessorBody(s) ? s : null;
        if (getter is null && setter is null)
        {
            return;
        }

        var enclosing = EnclosingSymbolId(name, model, lambdaIds);
        var receiver = ReceiverTypeOf(name, model);
        var structural = StructuralContextOf(name, model);
        if (getter is not null)
        {
            AddReference(
                references,
                getter,
                refKind: RefKinds.Invocation,
                enclosingId: enclosing,
                tree: tree,
                node: name,
                receiverType: receiver,
                structural: structural
            );
        }

        if (setter is not null)
        {
            AddReference(
                references,
                setter,
                refKind: RefKinds.Invocation,
                enclosingId: enclosing,
                tree: tree,
                node: name,
                receiverType: receiver,
                structural: structural
            );
        }
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
        {
            yield return getter;
        }

        if (property.SetMethod is { } setter)
        {
            yield return setter;
        }
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

    // The owning symbol of a usage site. Normally the nearest enclosing member (method/property/field),
    // BUT a node inside an ARGUMENT-passed lambda (18b) is owned by that lambda's synthetic symbol —
    // so the lambda body's calls/effects attach to the lambda (promotable to an async entry point by a
    // deferred dispatcher) instead of bleeding into the enclosing method. Walks ancestors-or-self,
    // innermost-first: the first arg-lambda in `lambdaIds` wins; lambdas NOT in the map (field/local
    // assignments — 18c) are transparent and fall through to the member, preserving prior behaviour.
    private static string? EnclosingSymbolId(SyntaxNode node, SemanticModel model, IReadOnlyDictionary<SyntaxNode, string> lambdaIds)
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
        {
            if (cur is AnonymousFunctionExpressionSyntax && lambdaIds.TryGetValue(cur, out var lambdaId))
            {
                return lambdaId;
            }

            // A node inside a bodied accessor (`get {…}`/`set {…}`/`init {…}`/`add`/`remove`, or
            // `get => …`) is owned by the ACCESSOR method (M:get_X/M:set_X) — the symbol the access-site
            // call edge targets and the graph node that is emitted — NOT the property (P:X), which is
            // never a call-graph node. Keying effects to the property orphaned them from reachability
            // (reaches/tree intersect call-graph method ids against effect enclosing ids).
            if (cur is AccessorDeclarationSyntax accessor)
            {
                return model.GetDeclaredSymbol(accessor)?.GetDocumentationCommentId();
            }

            if (cur is MemberDeclarationSyntax member)
            {
                if (member is BaseFieldDeclarationSyntax field)
                {
                    var first = field.Declaration.Variables.FirstOrDefault();
                    return first is null ? null : model.GetDeclaredSymbol(first)?.GetDocumentationCommentId();
                }

                // Expression-bodied property/indexer (`PersonRecord Person => PersonCache.New(…);`): the
                // body IS the getter's, so own it by the getter accessor (M:get_X) to match the node +
                // edge. Auto-property initializers (`{ get; } = Compute()`, no ExpressionBody) run in the
                // ctor — not an accessor node — so they fall through to the property id unchanged.
                ArrowExpressionClauseSyntax? expressionBody = member switch
                {
                    PropertyDeclarationSyntax p => p.ExpressionBody,
                    IndexerDeclarationSyntax ix => ix.ExpressionBody,
                    _ => null,
                };
                if (expressionBody is not null && model.GetDeclaredSymbol(member) is IPropertySymbol { GetMethod: { } getter })
                {
                    return getter.GetDocumentationCommentId();
                }

                return model.GetDeclaredSymbol(member)?.GetDocumentationCommentId();
            }
        }
        return null;
    }

    // 18b: assign a synthetic identity to every lambda passed as a call/ctor ARGUMENT, emit it as a
    // "lambda" SymbolFact + a methodGroup edge (enclosing -> lambda) carrying the DelegateConsumer (the
    // dispatcher it's handed to), and return the node->id map that re-roots the lambda body's facts.
    // A lambda that is NOT an argument (a `Func<> f = () => ..` field/local, a `+=` handler) gets no
    // identity here — LambdaConsumerOf returns null — and stays owned by its member (deferred to 18c).
    // Outer lambdas precede their nested children in document order, so a nested lambda's own edge
    // resolves its enclosing to the OUTER lambda (already in the map).
    private static Dictionary<SyntaxNode, string> CollectLambdaSymbols(
        SyntaxNode root,
        SemanticModel model,
        SyntaxTree tree,
        List<SymbolFact> symbols,
        List<ReferenceFact> references
    )
    {
        var ids = new Dictionary<SyntaxNode, string>();
        var ordinalByMember = new Dictionary<string, int>(StringComparer.Ordinal);
        var assembly = model.Compilation.AssemblyName ?? "";

        foreach (var lambda in root.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
        {
            var consumer = LambdaConsumerOf(lambda, model);
            if (consumer is null)
            {
                continue; // not an argument-passed lambda — no deferred identity
            }

            var member = lambda.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            var memberSymbol = member is null ? null : model.GetDeclaredSymbol(member);
            var memberId = memberSymbol?.GetDocumentationCommentId();
            if (memberId is null)
            {
                continue;
            }

            var ordinal = ordinalByMember.TryGetValue(memberId, out var n) ? n : 0;
            ordinalByMember[memberId] = ordinal + 1;
            var id = $"{memberId}~λ{ordinal}"; // λ marker: clearly synthetic, never collides with a real DocID
            ids[lambda] = id;

            var line = tree.GetLineSpan(lambda.Span).StartLinePosition.Line + 1;
            symbols.Add(
                new SymbolFact(
                    SymbolId: id,
                    Kind: "lambda",
                    Name: $"λ{ordinal}",
                    Namespace: memberSymbol?.ContainingNamespace?.ToDisplayString() ?? "",
                    ContainingSymbolId: memberId,
                    Modifiers: "",
                    TypeKind: "",
                    Signature: "lambda",
                    FilePath: tree.FilePath,
                    Line: line,
                    DefiningAssembly: assembly,
                    IsOverride: false
                )
            );
            references.Add(
                new ReferenceFact(
                    TargetSymbolId: id,
                    RefKind: RefKinds.MethodGroup,
                    EnclosingSymbolId: EnclosingSymbolId(lambda.Parent ?? lambda, model, ids),
                    TargetAssembly: assembly,
                    TargetInSource: true,
                    FilePath: tree.FilePath,
                    Line: line,
                    DelegateConsumer: consumer
                )
            );
        }
        return ids;
    }

    // The dispatcher a lambda is handed to: the enclosing invocation/constructor the lambda is an
    // ARGUMENT of (mirrors DelegateConsumerOf's transparent-wrapper walk). Null when the lambda is not
    // a call argument (assigned to a field/local, a return, a `+=`), which keeps those out of the
    // promotion population.
    private static string? LambdaConsumerOf(AnonymousFunctionExpressionSyntax lambda, SemanticModel model)
    {
        foreach (var ancestor in lambda.Ancestors())
        {
            switch (ancestor)
            {
                case InvocationExpressionSyntax invocation:
                    return ConsumerDocId(model.GetSymbolInfo(invocation).Symbol);
                case BaseObjectCreationExpressionSyntax creation:
                    return ConsumerDocId(model.GetSymbolInfo(creation).Symbol);
                case ArgumentSyntax:
                case ArgumentListSyntax:
                case ParenthesizedExpressionSyntax:
                case CastExpressionSyntax:
                    continue;
                default:
                    return null;
            }
        }
        return null;
    }

    private static string KindOf(ISymbol symbol) =>
        symbol switch
        {
            INamedTypeSymbol => SymbolKinds.Type,
            IMethodSymbol => SymbolKinds.Method,
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
        {
            parts.Add(access);
        }

        if (symbol.IsStatic)
        {
            parts.Add("static");
        }

        if (symbol.IsAbstract)
        {
            parts.Add("abstract");
        }

        if (symbol.IsSealed)
        {
            parts.Add("sealed");
        }

        if (symbol.IsVirtual)
        {
            parts.Add("virtual");
        }

        if (symbol.IsOverride)
        {
            parts.Add("override");
        }

        if (symbol is IMethodSymbol { IsAsync: true })
        {
            parts.Add("async");
        }

        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true })
        {
            parts.Add("readonly");
        }

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
