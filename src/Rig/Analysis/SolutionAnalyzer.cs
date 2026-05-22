using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

public static class SolutionAnalyzer
{
    private static readonly Dictionary<string, string> MinimalApiMethods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET",
        ["MapPost"] = "POST",
        ["MapPut"] = "PUT",
        ["MapDelete"] = "DELETE",
        ["MapPatch"] = "PATCH"
    };

    private static readonly Dictionary<string, string> MvcHttpAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpGetAttribute"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPostAttribute"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpPutAttribute"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatch"] = "PATCH",
        ["HttpPatchAttribute"] = "PATCH"
    };

    private static readonly HashSet<string> EfReadMethods = new(StringComparer.Ordinal)
    {
        "ToListAsync",
        "FirstAsync",
        "FirstOrDefaultAsync",
        "SingleAsync",
        "SingleOrDefaultAsync",
        "AnyAsync",
        "CountAsync",
        "FindAsync"
    };

    public static async Task<AnalysisResult> AnalyzeAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        var sourceFiles = DiscoverSourceFiles(solutionPath);
        var entryPoints = new List<EntryPointInfo>();
        var effects = new List<EffectInfo>();

        foreach (var sourceFile in sourceFiles)
        {
            var text = await File.ReadAllTextAsync(sourceFile, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken, path: sourceFile);
            var root = await tree.GetRootAsync(cancellationToken);
            var fields = FieldTypeIndex.Create(root);

            entryPoints.AddRange(FindMinimalApiEntryPoints(root, tree, sourceFile));
            entryPoints.AddRange(FindMvcEntryPoints(root, tree, sourceFile));
            effects.AddRange(FindEffects(root, tree, sourceFile, fields));
        }

        return new AnalysisResult(entryPoints, effects);
    }

    private static IReadOnlyList<string> DiscoverSourceFiles(string solutionPath)
    {
        var solutionFullPath = Path.GetFullPath(solutionPath);
        var solutionDirectory = Path.GetDirectoryName(solutionFullPath)
            ?? throw new InvalidOperationException($"Solution path has no directory: {solutionPath}");

        var projectPaths = solutionFullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? DiscoverSlnxProjects(solutionFullPath, solutionDirectory)
            : DiscoverSlnProjects(solutionFullPath, solutionDirectory);

        return projectPaths
            .SelectMany(DiscoverProjectSources)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> DiscoverSlnxProjects(string solutionPath, string solutionDirectory)
    {
        var document = XDocument.Load(solutionPath);

        return document
            .Descendants("Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(solutionDirectory, path!)))
            .ToArray();
    }

    private static IReadOnlyList<string> DiscoverSlnProjects(string solutionPath, string solutionDirectory)
    {
        return File.ReadLines(solutionPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("Project(", StringComparison.Ordinal))
            .Select(line => line.Split(','))
            .Where(parts => parts.Length >= 2)
            .Select(parts => parts[1].Trim().Trim('"'))
            .Where(path => path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetFullPath(Path.Combine(solutionDirectory, path)))
            .ToArray();
    }

    private static IEnumerable<string> DiscoverProjectSources(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath)
            ?? throw new InvalidOperationException($"Project path has no directory: {projectPath}");

        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !HasPathSegment(path, "bin"))
            .Where(path => !HasPathSegment(path, "obj"));
    }

    private static bool HasPathSegment(string path, string segment)
    {
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => string.Equals(part, segment, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<EntryPointInfo> FindMinimalApiEntryPoints(
        SyntaxNode root,
        SyntaxTree tree,
        string sourceFile)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (!MinimalApiMethods.TryGetValue(methodName, out var httpMethod))
            {
                continue;
            }

            var route = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.GetLiteralString();
            if (string.IsNullOrWhiteSpace(route))
            {
                continue;
            }

            yield return new EntryPointInfo(
                "minapi",
                httpMethod,
                route,
                $"minapi {httpMethod} {route}",
                sourceFile,
                GetLine(tree, invocation));
        }
    }

    private static IEnumerable<EntryPointInfo> FindMvcEntryPoints(
        SyntaxNode root,
        SyntaxTree tree,
        string sourceFile)
    {
        foreach (var controller in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
        {
            if (!controller.Identifier.ValueText.EndsWith("Controller", StringComparison.Ordinal))
            {
                continue;
            }

            var controllerToken = controller.Identifier.ValueText[..^"Controller".Length].ToLowerInvariant();
            var controllerRoute = GetAttributeStringArgument(controller.AttributeLists, "Route") ?? "[controller]";
            controllerRoute = controllerRoute.Replace("[controller]", controllerToken, StringComparison.OrdinalIgnoreCase);

            foreach (var method in controller.Members.OfType<MethodDeclarationSyntax>())
            {
                var httpAttribute = FindHttpAttribute(method.AttributeLists);
                if (httpAttribute is null)
                {
                    continue;
                }

                var route = CombineRoutes(controllerRoute, httpAttribute.Value.Route);

                yield return new EntryPointInfo(
                    "mvc",
                    httpAttribute.Value.Method,
                    route,
                    $"mvc {httpAttribute.Value.Method} {route}",
                    sourceFile,
                    GetLine(tree, method));
            }
        }
    }

    private static (string Method, string? Route)? FindHttpAttribute(SyntaxList<AttributeListSyntax> attributes)
    {
        foreach (var attribute in attributes.SelectMany(list => list.Attributes))
        {
            var attributeName = attribute.Name.ToString();
            if (!MvcHttpAttributes.TryGetValue(attributeName, out var method))
            {
                continue;
            }

            return (method, attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.GetLiteralString());
        }

        return null;
    }

    private static string? GetAttributeStringArgument(SyntaxList<AttributeListSyntax> attributes, string attributeName)
    {
        return attributes
            .SelectMany(list => list.Attributes)
            .Where(attribute => string.Equals(attribute.Name.ToString(), attributeName, StringComparison.Ordinal)
                || string.Equals(attribute.Name.ToString(), $"{attributeName}Attribute", StringComparison.Ordinal))
            .Select(attribute => attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression.GetLiteralString())
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string CombineRoutes(string prefix, string? suffix)
    {
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return prefix.Trim('/');
        }

        return $"{prefix.TrimEnd('/')}/{suffix.TrimStart('/')}".Trim('/');
    }

    private static IEnumerable<EffectInfo> FindEffects(
        SyntaxNode root,
        SyntaxTree tree,
        string sourceFile,
        FieldTypeIndex fields)
    {
        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            var line = GetLine(tree, invocation);

            var httpEffect = TryCreateHttpEffect(methodName, invocation, sourceFile, line);
            if (httpEffect is not null)
            {
                yield return httpEffect;
            }

            var efEffect = TryCreateEfEffect(methodName, memberAccess, sourceFile, line, fields);
            if (efEffect is not null)
            {
                yield return efEffect;
            }

            var redisEffect = TryCreateRedisEffect(methodName, invocation, sourceFile, line);
            if (redisEffect is not null)
            {
                yield return redisEffect;
            }
        }
    }

    private static EffectInfo? TryCreateHttpEffect(
        string methodName,
        InvocationExpressionSyntax invocation,
        string sourceFile,
        int line)
    {
        var operation = methodName switch
        {
            "GetAsync" or "GetStringAsync" or "GetFromJsonAsync" => "GET",
            "PostAsync" or "PostAsJsonAsync" => "POST",
            "PutAsync" or "PutAsJsonAsync" => "PUT",
            "DeleteAsync" => "DELETE",
            _ => null
        };

        if (operation is null)
        {
            return null;
        }

        var url = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.GetStringTemplate();
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var resource = NormalizeHttpResource(url);

        return new EffectInfo(
            "http",
            operation,
            resource,
            methodName,
            sourceFile,
            line,
            "high",
            "compilation+profile",
            "httpclient_method_match");
    }

    private static EffectInfo? TryCreateEfEffect(
        string methodName,
        MemberAccessExpressionSyntax memberAccess,
        string sourceFile,
        int line,
        FieldTypeIndex fields)
    {
        if (EfReadMethods.Contains(methodName))
        {
            var dbSetResource = TryGetDbSetResource(memberAccess.Expression, fields);
            if (dbSetResource is null)
            {
                return null;
            }

            return new EffectInfo(
                "efcore",
                "read",
                dbSetResource,
                methodName,
                sourceFile,
                line,
                "medium",
                "compilation+heuristic",
                "dbset_materialization");
        }

        if (methodName is "SaveChanges" or "SaveChangesAsync")
        {
            var contextResource = TryGetContextResource(memberAccess.Expression, fields);
            if (contextResource is null)
            {
                return null;
            }

            return new EffectInfo(
                "efcore",
                "commit",
                contextResource,
                methodName,
                sourceFile,
                line,
                "high",
                "compilation+profile",
                "dbcontext_commit");
        }

        if (methodName is "Add" or "AddAsync" or "Update" or "Remove")
        {
            var dbSetResource = TryGetDbSetResource(memberAccess.Expression, fields);
            if (dbSetResource is null)
            {
                return null;
            }

            return new EffectInfo(
                "efcore",
                "pending_write",
                dbSetResource,
                methodName,
                sourceFile,
                line,
                "medium",
                "compilation+heuristic",
                "change_tracker");
        }

        return null;
    }

    private static EffectInfo? TryCreateRedisEffect(
        string methodName,
        InvocationExpressionSyntax invocation,
        string sourceFile,
        int line)
    {
        var operation = methodName switch
        {
            "StringGet" or "StringGetAsync" or "HashGet" or "HashGetAsync" => "read",
            "StringSet" or "StringSetAsync" or "HashSet" or "HashSetAsync" => "write",
            "KeyDelete" or "KeyDeleteAsync" => "delete",
            _ => null
        };

        if (operation is null)
        {
            return null;
        }

        var key = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression.GetStringTemplate();
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        return new EffectInfo(
            "redis",
            operation,
            key,
            methodName,
            sourceFile,
            line,
            "high",
            "compilation+profile",
            "stackexchange_redis_method_match");
    }

    private static string NormalizeHttpResource(string url)
    {
        var schemeSeparator = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var withoutScheme = url[(schemeSeparator + 3)..];
            return withoutScheme.TrimEnd('/');
        }

        return url.TrimStart('/');
    }

    private static string? TryGetDbSetResource(ExpressionSyntax expression, FieldTypeIndex fields)
    {
        if (expression is not MemberAccessExpressionSyntax dbSetAccess)
        {
            return null;
        }

        if (dbSetAccess.Expression is not IdentifierNameSyntax identifier)
        {
            return null;
        }

        var contextType = fields.GetTypeName(identifier.Identifier.ValueText);
        return contextType is null ? null : $"{contextType}.{dbSetAccess.Name.Identifier.ValueText}";
    }

    private static string? TryGetContextResource(ExpressionSyntax expression, FieldTypeIndex fields)
    {
        return expression is IdentifierNameSyntax identifier
            ? fields.GetTypeName(identifier.Identifier.ValueText)
            : null;
    }

    private static int GetLine(SyntaxTree tree, SyntaxNode node)
    {
        return tree.GetLineSpan(node.Span).StartLinePosition.Line + 1;
    }

    private sealed class FieldTypeIndex
    {
        private readonly Dictionary<string, string> fieldTypes;

        private FieldTypeIndex(Dictionary<string, string> fieldTypes)
        {
            this.fieldTypes = fieldTypes;
        }

        public static FieldTypeIndex Create(SyntaxNode root)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
            {
                var typeName = field.Declaration.Type switch
                {
                    IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                    QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                    _ => field.Declaration.Type.ToString()
                };

                foreach (var variable in field.Declaration.Variables)
                {
                    fields[variable.Identifier.ValueText] = typeName;
                }
            }

            return new FieldTypeIndex(fields);
        }

        public string? GetTypeName(string fieldName)
        {
            return fieldTypes.GetValueOrDefault(fieldName);
        }
    }
}
