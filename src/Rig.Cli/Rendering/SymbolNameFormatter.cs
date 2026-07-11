using System.Text;
using System.Text.Json;
using static System.Globalization.CultureInfo;

namespace Rig.Cli.Rendering;

// Pure DocID -> human display formatting: short names, parameter signatures, generic-arity rendering,
// path-contextual monomorphization, and small string tidiers. No dependencies — every method is a pure
// function of its arguments — so it is shared by every renderer (TreeRenderer, the query commands, the
// entry-point listing) without coupling them.
internal static class SymbolNameFormatter
{
    internal static string ShortName(string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId))
        {
            return "(top-level)";
        }

        var s = symbolId;
        // EF projections surface as Roslyn's full anonymous-type display ("<anonymous type: T1 M1, ...>").
        // Collapse the member list — the enclosing method already identifies the projection. Display-only;
        // the facts keep the full string (so --format tsv and effect-identity stay verbatim).
        if (s.StartsWith("<anonymous type:", StringComparison.Ordinal))
        {
            return "<anon>";
        }

        var paren = s.IndexOf('(');
        if (paren >= 0)
        {
            s = s.Substring(startIndex: 0, length: paren);
        }

        // Take the last two namespace segments, scanning for TOP-LEVEL dots only: a constructed-generic
        // DocID renders type args in braces (`Foo{System.Int32}`) whose dots would otherwise mis-split the
        // name into garbage like "Int32}}.New". Skip dots nested inside {}/<>/()/[] by tracking depth.
        var lastDot = TopLevelLastDot(s, s.Length);
        var prevDot = lastDot > 0 ? TopLevelLastDot(s, lastDot) : -1;
        return prevDot >= 0 ? s.Substring(prevDot + 1) : s;
    }

    // A DocID reduced to its queryable, fully-qualified dotted name: the leading `M:` kind prefix and the
    // parameter list are stripped, leaving `Namespace.Type.Member`. This is the exact suffix `rig tree` /
    // `reaches` / `callers` match on, so a rendered FQN round-trips straight back into a query — unlike the
    // slash-form EP `Route`, which matches nothing. Empty in, empty out. (Same reduction as
    // ImpactEngine.StripParams, which now delegates here so the EP card and the EP listings agree.)
    internal static string FqnFromDocId(string? docId)
    {
        if (string.IsNullOrEmpty(docId))
        {
            return "";
        }

        var body = docId.StartsWith("M:", StringComparison.Ordinal) ? docId[2..] : docId;
        var paren = body.IndexOf('(', StringComparison.Ordinal);
        return paren >= 0 ? body[..paren] : body;
    }

    // The index of the last '.' at bracket-depth 0 strictly before `end` (scanning backward), or -1.
    // Dots inside generic-arg braces/angles/parens/brackets are skipped so a namespaced type ARGUMENT
    // (e.g. `System.Int32` in `Foo{System.Int32}`) never mis-splits the enclosing name.
    internal static int TopLevelLastDot(string s, int end)
    {
        var depth = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            var c = s[i];
            if (c is '}' or '>' or ')' or ']')
            {
                depth++;
            }
            else if (c is '{' or '<' or '(' or '[')
            {
                depth--;
            }
            else if (c == '.' && depth == 0)
            {
                return i;
            }
        }
        return -1;
    }

    // Compact parameter signature for `rig tree --signatures`, so same-named OVERLOADS (e.g. the four
    // SiteCache.New / three Site.New) are distinguishable: "(Int32)", "(SiteId, ITransaction)", "()".
    // Each parameter type is reduced to its simple name (namespace stripped); generic/array suffixes are
    // left as-is. Empty string when the DocID carries no parameter list (so a bare member stays bare).
    internal static string ShortSignature(string? symbolId)
    {
        if (string.IsNullOrEmpty(symbolId))
        {
            return "";
        }

        var s = symbolId!;
        var open = s.IndexOf('(');
        if (open < 0)
        {
            return "";
        }

        var close = s.LastIndexOf(')');
        if (close <= open)
        {
            return "";
        }

        var inner = s.Substring(startIndex: open + 1, length: close - open - 1);
        if (inner.Length == 0)
        {
            return "()";
        }

        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i <= inner.Length; i++)
        {
            if (i < inner.Length)
            {
                var c = inner[i];
                if (c is '{' or '[' or '(' or '<')
                {
                    depth++;
                }
                else if (c is '}' or ']' or ')' or '>')
                {
                    depth--;
                }

                if (!(c == ',' && depth == 0))
                    continue;
            }
            parts.Add(SimplifyParamType(inner.Substring(start, i - start)));
            start = i + 1;
        }
        return "(" + string.Join(", ", parts) + ")";
    }

    // Strips the namespace from EVERY type token in a parameter type (not just the outer one) and shows
    // generics in C# angle-bracket form, so the rendering is consistent at any nesting depth:
    // "System.Int32" -> "Int32", "SD.…ORMSupportClasses.ITransaction" -> "ITransaction",
    // "System.Collections.Generic.Dictionary{System.String,System.Object}" -> "Dictionary<String, Object>",
    // "System.Nullable{MedDBase.SiteId}[]" -> "Nullable<SiteId>[]". Tokenizes on dotted-identifier runs;
    // every other char (braces->angles, brackets, commas, ref/out '@', '*') is structure and preserved.
    internal static string SimplifyParamType(string param)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < param.Length)
        {
            var c = param[i];
            if (c == '`')
            {
                // A type-parameter REFERENCE: ``N (method type param) or `N (containing-type param).
                // Render the positional placeholder T/U/V… (the real name isn't in the doc id).
                i++;
                if (i < param.Length && param[i] == '`')
                {
                    i++;
                }

                var ds = i;
                while (i < param.Length && char.IsDigit(param[i]))
                {
                    i++;
                }

                sb.Append(int.TryParse(param.Substring(ds, i - ds), InvariantCulture, out var idx) ? TypeParamName(idx) : "T");
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '.')
            {
                var start = i;
                while (i < param.Length && (char.IsLetterOrDigit(param[i]) || param[i] is '_' or '.'))
                {
                    i++;
                }

                var token = param.Substring(start, i - start);
                var dot = token.LastIndexOf('.');
                sb.Append(dot >= 0 ? token.Substring(dot + 1) : token);
            }
            else
            {
                // Doc-id generic args use {}; render as C# <> with ", " between args for readability.
                sb.Append(
                    c == '{' ? '<'
                    : c == '}' ? '>'
                    : c
                );
                if (c == ',')
                {
                    sb.Append(' ');
                }

                i++;
            }
        }
        return sb.ToString().Trim();
    }

    // Positional generic type-parameter placeholder (the real name isn't in the doc id): 0->T, 1->U,
    // 2->V, … then T7, T8 beyond the single-letter run.
    internal static string TypeParamName(int index) => index is >= 0 and < 7 ? "TUVWXYZ"[index].ToString() : "T" + index;

    // Replaces XML-doc-id generic-ARITY markers in a NAME with readable placeholders, so a node reads
    // like C#: "Cache`2.GetResults" -> "Cache<T, U>.GetResults",
    // "CheckAllExternalApplications``1" -> "CheckAllExternalApplications<T>". A bare name is returned
    // unchanged. (Parameter type-param REFERENCES are handled in SimplifyParamType.)
    // DocID-form name -> readable display. Two generic shapes:
    //   * OPEN generic backtick arity `N / ``N  ->  <T, U, …>  (placeholder type params),
    //   * CONSTRUCTED generic braces  {Ns.A, Ns.B{Ns.C}}  ->  <A, B<C>>  (actual args, namespaces
    //     stripped, nested handled) — so a LanguageExt `NewType{MedDBase.ChamberId,System.Int32,…}.New`
    //     reads as `NewType<ChamberId, Int32, …>.New` instead of a wall of fully-qualified braces.
    // Type-arg simple-naming applies only INSIDE the braces (depth>0); the outer name (already shortened
    // by ShortName) and the trailing `.Member` keep their dots.
    internal static string PrettyGenericName(string name) => PrettyGenericName(name, declaringArgs: null, methodArgs: null);

    // `declaringArgs` / `methodArgs` (when non-null) are the ordered, namespace-stripped concrete type
    // arguments the node ran under — resolved from the generic monomorphization bindings (see
    // ResolveNodeInstantiation). They substitute the label's two arity expansions: the declaring type's
    // `N (declaringArgs) and a generic method's own ``M (methodArgs) — e.g. `QueryPipeline<Account, Invoice>
    // .Create<Entity, Account>` instead of `QueryPipeline<T, U>.Create<T, U>`. A null list or per-position
    // null keeps that slot's placeholder. The constructed-brace form ({Ns.A,Ns.B}) is already concrete and
    // never reaches the arity branch, so it is unaffected.
    internal static string PrettyGenericName(string name, IReadOnlyList<string?>? declaringArgs, IReadOnlyList<string?>? methodArgs)
    {
        if (name.IndexOf('`') < 0 && name.IndexOf('{') < 0)
        {
            return name;
        }

        var sb = new StringBuilder();
        var token = new StringBuilder();
        var depth = 0;
        // Arity expansions appear in order: the FIRST is the declaring type's `N (substitute declaringArgs),
        // the SECOND is a generic method's own ``M (substitute methodArgs). A per-position null arg keeps
        // that slot's placeholder (T/U/V), so a partial binding still resolves the positions it knows.
        var arityGroup = 0;

        void FlushToken()
        {
            if (token.Length == 0)
            {
                return;
            }

            var t = token.ToString();
            token.Clear();
            // Inside a generic-arg list, drop the namespace of a type token (Ns.Ns.Type -> Type).
            if (depth > 0)
            {
                var lastDot = t.LastIndexOf('.');
                if (lastDot >= 0)
                {
                    t = t.Substring(lastDot + 1);
                }
            }
            sb.Append(t);
        }

        var i = 0;
        while (i < name.Length)
        {
            var c = name[i];
            switch (c)
            {
                case '`':
                    // `N after a NAME ("Foo`2") is ARITY -> <T, U>. A STANDALONE `N (token buffer empty,
                    // i.e. an argument position like the `0,`1 in "QueryPipeline{`0,`1}") is a positional
                    // type-PARAMETER reference -> that one param (T/U/V), NOT an arity count.
                    var isArity = token.Length > 0;
                    FlushToken();
                    i++;
                    if (i < name.Length && name[i] == '`')
                    {
                        i++;
                    }

                    var ds = i;
                    while (i < name.Length && char.IsDigit(name[i]))
                    {
                        i++;
                    }

                    if (int.TryParse(name.Substring(ds, i - ds), InvariantCulture, out var n))
                    {
                        if (!isArity)
                        {
                            sb.Append(TypeParamName(n)); // `0 -> T, `1 -> U
                        }
                        else if (n > 0)
                        {
                            // First arity group = the declaring type's, second = a generic method's own.
                            var args =
                                arityGroup == 0 ? declaringArgs
                                : arityGroup == 1 ? methodArgs
                                : null;
                            arityGroup++;
                            var usable = args is not null && args.Count == n;
                            sb.Append('<')
                                .Append(
                                    string.Join(
                                        ", ",
                                        Enumerable.Range(start: 0, count: n).Select(k => usable && args![k] is { } v ? v : TypeParamName(k))
                                    )
                                )
                                .Append('>');
                        }
                    }
                    break;
                case '{':
                    FlushToken();
                    sb.Append('<');
                    depth++;
                    i++;
                    break;
                case '}':
                    FlushToken();
                    sb.Append('>');
                    depth = Math.Max(val1: 0, val2: depth - 1);
                    i++;
                    break;
                case ',':
                    FlushToken();
                    sb.Append(", ");
                    i++;
                    if (i < name.Length && name[i] == ' ')
                    {
                        i++; // collapse an existing ", " so spacing stays single
                    }

                    break;
                default:
                    token.Append(c);
                    i++;
                    break;
            }
        }
        FlushToken();
        return sb.ToString();
    }

    // Path-contextual monomorphization: resolve this node's concrete instantiation — (declaring-type args,
    // own-method args) — from its mined bindings against the PARENT node's resolved instantiation. Each
    // binding is a JSON string[] of C:/T:/M:/? tokens (see ReferenceFact). A "C:" token is the concrete type
    // (namespace-stripped); a "T:n"/"M:n" token forwards the parent's n-th declaring/method concrete; "?" or
    // an out-of-range/missing forward yields null (that position keeps its placeholder). Returns (null, null)
    // for non-generic callees. The arrays it returns become the children's parent instantiation.
    internal static (IReadOnlyList<string?>? Declaring, IReadOnlyList<string?>? Method) ResolveNodeInstantiation(
        string? declaringBinding,
        string? methodBinding,
        IReadOnlyList<string?>? parentDeclaring,
        IReadOnlyList<string?>? parentMethod
    ) =>
        (
            ResolveBindingTokens(declaringBinding, parentDeclaring, parentMethod),
            ResolveBindingTokens(methodBinding, parentDeclaring, parentMethod)
        );

    internal static IReadOnlyList<string?>? ResolveBindingTokens(
        string? bindingJson,
        IReadOnlyList<string?>? parentDeclaring,
        IReadOnlyList<string?>? parentMethod
    )
    {
        if (string.IsNullOrEmpty(bindingJson))
        {
            return null;
        }

        string[]? tokens;
        try
        {
            tokens = JsonSerializer.Deserialize<string[]>(bindingJson!);
        }
        catch (JsonException)
        {
            return null;
        }
        if (tokens is null || tokens.Length == 0)
        {
            return null;
        }

        static string? Forward(IReadOnlyList<string?>? parent, string ordinalText) =>
            int.TryParse(ordinalText, InvariantCulture, out var ord) && parent is not null && ord >= 0 && ord < parent.Count
                ? parent[ord]
                : null;

        var resolved = new string?[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            var tok = tokens[i];
            resolved[i] =
                tok.Length < 2
                    ? null
                    : tok[0] switch
                    {
                        'C' => StripTypeNamespaces(tok.Substring(2)),
                        'T' => Forward(parentDeclaring, tok.Substring(2)),
                        'M' => Forward(parentMethod, tok.Substring(2)),
                        _ => null,
                    };
        }
        return resolved;
    }

    // Strips the namespace from every dotted type token in a C#-display type name, preserving generic
    // structure: "Ns.Foo<Ns.A, Other.B>" -> "Foo<A, B>", "System.Int32" -> "Int32".
    internal static string StripTypeNamespaces(string type)
    {
        var sb = new StringBuilder(type.Length);
        var i = 0;
        while (i < type.Length)
        {
            var c = type[i];
            if (char.IsLetterOrDigit(c) || c is '_' or '.')
            {
                var start = i;
                while (i < type.Length && (char.IsLetterOrDigit(type[i]) || type[i] is '_' or '.'))
                {
                    i++;
                }

                var token = type.Substring(startIndex: start, length: i - start);
                var dot = token.LastIndexOf('.');
                sb.Append(dot >= 0 ? token.Substring(dot + 1) : token);
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }
        return sb.ToString();
    }

    // Loop detail (e.g. a foreach's "{ident} in {expr}") can be long/multi-line (LINQ predicates),
    // so collapse whitespace and truncate for single-line trace output.
    internal static string ShortLoop(string? detail)
    {
        if (string.IsNullOrEmpty(detail))
        {
            return "?";
        }

        var s = string.Join(' ', detail!.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= 60 ? s : s.Substring(startIndex: 0, length: 57) + "...";
    }

    internal static string ShortenPath(string path)
    {
        var parts = path.Replace(oldChar: '\\', newChar: '/').Split('/');
        return parts.Length <= 3 ? path : string.Join('/', parts[^3..]);
    }
}
