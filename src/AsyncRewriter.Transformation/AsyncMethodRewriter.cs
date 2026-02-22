using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AsyncRewriter.Core.Models;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Roslyn syntax rewriter that transforms synchronous methods to async.
/// Matches methods by start line and call sites by line number.
/// Delegates all syntax-transformation logic to <see cref="AsyncTransformHelpers"/>.
/// </summary>
public class AsyncMethodRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<int, MethodTransformInfo> _methodsByStartLine;
    private readonly Dictionary<int, CallSiteInfo> _callSitesByLine;
    private readonly HashSet<string> _syncWrapperMethodIds;
    private readonly HashSet<string> _allAsyncMethodIds;
    private readonly List<MethodTransformation> _transformations = new();
    private bool _anyMethodTransformed;

    public IReadOnlyList<MethodTransformation> Transformations => _transformations;
    public bool AnyMethodTransformed => _anyMethodTransformed;

    public AsyncMethodRewriter(
        Dictionary<int, MethodTransformInfo> methodsByStartLine,
        Dictionary<int, CallSiteInfo> callSitesByLine,
        HashSet<string>? syncWrapperMethodIds = null,
        HashSet<string>? allAsyncMethodIds = null)
    {
        _methodsByStartLine = methodsByStartLine;
        _callSitesByLine = callSitesByLine;
        _syncWrapperMethodIds = syncWrapperMethodIds ?? new HashSet<string>();
        _allAsyncMethodIds = allAsyncMethodIds ?? new HashSet<string>();
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var startLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1; // 1-based

        if (!_methodsByStartLine.TryGetValue(startLine, out var info))
        {
            return base.VisitMethodDeclaration(node);
        }

        var awaitLines = CollectAwaitLines(info);
        var hasAwaitableCalls = awaitLines.Count > 0;
        var isSyncWrapper = _syncWrapperMethodIds.Contains(info.MethodId);

        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        // Out-parameter transformation path
        if (info.OutParameterInfo != null)
        {
            visited = AsyncTransformHelpers.TransformOutParameterMethod(visited, info, hasAwaitableCalls);

            if (info.NewMethodName != null)
            {
                visited = visited.WithIdentifier(
                    Identifier(info.NewMethodName).WithTriviaFrom(visited.Identifier));
            }

            if (info.DebugLines != null)
            {
                visited = AsyncTransformHelpers.PrependDebugComments(visited, info.DebugLines);
            }

            _anyMethodTransformed = true;
            var effectiveName = info.NewMethodName ?? info.MethodName;
            _transformations.Add(new MethodTransformation
            {
                MethodName = effectiveName,
                MethodSignature = $"{info.ContainingType}.{effectiveName}",
                StartLine = info.StartLine,
                EndLine = info.EndLine,
                OriginalReturnType = node.ReturnType.ToString().Trim(),
                NewReturnType = info.OutParameterInfo.NewAsyncReturnType,
                AwaitAddedAtLines = awaitLines
            });
            return visited;
        }

        var originalReturnType = node.ReturnType.ToString().Trim();

        if (AsyncTransformHelpers.IsAlreadyTaskType(originalReturnType))
        {
            return base.VisitMethodDeclaration(node);
        }

        var newReturnType = originalReturnType == "void" ? "Task" : $"Task<{originalReturnType}>";
        visited = visited.WithReturnType(
            ParseTypeName(newReturnType).WithTriviaFrom(visited.ReturnType));

        if (isSyncWrapper)
        {
            // Sync wrappers: change return type only; body rewritten elsewhere.
        }
        else if (hasAwaitableCalls
                 && AsyncTransformHelpers.TryOptimizeDirectTaskReturn(visited, originalReturnType) is { } optimized)
        {
            visited = optimized;
        }
        else if (hasAwaitableCalls
                 && AsyncTransformHelpers.TryOptimizeExternalSyncWrapperUnwrap(
                     visited, originalReturnType) is { } syncUnwrapped)
        {
            visited = syncUnwrapped;
        }
        else if (hasAwaitableCalls)
        {
            visited = AsyncTransformHelpers.AddAsyncModifier(visited);
        }
        else
        {
            visited = AsyncTransformHelpers.TransformBodyForNoAwait(
                visited, originalReturnType, newReturnType);
        }

        var effectiveMethodName = info.MethodName;
        if (info.NewMethodName != null)
        {
            visited = visited.WithIdentifier(
                Identifier(info.NewMethodName).WithTriviaFrom(visited.Identifier));
            effectiveMethodName = info.NewMethodName;
        }

        if (info.DebugLines != null)
        {
            visited = AsyncTransformHelpers.PrependDebugComments(visited, info.DebugLines);
        }

        _anyMethodTransformed = true;
        _transformations.Add(new MethodTransformation
        {
            MethodName = effectiveMethodName,
            MethodSignature = $"{info.ContainingType}.{effectiveMethodName}",
            StartLine = info.StartLine,
            EndLine = info.EndLine,
            OriginalReturnType = originalReturnType,
            NewReturnType = newReturnType,
            AwaitAddedAtLines = awaitLines
        });

        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var startLine = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

        if (!_methodsByStartLine.TryGetValue(startLine, out var info))
        {
            return base.VisitLocalFunctionStatement(node);
        }

        var awaitLines = CollectAwaitLines(info);
        var hasAwaitableCalls = awaitLines.Count > 0;

        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;

        var originalReturnType = node.ReturnType.ToString().Trim();

        if (AsyncTransformHelpers.IsAlreadyTaskType(originalReturnType))
        {
            return base.VisitLocalFunctionStatement(node);
        }

        var newReturnType = originalReturnType == "void" ? "Task" : $"Task<{originalReturnType}>";
        visited = visited.WithReturnType(
            ParseTypeName(newReturnType).WithTriviaFrom(visited.ReturnType));

        if (hasAwaitableCalls)
        {
            visited = AsyncTransformHelpers.AddAsyncModifierToLocalFunction(visited);
        }
        else
        {
            visited = AsyncTransformHelpers.TransformLocalFunctionBodyForNoAwait(
                visited, originalReturnType, newReturnType);
        }

        if (info.DebugLines != null)
        {
            visited = AsyncTransformHelpers.PrependDebugComments(visited, info.DebugLines);
        }

        _anyMethodTransformed = true;
        _transformations.Add(new MethodTransformation
        {
            MethodName = info.MethodName,
            MethodSignature = $"{info.ContainingType}.{info.MethodName}",
            StartLine = info.StartLine,
            EndLine = info.EndLine,
            OriginalReturnType = originalReturnType,
            NewReturnType = newReturnType,
            AwaitAddedAtLines = awaitLines
        });

        return visited;
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;

        if (!visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
            && AsyncTransformHelpers.ContainsDirectAwait(visited.Body))
        {
            return visited
                .WithAsyncKeyword(Token(SyntaxKind.AsyncKeyword)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                    .WithTrailingTrivia(Space))
                .WithParameter(visited.Parameter.WithoutLeadingTrivia());
        }

        return visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(
        ParenthesizedLambdaExpressionSyntax node)
    {
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;

        if (!visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
            && AsyncTransformHelpers.ContainsDirectAwait(visited.Body))
        {
            return visited
                .WithAsyncKeyword(Token(SyntaxKind.AsyncKeyword)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                    .WithTrailingTrivia(Space))
                .WithParameterList(visited.ParameterList.WithoutLeadingTrivia());
        }

        return visited;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
        if (!_callSitesByLine.TryGetValue(line, out var callSiteInfo))
        {
            return visited;
        }

        if (callSiteInfo.CalleeMethodName is not null)
        {
            var invokedName = AsyncTransformHelpers.GetInvokedMethodName(node);
            if (invokedName != null && invokedName != callSiteInfo.CalleeMethodName)
            {
                return visited;
            }
        }

        if (visited.Parent is AwaitExpressionSyntax)
        {
            return visited;
        }

        ExpressionSyntax expressionToAwait = visited;
        if (_syncWrapperMethodIds.Contains(callSiteInfo.CalleeMethodId))
        {
            var unwrapped = AsyncTransformHelpers.TryUnwrapSyncWrapperCall(visited);
            if (unwrapped != null)
            {
                expressionToAwait = unwrapped is AwaitExpressionSyntax innerAwait
                    ? innerAwait.Expression
                    : unwrapped;
            }
        }

        var leadingTrivia = visited.GetLeadingTrivia();

        if (node.Parent is MemberAccessExpressionSyntax
            or ElementAccessExpressionSyntax
            or ConditionalAccessExpressionSyntax)
        {
            var trailingTrivia = visited.GetTrailingTrivia();
            var stripped = expressionToAwait.WithoutLeadingTrivia().WithoutTrailingTrivia();
            var awaitExpr = AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                stripped);
            return ParenthesizedExpression(awaitExpr)
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(trailingTrivia);
        }

        return AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                expressionToAwait.WithoutLeadingTrivia())
            .WithLeadingTrivia(leadingTrivia);
    }

    private List<int> CollectAwaitLines(MethodTransformInfo info)
    {
        var result = new List<int>();
        foreach (var (line, _) in _callSitesByLine)
        {
            if (line >= info.StartLine && line <= info.EndLine)
            {
                result.Add(line);
            }
        }
        return result;
    }
}

