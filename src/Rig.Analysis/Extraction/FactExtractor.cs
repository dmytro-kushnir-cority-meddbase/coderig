using System.Collections.Immutable;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;
using Rig.Domain.Data;

namespace Rig.Analysis.Extraction;

// Stage-1 fact extraction (see docs/fact-layer-refactor.md). Rule-agnostic, resolved
// structural facts: declared symbols, references (find-all-references), and type-relation
// edges. Global identity is the DocumentationCommentId (DocID). Lambdas/locals get no
// global id (host-context only) — they are simply not emitted as symbols here.
internal static class FactExtractor
{
    public static FactExtractionResult Extract(SourceModel source, SymbolStringCache symbolCache)
    {
        var model = source.SemanticModel;
        var root = source.Root;
        var tree = source.Tree;

        // The full source text, materialized ONCE per tree. BodyHashOf slices node spans out of this
        // (cheap substring) instead of calling node.ToString() per symbol (which re-walks the green
        // subtree and allocates a fresh string every time) — the hot cost at ~2M symbols.
        var fileText = tree.GetText().ToString();

        var symbols = new List<SymbolFact>();
        var references = new List<ReferenceFact>();
        var relations = new List<TypeRelationFact>();
        var dispatch = new List<DispatchFact>();
        var dispatchSeen = new HashSet<(string, string, string)>();
        // Per-file memo for EnclosingSymbolId: enclosing node -> its owning DocID. Shared across the lambda
        // pass and every reference so a member's DocID is built once, not once per contained reference.
        var enclosingCache = new Dictionary<SyntaxNode, string?>();
        // Per-method CFG + control-dependence cache (branch-aware-effects). Built lazily the first time an
        // effect-bearing ref in a method asks for its guard set, then reused for every other ref in that
        // method — so each method's CFG is constructed ONCE (the cost-spike basis). null value = the body
        // had no buildable CFG (cached so we don't retry). Keyed by the method/accessor/ctor decl node.
        var cfgGuardCache = new Dictionary<SyntaxNode, (ControlFlowGraph Cfg, IReadOnlyList<ControlDependence.ControlGuard>[] Guards)?>();

        // --- Lambda identity (18b): a synthetic symbol + handoff edge for each argument-passed lambda,
        //     so EnclosingSymbolId can re-root the lambda body's facts onto the lambda. The map is built
        //     INCREMENTALLY by the single descendant walk below (no separate pre-pass): DescendantNodes()
        //     is pre-order, so a lambda node is always visited — and thus registered here — before any
        //     reference in its body asks EnclosingSymbolId to re-root to it, and before a NESTED lambda
        //     resolves its enclosing to this (outer, ancestor) one. EnclosingSymbolId only ever walks
        //     ancestors, which are always already visited, so one pass suffices. ---
        var lambdaIds = new Dictionary<SyntaxNode, string>();
        var lambdaOrdinalByMember = new Dictionary<string, int>(StringComparer.Ordinal);
        var assemblyName = model.Compilation.AssemblyName ?? "";

        // --- Declarations -> SymbolFact (+ TypeRelation for type base/interface edges, DispatchFact
        //     for exact member-level dispatch) ---
        void OnDeclaration(MemberDeclarationSyntax decl)
        {
            // Field/event declarations declare one symbol PER VARIABLE, so GetDeclaredSymbol(decl) on the
            // declaration node itself returns null (`int a, b;` has no single declared symbol). Handle them
            // FIRST — before the null gate below — resolving each variable declarator individually; otherwise
            // the null return swallows every class field, leaving only enum members (which ARE single-symbol
            // EnumMemberDeclarationSyntax) in the store and orphaning every `F:` write-ref from its symbol.
            if (decl is BaseFieldDeclarationSyntax fieldDecl)
            {
                foreach (var variable in fieldDecl.Declaration.Variables)
                {
                    if (model.GetDeclaredSymbol(variable) is { } fieldSymbol)
                    {
                        AddSymbol(symbols, fieldSymbol, tree, fileText, variable, symbolCache);
                    }
                }
                return;
            }

            var symbol = model.GetDeclaredSymbol(decl);
            if (symbol is null)
            {
                return;
            }

            var docId = symbolCache.DocId(symbol);
            if (docId is null)
            {
                return;
            }

            AddSymbol(symbols, symbol, tree, fileText, decl, symbolCache);

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

                    AddSymbol(symbols, accessor, tree, fileText, AccessorNode(accessor) ?? decl, symbolCache);
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
            // Capture the receiver for INVOCATIONS and METHOD GROUPS alike. A method group `x.M` (e.g.
            // `Retry(cert.Delete)`, `evt += handler.OnX`) binds a delegate to receiver `x`; recording `x`'s
            // type lets dispatch narrow the (deferred) call to `x`'s override instead of the full CHA fan,
            // same as an invocation. A static-class qualifier (`Type.M`) captures the declaring type too, but
            // it is INERT — a static method has no overrides, so dispatch never fans it. A bare implicit-`this`
            // method group gets null (it isn't an invocation, so the implicit-`this` arm doesn't fire) — a
            // minor, accepted gap.
            var receiverType = refKind is RefKinds.Invocation or RefKinds.MethodGroup ? ReceiverTypeOf(name, model, symbolCache) : null;
            var (firstArgTemplate, firstArgType, firstArgName) = FirstArgumentOf(
                FirstArgumentExpressionOf(name, refKind, invocation),
                model,
                symbolCache
            );
            var (argumentTemplates, argumentNames) = ArgumentListOf(refKind, invocation, model);
            // Structural context (enclosing loop / fan-out invocation / try-catch / held-resource scope)
            // feeds the stage-2 observation deriver. The walk root is the invocation node for a call, but a
            // STATIC-FIELD WRITE (`StaticType.Field = v`) also derives a shared_state:mutate effect (FR-1b)
            // and a static-field READ (`= StaticType.Field`) a shared_state:read effect (FR-1 read arm) —
            // both must carry the SAME observations as an invocation. A publish under Parallel.ForEach is the
            // highest-value WRITE shape; for the read leg the held-resource scope matters specifically: the
            // race_window hazard tiers a read-before-write pair DOWN to "verify isolation" only when BOTH the
            // read and the write are bracketed by a transaction (transaction_spans_effect), so the read ref
            // must walk its enclosing scopes too. So read AND write walk from their `name` node (its ancestors
            // include the enclosing loop / fan-out call / lock / using). Other ref kinds keep no structural
            // context (no effect consumes it).
            SyntaxNode? structuralRoot =
                refKind == RefKinds.Invocation ? invocation
                : refKind is RefKinds.Write or RefKinds.Read ? name
                : null;
            var structural = StructuralContextOf(structuralRoot, model, symbolCache);
            // Control-dependence guard set of this effect-bearing call-site within its method (CFG-derived,
            // frozen here — see branch-aware-effects). Same nodes that carry structural context.
            var enclosingGuards = structuralRoot is null ? null : EncodedGuardsFor(structuralRoot, model, cfgGuardCache);
            var delegateConsumer = refKind == RefKinds.MethodGroup ? DelegateConsumerOf(name, model) : null;
            // A `base.M(...)` call is NON-VIRTUAL (C# spec: CIL `call`, not `callvirt`): the instance
            // receiver is the `base` keyword, so it binds to exactly the base implementation and can never
            // dispatch to a sibling override. Detect it here (only for an invocation through a member access
            // whose receiver is `base`) so the traversal can keep it out of the override-dispatch fan.
            var nonVirtual =
                refKind == RefKinds.Invocation
                && name.Parent is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } baseMember
                && baseMember.Name == name;
            AddReference(
                references,
                target,
                refKind: refKind,
                enclosingId: EnclosingSymbolId(name, model, lambdaIds, enclosingCache),
                tree: tree,
                node: name,
                receiverType: receiverType,
                firstArgumentTemplate: firstArgTemplate,
                firstArgumentType: firstArgType,
                structural: structural,
                firstArgumentName: firstArgName,
                delegateConsumer: delegateConsumer,
                argumentTemplates: argumentTemplates,
                argumentNames: argumentNames,
                symbolCache: symbolCache,
                nonVirtual: nonVirtual,
                enclosingGuards: enclosingGuards
            );

            // 18c: a method-group ASSIGNED to a delegate field/property/event (not passed as an
            // argument — that's the 18b handoff) is a binding. Emit a delegate_bind dispatch fact
            // (slot -> bound target) so the seam resolver can resolve `slot()` to its target.
            if (refKind == RefKinds.MethodGroup && DelegateBindSlotOf(name, model) is { } slot)
            {
                var resolvedTarget = target is IMethodSymbol bound
                    ? (bound.ReducedFrom ?? bound).OriginalDefinition
                    : target.OriginalDefinition;
                if (symbolCache.DocId(resolvedTarget) is { } boundId && dispatchSeen.Add((slot, boundId, DispatchKinds.DelegateBind)))
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
                AddAccessorInvocations(references, propertyAccess, name, model, tree, lambdaIds, enclosingCache, symbolCache);
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
                    enclosingId: EnclosingSymbolId(creation, model, lambdaIds, enclosingCache),
                    tree: tree,
                    node: creation,
                    symbolCache: symbolCache
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
                    EnclosingSymbolId: EnclosingSymbolId(invocation, model, lambdaIds, enclosingCache),
                    TargetAssembly: assemblyName,
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
                enclosingId: EnclosingSymbolId(thrown, model, lambdaIds, enclosingCache),
                tree: tree,
                node: thrown,
                structural: StructuralContextOf(thrown, model, symbolCache),
                allowRuntime: true,
                symbolCache: symbolCache
            );
        }

