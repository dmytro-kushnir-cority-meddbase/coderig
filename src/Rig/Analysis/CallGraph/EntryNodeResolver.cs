using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal sealed record EntryNodeResolution(
    CallGraphNodeInfo Node,
    IReadOnlyList<ResolvedCall> Calls);

internal static class EntryNodeResolver
{
    public static EntryNodeResolution Resolve(
        EntryPointInfo entryPoint,
        IReadOnlyList<SourceModel> sources,
        CallGraphContext context)
    {
        var method = context.Methods.Values.FirstOrDefault(method =>
            string.Equals(method.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase) &&
            method.Line == entryPoint.Line);

        if (method is not null)
        {
            var calls = CallResolver.ResolveCalls(method.Body, method.SemanticModel, context);
            var reason = entryPoint.Kind == "mvc" ? "mvc_action_symbol" : "handler_method_symbol";
            return new EntryNodeResolution(
                CallGraphNodeFactory.Create(method.Key, entryPoint.FilePath, entryPoint.Line, calls, method.Effects, "high", "compilation", reason),
                calls.Application);
        }

        var source = sources.First(source => string.Equals(source.FilePath, entryPoint.FilePath, StringComparison.OrdinalIgnoreCase));
        var invocation = FindMinimalApiInvocation(entryPoint, source);
        if (invocation is not null)
        {
            var handlerArg = invocation.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;
            var lambdaEffects = Array.Empty<EffectInfo>();
            if (handlerArg is ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
            {
                lambdaEffects = context.AllEffects
                    .Where(e => string.Equals(e.FilePath, source.FilePath, StringComparison.OrdinalIgnoreCase))
                    .Where(e => RoslynSymbolHelpers.IsLineInside(source.Tree, handlerArg, e.Line))
                    .ToArray();
            }

            var calls = ResolveMinimalApiHandlerCalls(invocation, source.SemanticModel, context);
            return new EntryNodeResolution(
                CallGraphNodeFactory.Create(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, calls, lambdaEffects, "high", "compilation", "minimal_api_handler_symbol"),
                calls.Application);
        }

        return new EntryNodeResolution(
            CallGraphNodeFactory.Create(entryPoint.DisplayName, entryPoint.FilePath, entryPoint.Line, new ResolvedCallSet([], []), [], "low", "compilation", "entrypoint_handler_unresolved"),
            []);
    }

    private static InvocationExpressionSyntax? FindMinimalApiInvocation(EntryPointInfo entryPoint, SourceModel source)
    {
        return source.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .FirstOrDefault(invocation =>
                RoslynSymbolHelpers.GetLine(source.Tree, invocation) == entryPoint.Line &&
                invocation.ArgumentList.Arguments.Count >= 2 &&
                invocation.ArgumentList.Arguments[0].Expression.GetLiteralString() == entryPoint.Route);
    }

    private static ResolvedCallSet ResolveMinimalApiHandlerCalls(
        InvocationExpressionSyntax invocation,
        Microsoft.CodeAnalysis.SemanticModel semanticModel,
        CallGraphContext context)
    {
        var handler = invocation.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;
        if (handler is null)
        {
            return new ResolvedCallSet([], []);
        }

        if (handler is ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
        {
            return CallResolver.ResolveCalls(handler, semanticModel, context);
        }

        var methodSymbol = RoslynSymbolHelpers.ResolveMethodSymbol(handler, semanticModel);
        if (methodSymbol is null)
        {
            return new ResolvedCallSet([], [CallResolver.CreateUnresolvedBoundaryCall(handler, semanticModel)]);
        }

        var key = RoslynSymbolHelpers.GetMethodKey(methodSymbol);
        var handlerLine = RoslynSymbolHelpers.GetCallNameLine(semanticModel.SyntaxTree, handler);
        return context.Methods.ContainsKey(key)
            ? new ResolvedCallSet([new ResolvedCall(key, handlerLine)], [])
            : new ResolvedCallSet([], [CallResolver.CreateExternalBoundaryCall(handler, semanticModel, methodSymbol)]);
    }
}
