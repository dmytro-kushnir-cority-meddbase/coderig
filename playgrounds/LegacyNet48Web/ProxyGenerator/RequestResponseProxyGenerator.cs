using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MMS.Tools.RequestResponseProxyProjectBuilder.Roslyn;

[Generator]
public class RequestResponseProxyGenerator : ISourceGenerator
{
    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        var requestTemplate = Templates.Request;
        var responseTemplate = Templates.Response;

        var clientPageSymbol = context.Compilation.GetTypeByMetadataName("MMS.Web.UI.ClientPage");
        if (clientPageSymbol == null)
            return;

        HashSet<INamedTypeSymbol> seen = new(comparer: SymbolEqualityComparer.Default);

        var project = new Project();

        foreach (var syntaxTree in context.Compilation.SyntaxTrees)
        {
            var semanticModel = context.Compilation.GetSemanticModel(syntaxTree: syntaxTree);
            var root = syntaxTree.GetRoot();

            var classDeclarations = root.DescendantNodes().OfType<ClassDeclarationSyntax>();

            foreach (var classDecl in classDeclarations)
            {
                var symbol = semanticModel.GetDeclaredSymbol(declaration: classDecl);
                if (symbol is not INamedTypeSymbol typeSymbol)
                    continue;

                if (!IsSubclassOf(type: typeSymbol, baseType: clientPageSymbol))
                    continue;

                if (typeSymbol.IsAbstract)
                    continue;

                if (!seen.Add(item: typeSymbol))
                    continue;

                var fqClassName = typeSymbol.ToDisplayString();
                var invalidChars = Path.GetInvalidFileNameChars().Concat(second: ['<', '>', ',', ' ', '[', ']', '`']).ToArray();
                var safeName = new string(value: [.. fqClassName.Select(selector: c => invalidChars.Contains(value: c) ? '_' : c)]);

                var requestProxy = project.GenerateProxy(typeSymbol: typeSymbol, template: requestTemplate);
                var responseProxy = project.GenerateProxy(typeSymbol: typeSymbol, template: responseTemplate);

                context.AddSource(hintName: $"{safeName}_ResponseProxy.g.cs", sourceText: SourceText.From(text: responseProxy, encoding: Encoding.UTF8));
                context.AddSource(hintName: $"{safeName}_RequestProxy.g.cs", sourceText: SourceText.From(text: requestProxy, encoding: Encoding.UTF8));
            }
        }
    }

    private bool IsSubclassOf(INamedTypeSymbol type, INamedTypeSymbol baseType)
    {
        while (type != null)
        {
            if (SymbolEqualityComparer.Default.Equals(x: type.BaseType, y: baseType))
                return true;

            type = type.BaseType;
        }

        return false;
    }
}