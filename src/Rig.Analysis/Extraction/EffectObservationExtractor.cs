using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Rig.Analysis.Rules;
using Rig.Domain.Data;

namespace Rig.Analysis.Extraction;

internal static class EffectObservationExtractor
{
    public static EffectInfo AttachObservations(InvocationExpressionSyntax invocation, EffectInfo effect, SemanticModel semanticModel, AnalysisRuleSet rules)
    {
        var observations = new List<EffectObservationInfo>();

        var loop = FindLoopContext(invocation);
        if (loop is not null)
        {
            observations.Add(
                new EffectObservationInfo(
                    "looped_effect",
                    loop.Value.Context,
                    loop.Value.Detail,
                    "high",
                    "compilation",
                    "effect_inside_loop"
                )
            );
        }

        var fanout = FindParallelFanoutContext(invocation);
        if (fanout is not null)
        {
            observations.Add(
                new EffectObservationInfo(
                    "parallel_fanout",
                    fanout.Value.Context,
                    fanout.Value.Detail,
                    "high",
                    "compilation",
                    "effect_inside_parallel_fanout"
                )
            );
        }

        foreach (var rule in rules.ResilienceRetryObservations)
        {
            var resilience = FindResilienceRetryContext(invocation, semanticModel, rule);
            if (resilience is not null)
            {
                observations.Add(
                    new EffectObservationInfo(
                        "resilience_retry",
                        resilience.Value.Context,
                        resilience.Value.Detail,
                        "high",
                        "compilation",
                        "effect_inside_resilience_retry"
                    )
                );
            }
        }

        foreach (var rule in rules.ReadBeforeCommitObservations)
        {
            var readBeforeCommit = FindReadBeforeCommitContext(invocation, semanticModel, rule);
            if (readBeforeCommit is not null)
            {
                observations.Add(
                    new EffectObservationInfo(
                        "read_before_commit",
                        readBeforeCommit.Value.Context,
                        readBeforeCommit.Value.Detail,
                        "medium",
                        "compilation",
                        "potential_lost_update"
                    )
                );
                break;
            }
        }

        foreach (var rule in rules.ConcurrencyHandledObservations)
        {
            var concurrencyHandled = FindConcurrencyHandlingContext(invocation, semanticModel, rule);
            if (concurrencyHandled is not null)
            {
                observations.Add(
                    new EffectObservationInfo(
                        "concurrency_handled",
                        concurrencyHandled.Value.Context,
                        concurrencyHandled.Value.Detail,
                        "high",
                        "compilation",
                        "efcore_optimistic_concurrency_catch"
                    )
                );
                break;
            }
        }

        return effect with
        {
            Observations = observations,
        };
    }

    private static (string Context, string Detail)? FindReadBeforeCommitContext(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        ReadBeforeCommitObservationRule rule
    )
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax commitAccess)
            return null;

        var name = commitAccess.Name.Identifier.ValueText;
        if (!rule.CommitMethods.Contains(name, StringComparer.Ordinal))
            return null;

        var methodBody = invocation
            .Ancestors()
            .FirstOrDefault(static a =>
                a
                    is MethodDeclarationSyntax
                        or LocalFunctionStatementSyntax
                        or AnonymousMethodExpressionSyntax
                        or SimpleLambdaExpressionSyntax
                        or ParenthesizedLambdaExpressionSyntax
            );

        if (methodBody is null)
            return null;

        var commitPos = invocation.SpanStart;
        foreach (var candidate in methodBody.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (candidate.SpanStart >= commitPos)
                continue;
            if (candidate.Expression is not MemberAccessExpressionSyntax readAccess)
                continue;
            if (!rule.ReadMethods.Contains(readAccess.Name.Identifier.ValueText, StringComparer.Ordinal))
                continue;

            var receiverFqn = semanticModel.GetTypeInfo(readAccess.Expression).Type?.OriginalDefinition.ToDisplayString() ?? "";
            if (!rule.ReadReceiverTypePatterns.Any(pattern => receiverFqn.Contains(pattern, StringComparison.Ordinal)))
                continue;

            var readLine = semanticModel.SyntaxTree.GetLineSpan(candidate.Span).StartLinePosition.Line + 1;
            return ("before_commit", $"line_{readLine}");
        }

        return null;
    }

    private static (string Context, string Detail)? FindConcurrencyHandlingContext(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        ConcurrencyHandledObservationRule rule
    )
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax commitAccess)
            return null;

        var name = commitAccess.Name.Identifier.ValueText;
        if (!rule.CommitMethods.Contains(name, StringComparer.Ordinal))
            return null;

        foreach (var ancestor in invocation.Ancestors().OfType<TryStatementSyntax>())
        {
            foreach (var catchClause in ancestor.Catches)
            {
                if (catchClause.Declaration is null)
                    continue;

                var catchTypeName = semanticModel.GetTypeInfo(catchClause.Declaration.Type).Type?.ToDisplayString() ?? "";
                var matched = rule.CatchTypePatterns.FirstOrDefault(pattern => catchTypeName.Contains(pattern, StringComparison.Ordinal));
                if (matched is not null)
                    return (matched, catchTypeName);
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
        SemanticModel semanticModel,
        ResilienceRetryObservationRule rule
    )
    {
        foreach (var ancestor in invocation.Ancestors().OfType<InvocationExpressionSyntax>())
        {
            if (ancestor.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var methodName = memberAccess.Name.Identifier.ValueText;
            if (!rule.WrapperMethods.Contains(methodName, StringComparer.Ordinal))
                continue;

            var receiverType = semanticModel.GetTypeInfo(memberAccess.Expression).Type;
            if (receiverType is null)
                continue;

            var receiverTypeName = receiverType.OriginalDefinition.ToDisplayString();
            var matched = rule.ReceiverTypePatterns.FirstOrDefault(pattern => receiverTypeName.Contains(pattern, StringComparison.Ordinal));
            if (matched is not null)
                return (matched, receiverTypeName);
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

            if (string.Equals(receiver, "Task", StringComparison.Ordinal) && string.Equals(methodName, "WhenAll", StringComparison.Ordinal))
            {
                return ("Task.WhenAll", "Task.WhenAll");
            }

            if (
                string.Equals(receiver, "Parallel", StringComparison.Ordinal)
                && (
                    string.Equals(methodName, "ForEach", StringComparison.Ordinal)
                    || string.Equals(methodName, "ForEachAsync", StringComparison.Ordinal)
                )
            )
            {
                return ($"Parallel.{methodName}", $"Parallel.{methodName}");
            }
        }

        return null;
    }
}
