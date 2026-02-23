using System.Collections.Generic;
using AsyncRewriter.Core;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// A <see cref="CSharpSyntaxRewriter"/> that transforms synchronous methods to async
/// using a Roslyn <see cref="SemanticModel"/> for symbol-based method identification.
/// <para>
/// Unlike the line-number-based <see cref="AsyncMethodRewriter"/>, this rewriter resolves
/// each method declaration and invocation to its symbol and looks it up in the call graph
/// by ID, making the transformation robust against line-number drift and ordering artefacts.
/// </para>
/// </summary>
public sealed class SemanticCallGraphRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;

    /// <summary>Transform metadata, keyed by call-graph method ID.</summary>
    private readonly IReadOnlyDictionary<string, MethodTransformInfo> _methodsById;

    /// <summary>
    /// For each flooded caller ID: the set of flooded callee IDs whose invocations
    /// from that caller need <c>await</c> added.
    /// </summary>
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _awaitableCalleesByCallerId;

    private readonly IReadOnlySet<string> _syncWrapperMethodIds;

    /// <summary>
    /// Scope stack.  Each frame holds the method ID of the nearest enclosing
    /// flooded method/local-function, or <c>null</c> when we are inside a
    /// non-flooded callable unit (local function that is not being transformed).
    /// Lambdas do <em>not</em> push a frame — they inherit the enclosing scope.
    /// </summary>
    private readonly Stack<string?> _scopeStack = new();

    private readonly List<MethodTransformation> _transformations = new();

    public bool AnyMethodTransformed => _transformations.Count > 0;
    public IReadOnlyList<MethodTransformation> Transformations => _transformations;

    public SemanticCallGraphRewriter(
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, MethodTransformInfo> methodsById,
        IReadOnlyDictionary<string, IReadOnlySet<string>> awaitableCalleesByCallerId,
        IReadOnlySet<string> syncWrapperMethodIds)
    {
        _semanticModel = semanticModel;
        _methodsById = methodsById;
        _awaitableCalleesByCallerId = awaitableCalleesByCallerId;
        _syncWrapperMethodIds = syncWrapperMethodIds;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Method declarations
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var result = (ClassDeclarationSyntax?)base.VisitClassDeclaration(node);
        // We need to update the baselist of classes, to modify any potential external interface implementations to their new async versions.
        // We only do this for classes that contain transformed methods.
        // Examples of such cases are IMapper<TSource, TDestination> to IMapper<Tsource, Task<TDestination>>. In this case there should be a method that has IsReturnType
        if (result is not null && _transformations.Any(t => t.IsReturnTypeParameter))
        {
            foreach (var _transformation in _transformations.Where(t => t.IsReturnTypeParameter))
            {
                // Update the baselist of the class declaration, replace any generic parameter matching the transformations original return type with the new async return type.
                var originalReturnType = _transformation.OriginalReturnType;
                var newReturnType = _transformation.NewReturnType;
                var newBaseList = node.BaseList?.WithTypes(SeparatedList(
                    node.BaseList.Types.Select(bt => bt is SimpleBaseTypeSyntax sbt
                        ? sbt.WithType(AsyncTransformHelpers.TransformTypeSyntax(sbt.Type, originalReturnType, newReturnType))
                        : bt)));

                if (newBaseList != null)
                {
                    result = result.WithBaseList(newBaseList);

                }
            }
        }

        return result;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var methodId = ResolveMethodId(node);
        var isFlooded = methodId != null && _methodsById.ContainsKey(methodId);

        _scopeStack.Push(isFlooded ? methodId : null);
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        _scopeStack.Pop();

        if (!isFlooded)
        {
            return visited;
        }

        return TransformMethodDeclaration(visited, node, _methodsById[methodId!]);
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var methodId = ResolveLocalFunctionId(node);
        var isFlooded = methodId != null && _methodsById.ContainsKey(methodId);

        _scopeStack.Push(isFlooded ? methodId : null);
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        _scopeStack.Pop();

        if (!isFlooded)
        {
            return visited;
        }

        return TransformLocalFunction(visited, node, _methodsById[methodId!]);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Lambda expressions — inherit enclosing scope, add async when body awaits
    // ──────────────────────────────────────────────────────────────────────────

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

    // ──────────────────────────────────────────────────────────────────────────
    // Invocation expressions — add await where the call graph says to
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        // We only add await when inside a flooded method scope.
        var currentCallerId = CurrentTransformingMethodId;
        if (currentCallerId == null)
        {
            return visited;
        }

        if (!_awaitableCalleesByCallerId.TryGetValue(currentCallerId, out var awaitableCallees))
        {
            return visited;
        }

        // Resolve the callee symbol.
        var calleeSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (calleeSymbol == null)
        {
            return visited;
        }

        var calleeId = MethodIdFactory.GetMethodId(calleeSymbol);
        if (!awaitableCallees.Contains(calleeId))
        {
            return visited;
        }

        // Do not double-await.
        if (visited.Parent is AwaitExpressionSyntax)
        {
            return visited;
        }

        // Determine what expression to await (unwrap sync wrappers if needed).
        ExpressionSyntax expressionToAwait = visited;
        if (_syncWrapperMethodIds.Contains(calleeId))
        {
            var unwrapped = AsyncTransformHelpers.TryUnwrapSyncWrapperCall(visited);
            if (unwrapped != null)
            {
                // Strip an inner await to avoid "await await …".
                expressionToAwait = unwrapped is AwaitExpressionSyntax inner
                    ? inner.Expression
                    : unwrapped;
            }
        }

        return BuildAwaitExpression(node, visited, expressionToAwait);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Private transformation helpers
    // ──────────────────────────────────────────────────────────────────────────

    private SyntaxNode TransformMethodDeclaration(
        MethodDeclarationSyntax visited,
        MethodDeclarationSyntax originalNode,
        MethodTransformInfo info)
    {
        // Gather which lines within this method have await-needing calls
        // (used only for the MethodTransformation output record).
        var awaitLines = CollectAwaitLines(info);

        // Out-parameter methods have a dedicated transformation path.
        if (info.OutParameterInfo != null)
        {
            visited = AsyncTransformHelpers.TransformOutParameterMethod(
                visited, info, awaitLines.Count > 0);

            if (info.NewMethodName != null)
            {
                visited = visited.WithIdentifier(
                    Identifier(info.NewMethodName).WithTriviaFrom(visited.Identifier));
            }

            if (info.DebugLines != null)
            {
                visited = AsyncTransformHelpers.PrependDebugComments(visited, info.DebugLines);
            }

            RecordTransformation(info, originalNode.ReturnType.ToString().Trim(),
                info.OutParameterInfo.NewAsyncReturnType, awaitLines);
            return visited;
        }

        var originalReturnType = originalNode.ReturnType.ToString().Trim();

        if (AsyncTransformHelpers.IsAlreadyTaskType(originalReturnType))
        {
            return visited;
        }

        var newReturnType = originalReturnType == "void"
            ? "Task"
            : $"Task<{originalReturnType}>";

        visited = visited.WithReturnType(
            ParseTypeName(newReturnType).WithTriviaFrom(visited.ReturnType));

        var isSyncWrapper = _syncWrapperMethodIds.Contains(info.MethodId);
        var hasAwaitableCalls = awaitLines.Count > 0;

        if (!isSyncWrapper)
        {
            if (hasAwaitableCalls
                && AsyncTransformHelpers.TryOptimizeDirectTaskReturn(visited, originalReturnType) is { } opt)
            {
                visited = opt;
            }
            else if (hasAwaitableCalls
                     && AsyncTransformHelpers.TryOptimizeExternalSyncWrapperUnwrap(
                         visited, originalReturnType) is { } unwrapped)
            {
                visited = unwrapped;
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
        }

        var effectiveName = info.MethodName;
        if (info.NewMethodName != null)
        {
            visited = visited.WithIdentifier(
                Identifier(info.NewMethodName).WithTriviaFrom(visited.Identifier));
            effectiveName = info.NewMethodName;
        }

        if (info.DebugLines != null)
        {
            visited = AsyncTransformHelpers.PrependDebugComments(visited, info.DebugLines);
        }

        RecordTransformation(info, originalReturnType, newReturnType, awaitLines,
            overrideName: effectiveName != info.MethodName ? effectiveName : null);
        return visited;
    }

    private SyntaxNode TransformLocalFunction(
        LocalFunctionStatementSyntax visited,
        LocalFunctionStatementSyntax originalNode,
        MethodTransformInfo info)
    {
        var awaitLines = CollectAwaitLines(info);
        var originalReturnType = originalNode.ReturnType.ToString().Trim();

        if (AsyncTransformHelpers.IsAlreadyTaskType(originalReturnType))
        {
            return visited;
        }

        var newReturnType = originalReturnType == "void"
            ? "Task"
            : $"Task<{originalReturnType}>";

        visited = visited.WithReturnType(
            ParseTypeName(newReturnType).WithTriviaFrom(visited.ReturnType));

        if (awaitLines.Count > 0)
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

        RecordTransformation(info, originalReturnType, newReturnType, awaitLines);
        return visited;
    }

    private static SyntaxNode BuildAwaitExpression(
        InvocationExpressionSyntax originalNode,
        InvocationExpressionSyntax visited,
        ExpressionSyntax expressionToAwait)
    {
        var leadingTrivia = visited.GetLeadingTrivia();

        // Parenthesise await in member-access chains to avoid parse ambiguity:
        // a.Method1().Method2() → (await a.Method1()).Method2()
        if (originalNode.Parent is MemberAccessExpressionSyntax
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

    // ──────────────────────────────────────────────────────────────────────────
    // Scope / bookkeeping helpers
    // ──────────────────────────────────────────────────────────────────────────

    private string? CurrentTransformingMethodId =>
        _scopeStack.Count > 0 ? _scopeStack.Peek() : null;

    private string? ResolveMethodId(MethodDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        return symbol != null ? MethodIdFactory.GetMethodId(symbol) : null;
    }

    private string? ResolveLocalFunctionId(LocalFunctionStatementSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        return symbol != null ? MethodIdFactory.GetMethodId(symbol) : null;
    }

    private List<int> CollectAwaitLines(MethodTransformInfo info)
    {
        // Collect the lines within this method that are in an awaitable callee set,
        // purely for the MethodTransformation output record.
        if (!_awaitableCalleesByCallerId.ContainsKey(info.MethodId))
        {
            return new List<int>();
        }

        // We return a non-empty list so callers know there are awaitable calls;
        // the actual line numbers in the output record are best-effort here
        // (the semantic rewriter doesn't track exact lines per call).
        return new List<int> { info.StartLine };
    }

    private void RecordTransformation(
        MethodTransformInfo info,
        string originalReturnType,
        string newReturnType,
        List<int> awaitLines,
        string? overrideName = null)
    {
        var effectiveName = overrideName ?? info.NewMethodName ?? info.MethodName;
        _transformations.Add(new MethodTransformation
        {
            MethodName = effectiveName,
            MethodSignature = $"{info.ContainingType}.{effectiveName}",
            StartLine = info.StartLine,
            EndLine = info.EndLine,
            OriginalReturnType = originalReturnType,
            IsReturnTypeParameter = info.IsReturnTypeParameter,
            NewReturnType = newReturnType,
            AwaitAddedAtLines = awaitLines
        });
    }
}
