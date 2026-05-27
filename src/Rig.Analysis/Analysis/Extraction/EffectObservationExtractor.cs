using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Rig.Analysis;

internal static class EffectObservationExtractor
{
    private static readonly HashSet<string> EfReadMethodNames = new(StringComparer.Ordinal)
    {
        "ToListAsync", "FirstAsync", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync",
        "AnyAsync", "CountAsync", "LongCountAsync", "FindAsync"
    };

    public static EffectInfo AttachObservations(
        InvocationExpressionSyntax invocation,
        EffectInfo effect,
        SemanticModel semanticModel)
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

        var resilience = FindResilienceRetryContext(invocation, semanticModel);
        if (resilience is not null)
        {
            observations.Add(new EffectObservationInfo(
                "resilience_retry",
                resilience.Value.Context,
                resilience.Value.Detail,
                "high",
                "compilation",
                "effect_inside_resilience_retry"));
        }

        var readBeforeCommit = FindReadBeforeCommitContext(invocation, semanticModel);
        if (readBeforeCommit is not null)
        {
            observations.Add(new EffectObservationInfo(
                "read_before_commit",
                readBeforeCommit.Value.Context,
                readBeforeCommit.Value.Detail,
                "medium",
                "compilation",
                "potential_lost_update"));
        }

        var concurrencyHandled = FindConcurrencyHandlingContext(invocation, semanticModel);
        if (concurrencyHandled is not null)
        {
            observations.Add(new EffectObservationInfo(
                "concurrency_handled",
                concurrencyHandled.Value.Context,
                concurrencyHandled.Value.Detail,
                "high",
                "compilation",
                "efcore_optimistic_concurrency_catch"));
        }

        return effect with { Observations = observations };
    }

    private static (string Context, string Detail)? FindReadBeforeCommitContext(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax commitAccess)
            return null;

        var name = commitAccess.Name.Identifier.ValueText;
        if (!string.Equals(name, "SaveChangesAsync", StringComparison.Ordinal) &&
            !string.Equals(name, "SaveChanges", StringComparison.Ordinal))
            return null;

        var methodBody = invocation.Ancestors().FirstOrDefault(static a => a is
            MethodDeclarationSyntax or LocalFunctionStatementSyntax or
            AnonymousMethodExpressionSyntax or SimpleLambdaExpressionSyntax or
            ParenthesizedLambdaExpressionSyntax);

        if (methodBody is null)
            return null;

        var commitPos = invocation.SpanStart;
        foreach (var candidate in methodBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (candidate.SpanStart >= commitPos)
                continue;
            if (candidate.Expression is not MemberAccessExpressionSyntax readAccess)
                continue;
            if (!EfReadMethodNames.Contains(readAccess.Name.Identifier.ValueText))
                continue;

            var receiverFqn = semanticModel.GetTypeInfo(readAccess.Expression).Type
                ?.OriginalDefinition.ToDisplayString() ?? "";
            if (!receiverFqn.Contains("DbSet", StringComparison.Ordinal) &&
                !receiverFqn.Contains("IQueryable", StringComparison.Ordinal))
                continue;

            var readLine = semanticModel.SyntaxTree.GetLineSpan(candidate.Span).StartLinePosition.Line + 1;
            return ("before_commit", $"line_{readLine}");
        }

        return null;
    }

    private static (string Context, string Detail)? FindConcurrencyHandlingContext(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax commitAccess)
            return null;

        var name = commitAccess.Name.Identifier.ValueText;
        if (!string.Equals(name, "SaveChangesAsync", StringComparison.Ordinal) &&
            !string.Equals(name, "SaveChanges", StringComparison.Ordinal))
            return null;

        foreach (var ancestor in invocation.Ancestors().OfType<TryStatementSyntax>())
        {
            foreach (var catchClause in ancestor.Catches)
            {
                if (catchClause.Declaration is null)
                    continue;

                var catchTypeName = semanticModel.GetTypeInfo(catchClause.Declaration.Type).Type
                    ?.ToDisplayString() ?? "";
                if (catchTypeName.Contains("DbUpdateConcurrencyException", StringComparison.Ordinal))
                    return ("DbUpdateConcurrencyException", catchTypeName);
                if (catchTypeName.Contains("DbUpdateException", StringComparison.Ordinal))
                    return ("DbUpdateException", catchTypeName);
            }
        }

        return null;
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

    private static (string Context, string Detail)? FindResilienceRetryContext(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        foreach (var ancestor in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (!string.Equals(methodName, "Execute", StringComparison.Ordinal) &&
                !string.Equals(methodName, "ExecuteAsync", StringComparison.Ordinal))
                continue;

            var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is null)
                continue;

            var receiverTypeName = receiverType.OriginalDefinition.ToDisplayString();

            if (receiverTypeName.Contains("ResiliencePipeline", StringComparison.Ordinal))
                return ("ResiliencePipeline", receiverTypeName);

            if (receiverTypeName.Contains("ExecutionStrategy", StringComparison.Ordinal) ||
                receiverTypeName.Contains("IExecutionStrategy", StringComparison.Ordinal))
                return ("ExecutionStrategy", receiverTypeName);
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
}
