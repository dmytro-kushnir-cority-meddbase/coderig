using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace MMS.Tools.RequestResponseProxyProjectBuilder.Roslyn;

public class Project
{
    private static readonly SymbolDisplayFormat FullyQualifiedWithoutGlobalFormat = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters
    );

    private static readonly SymbolDisplayFormat FullyQualifiedWithoutGlobalFormatNullableFull = new SymbolDisplayFormat(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.ExpandNullable
    );

    private readonly Dictionary<string, Stack<string>> tags = new();
    private INamedTypeSymbol currentType;
    private IMethodSymbol currentCtor;
    private IEventSymbol currentEvent;
    private IMethodSymbol currentAction;

    public string GenerateProxy(INamedTypeSymbol typeSymbol, string template)
    {
        currentType = typeSymbol;

        string fqName = typeSymbol.ToDisplayString();
        string ns = typeSymbol.ContainingNamespace.ToDisplayString();
        string className = typeSymbol.Name;
        string fqSlashed = ns.StartsWith("MedDBase.Pages") ? ns.Substring("MedDBase.Pages".Length).TrimStart('.') : ns;
        fqSlashed = fqSlashed.Replace('.', '/');

        Push("<[Namespace]>", ns);
        Push("<[ClassName]>", className);
        Push("<[FqClassName]>", fqName);
        Push("<[NamespaceSlashed]>", fqSlashed);
        Push("<[Interfaces]>", ExtractInterfaces(typeSymbol));

        var output = ReplaceMarkup(template);

        output = ReplaceForEach(output, "ForEachAction", IterateActions);
        output = ReplaceForEach(output, "ForEachEvent", IterateEvents);
        output = ReplaceForEach(output, "ForEachCtor", IterateCtors);

        Pop("<[Namespace]>");
        Pop("<[ClassName]>");
        Pop("<[FqClassName]>");
        Pop("<[NamespaceSlashed]>");
        Pop("<[Interfaces]>");

        return output;
    }

    private string ReplaceForEach(string template, string tag, Func<string, string> callback)
    {
        var sb = new StringBuilder();
        int pos = 0;
        while (true)
        {
            int startTag = template.IndexOf($"<[{tag}]>", pos, StringComparison.Ordinal);
            if (startTag == -1)
            {
                sb.Append(template.Substring(pos));
                break;
            }

            sb.Append(template.Substring(pos, startTag - pos));
            int endTag = template.IndexOf($"</[{tag}]>", startTag, StringComparison.Ordinal);
            if (endTag == -1)
                throw new Exception($"Missing </[{tag}]>");

            int contentStart = startTag + tag.Length + 4;
            string inner = template.Substring(contentStart, endTag - contentStart);

            sb.Append(callback(inner));
            pos = endTag + tag.Length + 5;
        }
        return sb.ToString();
    }

    private string IterateEvents(string code)
    {
        var sb = new StringBuilder();

        var items = GetDeclaredAndInheritedNonOverriddenEvents(currentType).OrderBy(evt => evt.Name).ToArray();

        int count = items.Length;
        int index = 0;

        foreach (var item in items)
        {
            Push("<[EventName]>", item.Name);
            Push("<[EventType]>", item.Type.ToDisplayString());
            Push("<[Index]>", index.ToString());

            Push("<[CommaIfNotLast]>", index < count - 1 ? "," : "");
            Push("<[AmpersandIfNotLast]>", index < count - 1 ? "&" : "");
            Push("<[CommaOnFirst]>", index == 0 ? "," : "");

            currentEvent = item;
            sb.Append(ReplaceMarkup(ReplaceForEach(code, "ForEachEventParam", IterateEventParams)));

            Pop("<[Index]>");
            Pop("<[EventName]>");
            Pop("<[EventType]>");
            Pop("<[CommaIfNotLast]>");
            Pop("<[CommaOnFirst]>");
            Pop("<[AmpersandIfNotLast]>");

            index++;
        }

        return sb.ToString();
    }

    private string IterateEventParams(string section)
    {
        var sb = new StringBuilder();
        var method = (currentEvent.Type as INamedTypeSymbol)?.DelegateInvokeMethod;
        if (method == null)
            return "";

        int index = 0;
        foreach (var param in method.Parameters)
        {
            Push("<[EventParamType]>", param.Type.ToDisplayString(FullyQualifiedWithoutGlobalFormat));
            Push("<[EventParamName]>", param.Name);
            Push("<[Index]>", index.ToString());
            Push("<[CommaIfNotLast]>", index < method.Parameters.Length - 1 ? "," : "");
            Push("<[AmpersandIfNotLast]>", index < method.Parameters.Length - 1 ? "," : "&");
            Push("<[CommaOnFirst]>", index == 0 ? "," : "");

            sb.Append(ReplaceMarkup(section));

            Pop("<[CommaOnFirst]>");
            Pop("<[EventParamType]>");
            Pop("<[EventParamName]>");
            Pop("<[Index]>");
            Pop("<[CommaIfNotLast]>");
            Pop("<[AmpersandIfNotLast]>");
            index++;
        }
        return sb.ToString();
    }

    private string IterateCtors(string code)
    {
        var sb = new StringBuilder();

        string GetCtorKey(IMethodSymbol ctor) =>
            string.Join(",", ctor.Parameters.Select(p => p.Type.ToDisplayString(FullyQualifiedWithoutGlobalFormat) + ":" + p.Name));

        var seenSignatures = new HashSet<string>();
        var constructors = new List<IMethodSymbol>();

        foreach (var ctor in currentType.Constructors)
        {
            if (ctor.DeclaredAccessibility != Accessibility.Public || ctor.IsStatic)
                continue;

            var key = GetCtorKey(ctor);
            if (seenSignatures.Add(key))
            {
                constructors.Add(ctor);
            }
        }

        // Order the unique constructors
        var orderedCtors = constructors.OrderBy(GetCtorKey).ToArray();

        foreach (var ctor in orderedCtors)
        {
            currentCtor = ctor;
            sb.Append(ReplaceForEach(code, "ForEachCtorParam", IterateCtorParams));
        }

        currentCtor = null;
        return sb.ToString();
    }

    private string IterateCtorParams(string section)
    {
        var builder = new StringBuilder();
        int count = currentCtor.Parameters.Length;
        for (int index = 0; index < count; index++)
        {
            IParameterSymbol item = currentCtor.Parameters[index];

            Push("<[CtorParamName]>", item.Name);
            if (item.Type is INamedTypeSymbol { IsGenericType: true } namedType)
            {
                if (namedType.ConstructedFrom.ToDisplayString() == "System.Nullable<T>")
                {
                    Push("<[CtorParamType]>", namedType.ToDisplayString(FullyQualifiedWithoutGlobalFormat));
                }
                else
                {
                    throw new Exception("Do not use generics other than Nullable values for ClientPage constructors");
                }
            }
            else
            {
                Push("<[CtorParamType]>", item.Type.ToDisplayString(FullyQualifiedWithoutGlobalFormat).Replace('+', '.'));
            }

            Push("<[Index]>", index.ToString());
            Push("<[AmpersandIfNotLast]>", index == count - 1 ? "" : "&");
            Push("<[CommaIfNotLast]>", index == count - 1 ? "" : ",");
            Push("<[CommaOnFirst]>", index == 0 ? "," : "");

            builder.Append(ReplaceMarkup(section));

            Pop("<[Index]>");
            Pop("<[CtorParamName]>");
            Pop("<[CtorParamType]>");
            Pop("<[CommaIfNotLast]>");
            Pop("<[CommaOnFirst]>");
            Pop("<[AmpersandIfNotLast]>");
        }
        return builder.ToString();
    }

    private string IterateActions(string code)
    {
        var sb = new StringBuilder();

        var items = GetDeclaredAndInheritedClientActions(currentType).OrderBy(mi => mi.Name).ToArray();

        int count = items.Length;
        int index = 0;

        foreach (var item in items)
        {
            Push("<[ActionName]>", item.Name);
            Push("<[Index]>", index.ToString());
            Push("<[CommaIfNotLast]>", index < count - 1 ? "," : "");
            Push("<[AmpersandIfNotLast]>", index < count - 1 ? "&" : "");
            Push("<[CommaOnFirst]>", index == 0 ? "," : "");

            currentAction = item;

            sb.Append(ReplaceMarkup(ReplaceForEach(code, "ForEachActionParam", IterateActionParams)));

            Pop("<[Index]>");
            Pop("<[ActionName]>");
            Pop("<[CommaIfNotLast]>");
            Pop("<[CommaOnFirst]>");
            Pop("<[AmpersandIfNotLast]>");

            index++;
        }

        return sb.ToString();
    }

    private string IterateActionParams(string code)
    {
        var sb = new StringBuilder();
        var items = currentAction.Parameters;
        int count = items.Length;

        for (int index = 0; index < count; index++)
        {
            var item = items[index];
            var type = item.Type;
            string typeName;

            if (
                type is INamedTypeSymbol { IsGenericType: true } namedType
                && namedType.ConstructedFrom.ToDisplayString(FullyQualifiedWithoutGlobalFormatNullableFull) == "System.Nullable<T>"
            )
            {
                typeName = $"Nullable<{namedType.TypeArguments[0].ToDisplayString(FullyQualifiedWithoutGlobalFormat)}>";
            }
            else
            {
                typeName = type.ToDisplayString(FullyQualifiedWithoutGlobalFormat);
            }

            Push("<[ActionParamName]>", item.Name);
            Push("<[ActionParamType]>", typeName);
            Push("<[Index]>", index.ToString());

            Push("<[CommaIfNotLast]>", index < count - 1 ? "," : "");
            Push("<[AmpersandIfNotLast]>", index < count - 1 ? "&" : "");
            Push("<[CommaOnFirst]>", index == 0 ? "," : "");

            sb.Append(ReplaceMarkup(code));

            Pop("<[Index]>");
            Pop("<[ActionParamName]>");
            Pop("<[ActionParamType]>");
            Pop("<[CommaIfNotLast]>");
            Pop("<[CommaOnFirst]>");
            Pop("<[AmpersandIfNotLast]>");
        }

        return sb.ToString();
    }

    private static IEnumerable<IEventSymbol> GetDeclaredAndInheritedNonOverriddenEvents(INamedTypeSymbol type)
    {
        var seenNames = new HashSet<string>();
        var current = type;

        while (current != null)
        {
            foreach (var evt in current.GetMembers().OfType<IEventSymbol>())
            {
                if (evt.DeclaredAccessibility != Accessibility.Public)
                    continue;

                if (seenNames.Add(evt.Name))
                    yield return evt;
            }

            current = current.BaseType;
        }
    }

    private static IEnumerable<IMethodSymbol> GetDeclaredAndInheritedClientActions(INamedTypeSymbol type)
    {
        var methods = new Dictionary<string, (IMethodSymbol Method, bool HasClientActionAttr)>();

        for (var current = type; current != null; current = current.BaseType)
        {
            foreach (var method in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (method.DeclaredAccessibility != Accessibility.Public || method.IsStatic)
                    continue;

                var hasClientActionAttr = method
                    .GetAttributes()
                    .Any(attr => attr.AttributeClass?.ToDisplayString() == "MMS.Web.UI.Attributes.ClientActionAttribute");

                var signature =
                    $"{method.Name}({string.Join(",", method.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)))})";

                if (!methods.ContainsKey(signature))
                {
                    methods.Add(signature, (method, hasClientActionAttr));
                }
            }
        }

        return methods.Values.Where(m => m.HasClientActionAttr).Select(m => m.Method).ToArray();
    }

    private static AttributeData? FindFirstOccurrenceOfAttributeInInheritanceChain(INamedTypeSymbol typeSymbol)
    {
        for (var current = typeSymbol; current != null; current = current.BaseType)
        {
            var result = current
                .GetAttributes()
                .FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == "MMS.Web.UI.Attributes.ProxyInterfacesAttribute");

            if (result != null)
                return result;
        }

        return null;
    }

    private string ExtractInterfaces(INamedTypeSymbol typeSymbol)
    {
        var attr = FindFirstOccurrenceOfAttributeInInheritanceChain(typeSymbol);
        if (attr == null)
            return "";

        var interfaces = attr.ConstructorArguments[0]
            .Values.Select(v => v.Value?.ToString() ?? "")
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToList();

        return interfaces.Count > 0 ? ", " + string.Join(", ", interfaces) : "";
    }

    private void Push(string tag, string value)
    {
        if (!tags.ContainsKey(tag))
            tags[tag] = new Stack<string>();
        tags[tag].Push(value);
    }

    private void Pop(string tag)
    {
        if (tags.ContainsKey(tag))
        {
            tags[tag].Pop();
            if (tags[tag].Count == 0)
                tags.Remove(tag);
        }
    }

    private string ReplaceMarkup(string input)
    {
        foreach (var pair in tags)
        {
            input = input.Replace(pair.Key, pair.Value.Peek());
        }
        return input;
    }
}
