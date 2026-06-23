using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;

namespace Rig.Analysis.Extraction;

// Memoizes the EXPENSIVE Roslyn lazy-tree string projection that fact extraction asks for over and over:
// GetDocumentationCommentId() walks the symbol's structure and builds a string via StringBuilder on EVERY
// call, and the same symbol is the target of many references (2.1M reference sites on MedDBase resolve to
// a few hundred-thousand distinct symbols). Without memoization we recompute the same DocID thousands of
// times AND retain a fresh duplicate string for each — so caching by symbol is a CPU win on the hot extract
// path (compute each DocID once) AND a peak-memory win on the retained fact strings (one shared instance).
//
// Shared across the whole run's parallel per-file extraction, hence concurrent. Keyed by
// SymbolEqualityComparer.Default, which ignores exactly what a DocID ignores (nullability/tuple names), so
// two symbols that compare equal always produce the same DocID — sharing one entry is correctness-safe.
//
// The GENERAL ToDisplayString is deliberately NOT cached here: it CAN differ between symbols that compare
// equal under the comparer — e.g. tuple element names, which the comparer ignores but the display keeps —
// so a symbol-keyed cache would be unsound for type/signature display. NamespaceDisplay below is the sound
// EXCEPTION: a namespace's display name carries neither nullability nor tuple element names (the only two
// things SymbolEqualityComparer.Default ignores), so two namespace symbols that compare equal always
// produce the same display string.
internal sealed class SymbolStringCache
{
    private readonly ConcurrentDictionary<ISymbol, string?> _docIds = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<INamespaceSymbol, string> _namespaceDisplays = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<ITypeSymbol, string> _typeDisplays = new(SymbolEqualityComparer.Default);
    private readonly ConcurrentDictionary<int, string> _modifiers = new();

    public string? DocId(ISymbol? symbol) => symbol is null ? null : _docIds.GetOrAdd(symbol, static s => s.GetDocumentationCommentId());

    // The dotted display name of a namespace ("" for a null namespace, matching the prior `?? ""`),
    // memoized. ContainingNamespace.ToDisplayString() is called once per DECLARED symbol, but the
    // namespace is SHARED across every member declared in it — so on a namespace with N members it is N
    // visitor-walks producing N duplicate identical strings for one value. ToDisplayString never caches
    // (it rents an ArrayBuilder<SymbolDisplayPart>, runs the display visitor, and allocates a fresh
    // string on every call — Roslyn 5.3 SymbolDisplay). Caching by the namespace symbol turns that into
    // one walk + one retained instance per distinct namespace: a CPU win AND a peak-memory win, since the
    // Namespace string is stored on every SymbolFact (the retained duplicates collapse to one each).
    public string NamespaceDisplay(INamespaceSymbol? ns) =>
        ns is null ? "" : _namespaceDisplays.GetOrAdd(ns, static n => n.ToDisplayString());

    // The open-generic display FQN of a type's ORIGINAL DEFINITION — the `.OriginalDefinition.ToDisplayString()`
    // projection every use site (receiver/argument/scope type) asks for — memoized. The same type is the
    // receiver/argument at thousands of call sites and ToDisplayString never caches, so this is a CPU win
    // and, since ReceiverType/FirstArgumentType/etc. are stored on the fact set, a retained-duplicate
    // collapse. Keyed by the ORIGINAL DEFINITION: SOUND because OriginalDefinition strips construction
    // (tuple element names) and the default display format omits nullability — the only two things
    // SymbolEqualityComparer.Default ignores — so two types equal under the comparer always yield the same
    // string. The CONSTRUCTED display (which keeps tuple names + type args) is NOT routed here — that is the
    // unsound case the general-ToDisplayString note above describes. Null in -> null out (matches the prior
    // `?.OriginalDefinition.ToDisplayString()`); callers that want "" apply their own `?? ""`.
    public string? TypeDisplay(ITypeSymbol? type)
    {
        if (type is null)
        {
            return null;
        }

        return _typeDisplays.GetOrAdd(type.OriginalDefinition, static t => t.ToDisplayString());
    }

    // Memoizes the space-joined modifier string by a `key` that fully encodes the inputs the string is
    // built from (accessibility + the static/abstract/sealed/virtual/override/async/readonly flags). The
    // string is a PURE function of that key, so sharing one entry is sound. ModifiersOf is called once per
    // declared symbol (~hundreds of thousands), each building a throwaway List<string> + string.Join for
    // one of only a few dozen distinct combos; this collapses that to one build + one retained instance per
    // combo (Modifiers is stored on every SymbolFact). `build` is passed as a static method group so the
    // GetOrAdd factory allocates no closure; `symbol` rides through the TArg overload, not a capture.
    public string Modifiers(int key, ISymbol symbol, Func<ISymbol, string> build) =>
        _modifiers.GetOrAdd(key, static (_, state) => state.build(state.symbol), (symbol, build));
}