        // Collected during the single descendant walk below, then lowered after — folding the lock pass
        // into this walk avoids a second full `root.DescendantNodes()` traversal (+ its ToArray).
        List<LockStatementSyntax>? lockStatements = null;

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case AnonymousFunctionExpressionSyntax lambda:
                    ProcessLambda(
                        lambda: lambda,
                        symbols: symbols,
                        references: references,
                        lambdaIds: lambdaIds,
                        ordinalByMember: lambdaOrdinalByMember,
                        assembly: assemblyName,
                        model: model,
                        tree: tree,
                        fileText: fileText,
                        enclosingCache: enclosingCache,
                        symbolCache: symbolCache
                    );
                    break;

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

                case LockStatementSyntax lockStmt:
                    (lockStatements ??= []).Add(lockStmt);
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
        if (lockStatements is not null)
        {
            AddLockStatementRefs(references, lockStatements, model, tree, lambdaIds, enclosingCache, symbolCache);
        }

        return new FactExtractionResult(symbols, references, relations, dispatch);
    }

    // Emit synthetic Monitor.Enter (acquire) and Monitor.Exit (release) invocation refs for every
    // `lock (x) {}` statement, resolving the real Monitor method symbols from the compilation so the
    // refs carry genuine DocIds (the same the lock rule's declaringTypes gate matches). The release is
    // pinned to the body's closing-brace line so the acquire/release straddle the locked body — the
    // lexical span the ordering work (transaction/lock-held-across-IO) will read.
    private static void AddLockStatementRefs(
        List<ReferenceFact> references,
        IReadOnlyList<LockStatementSyntax> locks,
        SemanticModel model,
        SyntaxTree tree,
        IReadOnlyDictionary<SyntaxNode, string> lambdaIds,
        Dictionary<SyntaxNode, string?> enclosingCache,
        SymbolStringCache symbolCache
    )
    {
        var monitor = model.Compilation.GetTypeByMetadataName("System.Threading.Monitor");
        var enter = monitor?.GetMembers("Enter").OfType<IMethodSymbol>().FirstOrDefault();
        var exit = monitor?.GetMembers("Exit").OfType<IMethodSymbol>().FirstOrDefault();
        if (enter is null || exit is null)
        {
            return; // no Monitor in this compilation's references — nothing to lower against.
        }

        foreach (var lockStmt in locks)
        {
            var enclosing = EnclosingSymbolId(lockStmt, model, lambdaIds, enclosingCache);
            var structural = StructuralContextOf(lockStmt, model, symbolCache);

            // acquire: at the `lock` keyword / locked expression. allowRuntime keeps the BCL ref.
            AddReference(
                references,
                enter,
                refKind: RefKinds.Invocation,
                enclosingId: enclosing,
                tree: tree,
                node: lockStmt.Expression,
                structural: structural,
                allowRuntime: true,
                symbolCache: symbolCache
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
                lineOverride: releaseLine,
                symbolCache: symbolCache
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

    private static void AddSymbol(
        List<SymbolFact> symbols,
        ISymbol symbol,
        SyntaxTree tree,
        string fileText,
        SyntaxNode node,
        SymbolStringCache symbolCache
    )
    {
        var docId = symbolCache.DocId(symbol);
        if (docId is null)
        {
            return;
        }

        var lineSpan = tree.GetLineSpan(node.Span);

        var typeKind = symbol is INamedTypeSymbol t
            ? t.TypeKind switch
            {
                TypeKind.Unknown => "unknown",
                TypeKind.Array => "array",
                TypeKind.Class => "class",
                TypeKind.Delegate => "delegate",
                TypeKind.Dynamic => "dynamic",
                TypeKind.Enum => "enum",
                TypeKind.Error => "error",
                TypeKind.Interface => "interface",
                TypeKind.Module => "module",
                TypeKind.Pointer => "pointer",
                TypeKind.Struct => "struct",
                TypeKind.TypeParameter => "typeparameter",
                TypeKind.Submission => "submission",
                TypeKind.FunctionPointer => "functionpointer",
                TypeKind.Extension => "extension",
                var other => other.ToString().ToLowerInvariant(),
            }
            : "";

        symbols.Add(
            new SymbolFact(
                SymbolId: docId,
                Kind: KindOf(symbol),
                Name: symbol.Name,
                Namespace: symbolCache.NamespaceDisplay(symbol.ContainingNamespace),
                ContainingSymbolId: symbolCache.DocId(symbol.ContainingSymbol),
                Modifiers: ModifiersOf(symbol, symbolCache),
                TypeKind: typeKind,
                Signature: symbol.ToDisplayString(),
                FilePath: tree.FilePath,
                Line: lineSpan.StartLinePosition.Line + 1,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                DefiningAssembly: symbol.ContainingAssembly?.Name ?? "",
                IsOverride: symbol.IsOverride,
                // The declaration's normalized text — so `rig impact` detects an IN-PLACE body edit (a changed
                // constant/literal that leaves call structure, and thus the reachable-set diff, untouched).
                BodyHash: BodyHashOf(fileText, node)
            )
        );
    }

    // A deterministic content hash of a declaration node's verbatim source span (whitespace/comments
    // included), stable across runs of the same source. node.Span slices the same characters node.ToString()
    // would return, but straight out of the cached file text — no green-tree re-walk, no substring alloc.
    // We SHA-256 the UTF-16 bytes in place (stack destination, no intermediate byte[]); the 64-bit hex PREFIX
    // (16 chars) is collision-safe for diffing two stores of the same codebase. "" for an empty span.
    //
    // NOTE: this hashes the chars' native-endian UTF-16 bytes rather than the previous UTF-8 transcode, so the
    // values differ from older stores — re-mine before comparing across versions (any FactExtractor change
    // already requires a re-index). Byte order is identical across every little-endian host (Windows/macOS/Linux
    // on x64 or ARM64 are all LE), so two stores mined on different machines still diff correctly; only a
    // big-endian host (effectively extinct, never targeted) would produce a different hash.
    private static string BodyHashOf(string fileText, SyntaxNode node)
    {
        var span = node.Span;
        if (span.IsEmpty)
        {
            return "";
        }

        Span<byte> hash = stackalloc byte[8];

        XxHash3.Hash(source: MemoryMarshal.AsBytes(fileText.AsSpan(start: span.Start, length: span.Length)), destination: hash);

        return Convert.ToHexStringLower(hash);
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
        string? argumentNames = null,
        SymbolStringCache? symbolCache = null,
        bool nonVirtual = false,
        string? enclosingGuards = null
    )
    {
        // Generic type arguments at the CALL SITE — read from the constructed `target` BEFORE
        // OriginalDefinition strips them below (e.g. `ask<PaymentGatewayResponse<T>>` → that type).
        var typeArguments = target is IMethodSymbol { TypeArguments.Length: > 0 } generic
            ? string.Join(',', generic.TypeArguments.Select(t => t.ToDisplayString()))
            : null;

        // For constructors, point the reference at the constructor's containing type's ctor DocID;
        // for everything else use the symbol's own DocID. Reduced extension methods resolve to the
        // original definition so the DocID matches the declaration.
        var resolved = target is IMethodSymbol method ? (method.ReducedFrom ?? method).OriginalDefinition : target.OriginalDefinition;
        var docId = symbolCache is not null ? symbolCache.DocId(resolved) : resolved.GetDocumentationCommentId();
        if (docId is null)
        {
            return;
        }

        var inSource = resolved.Locations.Any(loc => loc.IsInSource);
        var assembly = resolved.ContainingAssembly?.Name ?? "";

        // Generic monomorphization bindings (RENDERING only) — see ReferenceFact. The DECLARING binding is
        // the callee's containing-type instantiation at this site (receiver/qualifier for a call, the
        // constructed type for a ctor, the owning type for a property/field read — e.g. `pipeline.Enumerate`
        // where Enumerate is a `Func<…>` property on QueryPipeline<TRecord, TColumn>); the METHOD binding is
        // the callee's own type args. Each position is encoded C:/T:/M:/? so the renderer can resolve
        // forwarded params against the parent's binding. Computed ONLY for first-party (inSource) targets:
        // only first-party nodes render, so a BCL callee's binding is dead storage (stored as null below) —
        // and a generic BCL call (List<T>.Add, Dictionary<,>.TryGetValue) is the common case, so gating
        // this skips the GenericArgBinding JSON serialization that would otherwise be computed and discarded.
        string? declaringTypeArgBinding = null;
        string? methodTypeArgBinding = null;
        if (inSource)
        {
            var constructed = target as IMethodSymbol;
            var declaringContainer = constructed is not null
                ? (constructed.ReducedFrom ?? constructed).ContainingType
                : target.ContainingType;
            declaringTypeArgBinding = GenericArgBinding(declaringContainer?.TypeArguments);
            methodTypeArgBinding = GenericArgBinding(constructed?.TypeArguments);
        }

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
                // Already null for non-first-party targets (computed only when inSource above) — only
                // first-party nodes render, so a BCL callee's binding would be dead storage.
                DeclaringTypeArgBinding: declaringTypeArgBinding,
                MethodTypeArgBinding: methodTypeArgBinding,
                // True for a `base.M(...)` call — non-virtual (CIL `call`), binds to exactly the base
                // implementation. The traversal resolves it to its static callee only and keeps it out of
                // the override-dispatch fan. False for every ordinary call. (Detected by the caller.)
                NonVirtual: nonVirtual,
                // CFG-derived control-dependence guard set of this call-site within its method (null = must-run).
                EnclosingGuards: enclosingGuards
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
        var anyTemplate = false;
        var anyName = false;
        for (var i = 0; i < arguments.Count; i++)
        {
            var expression = arguments[i].Expression;
            var template = StringValueOf(expression, model);
            templates[i] = template;
            anyTemplate |= template is not null;

            var name = expression is MemberAccessExpressionSyntax or IdentifierNameSyntax ? expression.ToString() : null;
            names[i] = name;
            anyName |= name is not null;
        }

        // When NEITHER list captured anything (every arg is a numeric/other literal or a complex
        // expression — no string template, no member/identifier path) both arrays are all-null and carry
        // no information: NthJsonString returns null for every index over a null payload and a "[null,
        // null]" one alike. Skip the serialize (and the retained/stored strings) for that case. The two
        // lists stay index-aligned — both present or both absent — so a captured value in either keeps
        // the full positional pair (e.g. a literal arg surfaces a null hole in the names list).
        if (!anyTemplate && !anyName)
        {
            return (null, null);
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
    private static string? ReceiverTypeOf(SimpleNameSyntax name, SemanticModel model, SymbolStringCache symbolCache)
    {
        if (name.Parent is MemberAccessExpressionSyntax member && member.Name == name)
        {
            return symbolCache.TypeDisplay(model.GetTypeInfo(member.Expression).Type);
        }

        if (name.Parent is MemberBindingExpressionSyntax binding && binding.Parent is ConditionalAccessExpressionSyntax conditional)
        {
            return symbolCache.TypeDisplay(model.GetTypeInfo(conditional.Expression).Type);
        }

        // Bare `Foo()` — the receiver is the implicit `this` (C# spec: an instance method invoked with no
        // explicit receiver runs on `this`), whose static type is the type lexically containing the call.
        // Recording it lets dispatch narrow `this.VirtualMethod()` to the enclosing type's family instead of
        // the full CHA fan (e.g. AppointmentEntity.Cancel's bare `Save()` resolves to AppointmentEntity, not
        // all 114 EntityBase.Save overrides). Static calls / local functions / delegate invokes have no `this`
        // receiver, and an unresolved target (net48 error type) leaves it null — both fall through.
        if (
            name.Parent is InvocationExpressionSyntax invocation
            && invocation.Expression == name
            // The call target is an instance method (so it HAS a receiver) ...
            && model.GetSymbolInfo(name).Symbol is IMethodSymbol { IsStatic: false, MethodKind: MethodKind.Ordinary }
            // ... AND `this` actually exists here: the enclosing executable is non-static (instance method /
            // accessor / non-static lambda / instance field-initializer). In valid C# the first condition
            // implies the second, but checking it directly keeps us correct on error code and self-evident.
            && model.GetEnclosingSymbol(name.SpanStart) is { IsStatic: false } enclosing
            && enclosing.ContainingType is { } thisType
        )
        {
            return symbolCache.TypeDisplay(thisType);
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
    private static string? GenericArgBinding(ImmutableArray<ITypeSymbol>? args)
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
    private static (string? Template, string? Type, string? Name) FirstArgumentOf(
        ExpressionSyntax? argument,
        SemanticModel model,
        SymbolStringCache symbolCache
    )
    {
        if (argument is null)
        {
            return (null, null, null);
        }

        var template = argument.GetStringTemplate();
        var type = symbolCache.TypeDisplay(model.GetTypeInfo(argument).Type);
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
    // The FQN type of an enclosing-invocation receiver, for FQN-based structural-context matching (e.g.
    // parallel_fanout). An INSTANCE receiver (`x.M()`) resolves to the value's type; a STATIC-class receiver
    // (`Parallel.ForEach`, whose expression has no value type) resolves to the referenced type ITSELF — so a
    // rule matches the same FQN whether the call was written `Parallel.ForEach` or fully qualified as
    // `System.Threading.Tasks.Parallel.ForEach`. (Matching the syntactic receiver TEXT missed the qualified form.)
    private static string EnclosingReceiverType(ExpressionSyntax receiver, SemanticModel model, SymbolStringCache symbolCache)
    {
        if (model.GetTypeInfo(receiver).Type is { } valueType)
        {
            return symbolCache.TypeDisplay(valueType) ?? "";
        }

        if (model.GetSymbolInfo(receiver).Symbol is INamedTypeSymbol staticType)
        {
            return symbolCache.TypeDisplay(staticType) ?? "";
        }

        return "";
    }

    private static StructuralContext StructuralContextOf(SyntaxNode? invocation, SemanticModel model, SymbolStringCache symbolCache)
    {
        if (invocation is null)
        {
            return default;
        }

        // ONE ancestor walk feeds all four structural facts — nearest enclosing loop, enclosing
        // member-access invocations, caught exception types, and held-resource (using/lock) scopes —
        // instead of four separate `Ancestors()` enumerations (each of which re-walked to the root).
        // Ancestors() is innermost-first, so every list keeps the exact order the prior per-fact walks
        // produced. The three lists are allocated LAZILY: the common case (a call with no enclosing
        // loop/try/scope and no member-access invocation around it) allocates nothing here.
        string? loopKind = null;
        string? loopDetail = null;
        List<FactStructuralContext.EnclosingInvocation>? enclosing = null;
        List<string>? catchTypes = null;
        List<FactStructuralContext.EnclosingScope>? scopes = null;

        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                // Nearest enclosing loop only — first one found wins; later (outer) loops are ignored.
                case ForEachStatementSyntax forEach when loopKind is null:
                    loopKind = "foreach";
                    loopDetail = $"{forEach.Identifier.ValueText} in {forEach.Expression}";
                    break;
                case ForStatementSyntax when loopKind is null:
                    loopKind = "for";
                    loopDetail = "for";
                    break;
                case WhileStatementSyntax when loopKind is null:
                    loopKind = "while";
                    loopDetail = "while";
                    break;

                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax memberAccess }:
                    (enclosing ??= []).Add(
                        new FactStructuralContext.EnclosingInvocation(
                            ReceiverText: memberAccess.Expression.ToString(),
                            ReceiverType: EnclosingReceiverType(memberAccess.Expression, model, symbolCache),
                            MethodName: memberAccess.Name.Identifier.ValueText
                        )
                    );
                    break;

                case TryStatementSyntax tryStatement:
                    foreach (var catchClause in tryStatement.Catches)
                    {
                        if (catchClause.Declaration is not null)
                        {
                            (catchTypes ??= []).Add(model.GetTypeInfo(catchClause.Declaration.Type).Type?.ToDisplayString() ?? "");
                        }
                    }
                    break;

                // Held-resource scopes (innermost-first): a `using` carries its resource type (the
                // disposed object — a transaction, connection, …); a `lock` carries the locked
                // expression's type (or "" if unresolved). Feeds resource_span: a network/IO effect
                // nested in a transaction-using or a lock is held across that effect.
                case LockStatementSyntax lockStmt:
                    (scopes ??= []).Add(
                        new FactStructuralContext.EnclosingScope(Kind: "lock", Type: TypeDisplayOf(lockStmt.Expression, model, symbolCache))
                    );
                    break;
                case UsingStatementSyntax usingStmt:
                    (scopes ??= []).Add(
                        new FactStructuralContext.EnclosingScope(Kind: "using", Type: UsingResourceType(usingStmt, model, symbolCache))
                    );
                    break;
                case LocalDeclarationStatementSyntax local when local.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                    (scopes ??= []).Add(
                        new FactStructuralContext.EnclosingScope(
                            Kind: "using",
                            Type: DeclarationType(local.Declaration, model, symbolCache)
                        )
                    );
                    break;
            }
        }

        return new StructuralContext(
            LoopKind: loopKind,
            LoopDetail: loopDetail,
            EnclosingInvocations: enclosing is null ? null : FactStructuralContext.EncodeInvocations(enclosing),
            CatchTypes: catchTypes is null ? null : FactStructuralContext.EncodeList(catchTypes),
            EnclosingScopes: scopes is null ? null : FactStructuralContext.EncodeScopes(scopes)
        );
    }

    // The resource type of a `using` statement: the declared variable's type for
    // `using (var x = expr)` / `using (Resource x = expr)`, or the expression's type for
    // `using (expr)`. Open-generic FQN; "" when unresolved.
    private static string UsingResourceType(UsingStatementSyntax usingStmt, SemanticModel model, SymbolStringCache symbolCache)
    {
        if (usingStmt.Declaration is { } declaration)
        {
            return DeclarationType(declaration, model, symbolCache);
        }

        if (usingStmt.Expression is { } expression)
        {
            return TypeDisplayOf(expression, model, symbolCache);
        }

        return "";
    }

    // The declared type of a variable declaration; for `var` Roslyn resolves the inferred type from
    // the declaration's type syntax, falling back to the first initializer's type. Open-generic FQN.
    private static string DeclarationType(VariableDeclarationSyntax declaration, SemanticModel model, SymbolStringCache symbolCache)
    {
        var type = model.GetTypeInfo(declaration.Type).Type;
        if (type is null or IErrorTypeSymbol && declaration.Variables.FirstOrDefault()?.Initializer?.Value is { } initializer)
        {
            type = model.GetTypeInfo(initializer).Type;
        }

        return symbolCache.TypeDisplay(type) ?? "";
    }

    private static string TypeDisplayOf(ExpressionSyntax expression, SemanticModel model, SymbolStringCache symbolCache) =>
        symbolCache.TypeDisplay(model.GetTypeInfo(expression).Type) ?? "";

    private readonly record struct StructuralContext(
        string? LoopKind,
        string? LoopDetail,
        string? EnclosingInvocations,
        string? CatchTypes,
        string? EnclosingScopes = null
    );

    // The encoded control-dependence guard set of an effect-bearing call-site `node`, within its enclosing
    // method (branch-aware-effects; frozen at index). Returns null when the effect is unconditional
    // (must-run — empty guard set), when `node` isn't in the method's top-level CFG (e.g. it sits inside a
    // lambda body — a deliberate M3 follow-up; that ref carries no guards rather than wrong ones), or when
    // no CFG could be built. The per-extraction `cache` ensures each method's CFG is constructed ONCE.
    private static string? EncodedGuardsFor(
        SyntaxNode node,
        SemanticModel model,
        Dictionary<SyntaxNode, (ControlFlowGraph Cfg, IReadOnlyList<ControlDependence.ControlGuard>[] Guards)?> cache
    )
    {
        var owner = node.AncestorsAndSelf().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax);
        if (owner is null)
        {
            return null;
        }

        if (!cache.TryGetValue(owner, out var data))
        {
            data = BuildGuardData(owner, model);
            cache[owner] = data;
        }

        if (data is null)
        {
            return null;
        }

        var block = ControlDependence.BlockOf(data.Value.Cfg, node);
        if (block < 0 || block >= data.Value.Guards.Length)
        {
            return null; // not a node in this method's top-level CFG (lambda body, etc.)
        }

        var guards = data.Value.Guards[block];
        return guards.Count == 0 ? null : FactStructuralContext.EncodeGuards(guards.Select(g => (g.Predicate, g.WhenTrue)).ToList());
    }

    private static (ControlFlowGraph Cfg, IReadOnlyList<ControlDependence.ControlGuard>[] Guards)? BuildGuardData(
        SyntaxNode owner,
        SemanticModel model
    )
    {
        ControlFlowGraph? cfg = model.GetOperation(owner) switch
        {
            IMethodBodyOperation methodBody => ControlFlowGraph.Create(methodBody),
            IConstructorBodyOperation ctorBody => ControlFlowGraph.Create(ctorBody),
            IBlockOperation block => ControlFlowGraph.Create(block),
            _ => null,
        };

        return cfg is null ? null : (cfg, ControlDependence.ComputeGuards(cfg));
    }

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
        IReadOnlyDictionary<SyntaxNode, string> lambdaIds,
        Dictionary<SyntaxNode, string?> enclosingCache,
        SymbolStringCache symbolCache
    )
    {
        var (reads, writes) = AccessShape(name);
        var getter = reads && property.GetMethod is { } g && HasAccessorBody(g) ? g : null;
        var setter = writes && property.SetMethod is { } s && HasAccessorBody(s) ? s : null;
        if (getter is null && setter is null)
        {
            return;
        }

        var enclosing = EnclosingSymbolId(name, model, lambdaIds, enclosingCache);
        var receiver = ReceiverTypeOf(name, model, symbolCache);
        var structural = StructuralContextOf(name, model, symbolCache);
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
                structural: structural,
                symbolCache: symbolCache
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
                structural: structural,
                symbolCache: symbolCache
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
    // The enclosing node's owning DocID, MEMOIZED per enclosing node. Every reference inside a member
    // resolves to the same id, so without the cache a method with N reference sites pays N× GetDeclaredSymbol
    // + GetDocumentationCommentId (the latter rebuilds the full signature string each time) — the dominant
    // allocator in extract at ~1.7M references. The cache (one per source file) collapses that to ~one
    // resolution per declared method/accessor. Keyed by the enclosing node; the ancestor walk is allocation-
    // free and runs per call, but the expensive bind + string build happens once.
    private static string? EnclosingSymbolId(
        SyntaxNode node,
        SemanticModel model,
        IReadOnlyDictionary<SyntaxNode, string> lambdaIds,
        Dictionary<SyntaxNode, string?> cache
    )
    {
        for (var cur = node; cur is not null; cur = cur.Parent)
        {
            if (cur is AnonymousFunctionExpressionSyntax && lambdaIds.TryGetValue(cur, out var lambdaId))
            {
                return lambdaId;
            }

            if (cur is AccessorDeclarationSyntax or MemberDeclarationSyntax)
            {
                if (cache.TryGetValue(cur, out var cached))
                {
                    return cached;
                }

                var id = ComputeEnclosingId(cur, model);
                cache[cur] = id;
                return id;
            }
        }

        return null;
    }

    // The per-node enclosing-owner resolution, factored out of EnclosingSymbolId so it can be memoized.
    private static string? ComputeEnclosingId(SyntaxNode cur, SemanticModel model)
    {
        // A node inside a bodied accessor (`get {…}`/`set {…}`/`init {…}`/`add`/`remove`, or `get => …`)
        // is owned by the ACCESSOR method (M:get_X/M:set_X) — the symbol the access-site call edge targets
        // and the graph node that is emitted — NOT the property (P:X), which is never a call-graph node.
        // Keying effects to the property orphaned them from reachability (reaches/tree intersect call-graph
        // method ids against effect enclosing ids).
        if (cur is AccessorDeclarationSyntax accessor)
        {
            return model.GetDeclaredSymbol(accessor)?.GetDocumentationCommentId();
        }

        var member = (MemberDeclarationSyntax)cur;
        if (member is BaseFieldDeclarationSyntax field)
        {
            var first = field.Declaration.Variables.FirstOrDefault();
            return first is null ? null : model.GetDeclaredSymbol(first)?.GetDocumentationCommentId();
        }

        // Expression-bodied property/indexer (`PersonRecord Person => PersonCache.New(…);`): the body IS the
        // getter's, so own it by the getter accessor (M:get_X) to match the node + edge. Auto-property
        // initializers (`{ get; } = Compute()`, no ExpressionBody) run in the ctor — not an accessor node —
        // so they fall through to the property id unchanged.
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

    // 18b: assign a synthetic identity to ONE lambda passed as a call/ctor ARGUMENT, emit it as a
    // "lambda" SymbolFact + a methodGroup edge (enclosing -> lambda) carrying the DelegateConsumer (the
    // dispatcher it's handed to), and register the node->id mapping that re-roots the lambda body's facts.
    // A lambda that is NOT an argument (a `Func<> f = () => ..` field/local, a `+=` handler) gets no
    // identity here — LambdaConsumerOf returns null — and stays owned by its member (deferred to 18c).
    // Called from the single descendant walk in document (pre-order) order, so an OUTER lambda is always
    // registered before its NESTED children: a nested lambda's own edge resolves its enclosing to the
    // outer lambda (already in lambdaIds), and ordinals are assigned per member in source order.
    private static void ProcessLambda(
        AnonymousFunctionExpressionSyntax lambda,
        List<SymbolFact> symbols,
        List<ReferenceFact> references,
        Dictionary<SyntaxNode, string> lambdaIds,
        Dictionary<string, int> ordinalByMember,
        string assembly,
        SemanticModel model,
        SyntaxTree tree,
        string fileText,
        Dictionary<SyntaxNode, string?> enclosingCache,
        SymbolStringCache symbolCache
    )
    {
        var consumer = LambdaConsumerOf(lambda, model);
        if (consumer is null)
        {
            return; // not an argument-passed lambda — no deferred identity
        }

        var member = lambda.FirstAncestorOrSelf<MemberDeclarationSyntax>();
        var memberSymbol = member is null ? null : model.GetDeclaredSymbol(member);
        var memberId = symbolCache.DocId(memberSymbol);
        if (memberId is null)
        {
            return;
        }

        var ordinal = ordinalByMember.TryGetValue(memberId, out var n) ? n : 0;
        ordinalByMember[memberId] = ordinal + 1;
        var id = $"{memberId}~λ{ordinal}"; // λ marker: clearly synthetic, never collides with a real DocID
        lambdaIds[lambda] = id;

        var lineSpan = tree.GetLineSpan(lambda.Span);
        var line = lineSpan.StartLinePosition.Line + 1;
        symbols.Add(
            new SymbolFact(
                SymbolId: id,
                Kind: "lambda",
                Name: $"λ{ordinal}",
                Namespace: symbolCache.NamespaceDisplay(memberSymbol?.ContainingNamespace),
                ContainingSymbolId: memberId,
                Modifiers: "",
                TypeKind: "",
                Signature: "lambda",
                FilePath: tree.FilePath,
                Line: line,
                EndLine: lineSpan.EndLinePosition.Line + 1,
                DefiningAssembly: assembly,
                IsOverride: false,
                BodyHash: BodyHashOf(fileText, lambda)
            )
        );
        references.Add(
            new ReferenceFact(
                TargetSymbolId: id,
                RefKind: RefKinds.MethodGroup,
                EnclosingSymbolId: EnclosingSymbolId(lambda.Parent ?? lambda, model, lambdaIds, enclosingCache),
                TargetAssembly: assembly,
                TargetInSource: true,
                FilePath: tree.FilePath,
                Line: line,
                DelegateConsumer: consumer
            )
        );
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

    // The space-joined modifier string, memoized per (accessibility + flags) combo: the value is a pure
    // function of those inputs, so ModifierKey encodes them into one int and the cache shares one built
    // string across all symbols with that combo (one of only a few dozen) — skipping the per-symbol
    // List<string> + Join, and collapsing the retained-duplicate Modifiers strings on the fact set.
    private static string ModifiersOf(ISymbol symbol, SymbolStringCache symbolCache) =>
        symbolCache.Modifiers(key: ModifierKey(symbol), symbol: symbol, build: BuildModifiers);

    // Packs everything BuildModifiers reads into one int cache key: accessibility in the low bits, each
    // boolean modifier in its own bit. Two symbols with the same key produce the identical modifier string.
    private static int ModifierKey(ISymbol symbol)
    {
        var key = (int)symbol.DeclaredAccessibility; // 0..6, fits the low 3 bits
        if (symbol.IsStatic)
        {
            key |= 1 << 3;
        }
        if (symbol.IsAbstract)
        {
            key |= 1 << 4;
        }
        if (symbol.IsSealed)
        {
            key |= 1 << 5;
        }
        if (symbol.IsVirtual)
        {
            key |= 1 << 6;
        }
        if (symbol.IsOverride)
        {
            key |= 1 << 7;
        }
        if (symbol is IMethodSymbol { IsAsync: true })
        {
            key |= 1 << 8;
        }
        if (symbol is IFieldSymbol { IsReadOnly: true } or IPropertySymbol { IsReadOnly: true })
        {
            key |= 1 << 9;
        }
        return key;
    }

    private static string BuildModifiers(ISymbol symbol)
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