/// <summary>Info about a method that needs transformation, keyed by start line.</summary>
public class MethodTransformInfo
{
    public required string MethodId { get; init; }
    public required string MethodName { get; init; }
    public required string ContainingType { get; init; }
    public required string OriginalReturnType { get; init; }
    public required string NewReturnType { get; init; }
    public required int StartLine { get; init; }
    public required int EndLine { get; init; }
    public List<string>? DebugLines { get; init; }
    public string? NewMethodName { get; init; }
    /// <summary>Out-parameter transformation info. Null if method has no out parameters.</summary>
    public OutParameterTransformInfo? OutParameterInfo { get; init; }
}

/// <summary>Describes how out parameters should be transformed for an async method.</summary>
public class OutParameterTransformInfo
{
    public required bool IsTryPattern { get; init; }
    public required List<int> OutParameterIndices { get; init; }
    public required List<string> OutParameterTypes { get; init; }
    public required List<string> OutParameterNames { get; init; }
    public required string NewAsyncReturnType { get; init; }
}

/// <summary>Info about a call site that needs await, keyed by line number.</summary>
public class CallSiteInfo
{
    public required string CalleeMethodId { get; init; }
    public required int LineNumber { get; init; }

    /// <summary>
    /// The simple method name of the callee (e.g., "Configure" for "IBuilder.Configure()").
    /// Used to disambiguate when multiple invocations appear on the same line in a method chain.
    /// </summary>
    public string? CalleeMethodName { get; init; }
}
