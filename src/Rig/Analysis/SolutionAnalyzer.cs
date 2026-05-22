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
        var sources = new List<SourceModel>();
        var entryPoints = new List<EntryPointInfo>();
        var effects = new List<EffectInfo>();

        foreach (var sourceFile in sourceFiles)
        {
            var text = await File.ReadAllTextAsync(sourceFile, cancellationToken);
            var tree = CSharpSyntaxTree.ParseText(text, cancellationToken: cancellationToken, path: sourceFile);
            var root = await tree.GetRootAsync(cancellationToken);
            var fields = FieldTypeIndex.Create(root);
            sources.Add(new SourceModel(sourceFile, tree, root, fields));

            entryPoints.AddRange(FindMinimalApiEntryPoints(root, tree, sourceFile));
            entryPoints.AddRange(FindMvcEntryPoints(root, tree, sourceFile));
            effects.AddRange(FindEffects(root, tree, sourceFile, fields));
        }

        var callGraphs = BuildCallGraphs(entryPoints, sources, effects);

        return new AnalysisResult(entryPoints, effects, callGraphs);
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
                yield return AttachObservations(invocation, httpEffect);
            }

            var efEffect = TryCreateEfEffect(methodName, memberAccess, sourceFile, line, fields);
            if (efEffect is not null)
            {
                yield return AttachObservations(invocation, efEffect);
            }

            var redisEffect = TryCreateRedisEffect(methodName, invocation, sourceFile, line);
            if (redisEffect is not null)
            {
                yield return AttachObservations(invocation, redisEffect);
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
            "httpclient_method_match",
            []);
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
                "dbset_materialization",
                []);
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
                "dbcontext_commit",
                []);
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
                "change_tracker",
                []);
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
            "stackexchange_redis_method_match",
            []);
    }

    private static EffectInfo AttachObservations(InvocationExpressionSyntax invocation, EffectInfo effect)
    {
        var observations = new List<EffectObservationInfo>();

        var loop = FindLoopContext(invocation);
        if (loop is not null)
        {
            observations.Add(new EffectObservationInfo(
                "looped_effect",
                loop.Value.Context,
                loop.Value.Detail,
                "high",
                "compilation",
                "effect_inside_loop"));
        }

        var fanout = FindParallelFanoutContext(invocation);
        if (fanout is not null)
        {
            observations.Add(new EffectObservationInfo(
                "parallel_fanout",
                fanout.Value.Context,
                fanout.Value.Detail,
                "high",
                "compilation",
                "effect_inside_parallel_fanout"));
        }

        return effect with { Observations = observations };
    }

    private static (string Context, string Detail)? FindLoopContext(InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in invocation.Ancestors())
        {
            switch (ancestor)
            {
                case ForEachStatementSyntax forEach:
                    return ("foreach", $"{forEach.Identifier.ValueText} in {forEach.Expression}");
                case ForStatementSyntax:
                    return ("for", "for");
                case WhileStatementSyntax:
                    return ("while", "while");
            }
        }

        return null;
    }

    private static (string Context, string Detail)? FindParallelFanoutContext(InvocationExpressionSyntax invocation)
    {
        foreach (var ancestor in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            var methodName = memberAccess.Name.Identifier.ValueText;
            var receiver = memberAccess.Expression.ToString();

            if (string.Equals(receiver, "Task", StringComparison.Ordinal) &&
                string.Equals(methodName, "WhenAll", StringComparison.Ordinal))
            {
                return ("Task.WhenAll", "Task.WhenAll");
            }

            if (string.Equals(receiver, "Parallel", StringComparison.Ordinal) &&
                (string.Equals(methodName, "ForEach", StringComparison.Ordinal) ||
                 string.Equals(methodName, "ForEachAsync", StringComparison.Ordinal)))
            {
                return ($"Parallel.{methodName}", $"Parallel.{methodName}");
            }
        }

        return null;
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

    private static IReadOnlyList<CallGraphInfo> BuildCallGraphs(
        IReadOnlyList<EntryPointInfo> entryPoints,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyList<EffectInfo> effects)
    {
        var methods = sources
            .SelectMany(source => FindApplicationMethods(source, effects))
            .ToDictionary(method => method.Symbol, StringComparer.Ordinal);

        return entryPoints
            .Select(entryPoint => BuildCallGraph(entryPoint, sources, methods))
            .ToArray();
    }

    private static IEnumerable<MethodModel> FindApplicationMethods(SourceModel source, IReadOnlyList<EffectInfo> effects)
    {
        foreach (var method in source.Root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Parent is not ClassDeclarationSyntax containingClass)
            {
                continue;
            }

            var symbol = $"{containingClass.Identifier.ValueText}.{method.Identifier.ValueText}";
            var line = GetLine(source.Tree, method);
            var methodEffects = effects
                .Where(effect => string.Equals(effect.FilePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
                .Where(effect => IsLineInside(source.Tree, method, effect.Line))
                .ToArray();

            yield return new MethodModel(
                symbol,
                source.FilePath,
                line,
                method,
                source.Fields,
                BuildMethodVariableTypes(method, source.Fields),
                methodEffects);
        }
    }

    private static CallGraphInfo BuildCallGraph(
        EntryPointInfo entryPoint,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        var nodes = new List<CallGraphNodeInfo>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var entryNode = CreateEntryNode(entryPoint, sources, methods);
        nodes.Add(entryNode.Node);

        foreach (var call in entryNode.Calls)
        {
            VisitMethod(call, methods, nodes, visited);
        }

        return new CallGraphInfo(entryPoint.DisplayName, nodes);
    }

    private static (CallGraphNodeInfo Node, IReadOnlyList<string> Calls) CreateEntryNode(
        EntryPointInfo entryPoint,
        IReadOnlyList<SourceModel> sources,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        if (entryPoint.Kind == "mvc")
        {
            var method = methods.Values.FirstOrDefault(method =>
                string.Equals(method.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase) &&
                method.Line == entryPoint.Line);

            if (method is not null)
            {
                var calls = ResolveCalls(method.Body, method.VariableTypes, methods);
                return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, []), calls);
            }
        }

        var source = sources.First(source => string.Equals(source.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase));
        var invocation = source.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation => GetLine(source.Tree, invocation) == entryPoint.Line);

        if (invocation is not null)
        {
            var variableTypes = BuildMinimalApiVariableTypes(invocation);
            var calls = ResolveCalls(invocation, variableTypes, methods);
            return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, []), calls);
        }

        return (CreateNode(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, [], []), []);
    }

    private static void VisitMethod(
        string symbol,
        IReadOnlyDictionary<string, MethodModel> methods,
        List<CallGraphNodeInfo> nodes,
        HashSet<string> visited)
    {
        if (!visited.Add(symbol) || !methods.TryGetValue(symbol, out var method))
        {
            return;
        }

        var calls = ResolveCalls(method.Body, method.VariableTypes, methods);
        nodes.Add(CreateNode(method.Symbol, method.FilePath, method.Line, calls, method.Effects));

        foreach (var call in calls)
        {
            VisitMethod(call, methods, nodes, visited);
        }
    }

    private static CallGraphNodeInfo CreateNode(
        string symbol,
        string filePath,
        int line,
        IReadOnlyList<string> calls,
        IReadOnlyList<EffectInfo> effects)
    {
        return new CallGraphNodeInfo(
            symbol,
            filePath,
            line,
            "medium",
            "heuristic",
            "syntax_local_resolution",
            calls,
            effects);
    }

    private static IReadOnlyList<string> ResolveCalls(
        SyntaxNode root,
        IReadOnlyDictionary<string, string> variableTypes,
        IReadOnlyDictionary<string, MethodModel> methods)
    {
        var calls = new List<string>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            if (memberAccess.Expression is not IdentifierNameSyntax receiver)
            {
                continue;
            }

            if (!variableTypes.TryGetValue(receiver.Identifier.ValueText, out var typeName))
            {
                continue;
            }

            var symbol = $"{typeName}.{memberAccess.Name.Identifier.ValueText}";
            if (methods.ContainsKey(symbol) && !calls.Contains(symbol, StringComparer.Ordinal))
            {
                calls.Add(symbol);
            }
        }

        return calls;
    }

    private static IReadOnlyDictionary<string, string> BuildMethodVariableTypes(
        MethodDeclarationSyntax method,
        FieldTypeIndex fields)
    {
        var variableTypes = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var field in fields.All)
        {
            variableTypes[field.Key] = field.Value;
        }

        foreach (var parameter in method.ParameterList.Parameters)
        {
            if (parameter.Type is not null)
            {
                variableTypes[parameter.Identifier.ValueText] = GetSimpleTypeName(parameter.Type);
            }
        }

        return variableTypes;
    }

    private static IReadOnlyDictionary<string, string> BuildMinimalApiVariableTypes(InvocationExpressionSyntax invocation)
    {
        var variableTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        var handler = invocation.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;

        if (handler is ParenthesizedLambdaExpressionSyntax lambda)
        {
            foreach (var parameter in lambda.ParameterList.Parameters)
            {
                if (parameter.Type is not null)
                {
                    variableTypes[parameter.Identifier.ValueText] = GetSimpleTypeName(parameter.Type);
                }
            }
        }

        return variableTypes;
    }

    private static bool IsLineInside(SyntaxTree tree, SyntaxNode node, int line)
    {
        var span = tree.GetLineSpan(node.Span);
        var start = span.StartLinePosition.Line + 1;
        var end = span.EndLinePosition.Line + 1;
        return line >= start && line <= end;
    }

    private static string GetSimpleTypeName(TypeSyntax type)
    {
        return type switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            _ => type.ToString()
        };
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

        public IReadOnlyDictionary<string, string> All => fieldTypes;
    }

    private sealed record SourceModel(
        string FilePath,
        SyntaxTree Tree,
        SyntaxNode Root,
        FieldTypeIndex Fields);

    private sealed record MethodModel(
        string Symbol,
        string FilePath,
        int Line,
        MethodDeclarationSyntax Body,
        FieldTypeIndex Fields,
        IReadOnlyDictionary<string, string> VariableTypes,
        IReadOnlyList<EffectInfo> Effects);
}
