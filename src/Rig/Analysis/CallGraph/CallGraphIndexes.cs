using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal sealed record CallGraphIndexSet(
    IReadOnlyDictionary<string, IReadOnlyList<string>> DispatchIndex,
    IReadOnlyDictionary<string, string> SingleImplIndex);

internal static class CallGraphIndexes
{
    public static CallGraphIndexSet Build(
        IReadOnlyList<SourceModel> sources,
        IReadOnlyList<DiRegistrationInfo> diRegistrations)
    {
        return new CallGraphIndexSet(
            BuildDispatchIndex(sources),
            BuildSingleImplIndex(diRegistrations));
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildDispatchIndex(
        IReadOnlyList<SourceModel> sources)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var source in sources)
        {
            foreach (var classDecl in source.Root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (source.SemanticModel.GetDeclaredSymbol(classDecl) is not INamedTypeSymbol classSymbol)
                {
                    continue;
                }

                foreach (var iface in classSymbol.AllInterfaces)
                {
                    if (!IsMessageHandlerInterface(iface.OriginalDefinition) || iface.TypeArguments.Length == 0)
                    {
                        continue;
                    }

                    var messageKey = iface.TypeArguments[0].OriginalDefinition.ToDisplayString();

                    var handleMethod = classDecl.Members
                        .OfType<MethodDeclarationSyntax>()
                        .FirstOrDefault(m => m.Identifier.ValueText is "Handle" or "HandleAsync");

                    if (handleMethod is null ||
                        source.SemanticModel.GetDeclaredSymbol(handleMethod) is not IMethodSymbol handleSymbol)
                    {
                        continue;
                    }

                    var methodKey = RoslynSymbolHelpers.GetMethodKey(handleSymbol);

                    if (!index.TryGetValue(messageKey, out var handlers))
                    {
                        index[messageKey] = handlers = [];
                    }

                    if (!handlers.Contains(methodKey, StringComparer.Ordinal))
                    {
                        handlers.Add(methodKey);
                    }
                }
            }
        }

        return index.ToDictionary(
            kvp => kvp.Key,
            kvp => (IReadOnlyList<string>)kvp.Value.AsReadOnly(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> BuildSingleImplIndex(
        IReadOnlyList<DiRegistrationInfo> registrations)
    {
        return registrations
            .Where(r => r.ImplementationType is not null)
            .GroupBy(r => r.ServiceType, StringComparer.Ordinal)
            .Where(g => g.Select(r => r.ImplementationType).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(
                g => g.Key,
                g => g.First().ImplementationType!,
                StringComparer.Ordinal);
    }

    private static bool IsMessageHandlerInterface(INamedTypeSymbol iface)
    {
        var ns = iface.ContainingNamespace?.ToDisplayString();
        return ns is "MediatR" or "Mediator" &&
               iface.Name is "IRequestHandler" or "ICommandHandler" or "IQueryHandler" or "INotificationHandler";
    }
}
