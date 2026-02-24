using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Adds missing <c>await</c> keywords to invocations that return <c>Task</c> or
/// <c>Task&lt;T&gt;</c> (or their <c>ValueTask</c> equivalents) but are not currently awaited.
/// <para>
/// The rewriter requires a Roslyn <see cref="SemanticModel"/> to determine the return type
/// of each invocation.  Invocations that are already awaited, or that are the direct
/// expression of a <c>return</c> statement (direct-return / passthrough pattern), are
/// left unchanged.
/// </para>
/// <para>
/// After adding <c>await</c> to an invocation, the rewriter also ensures that any
/// enclosing method or lambda that now contains an <c>await</c> carries the
/// <c>async</c> keyword.
/// </para>
/// <para>
/// Chain continuations are parenthesized automatically:
/// <code>
/// repo.GetAsync().Name   →   (await repo.GetAsync()).Name
/// </code>
/// </para>
/// </summary>
public sealed class MissingAwaitRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;

    public bool AnyRewritten { get; private set; }

    public MissingAwaitRewriter(SemanticModel semanticModel)
    {
        _semanticModel = semanticModel;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Method declarations — add async modifier when body now contains awaits
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        if (!visited.Modifiers.Any(SyntaxKind.AsyncKeyword)
            && AsyncTransformHelpers.ContainsDirectAwait(visited.Body ?? (SyntaxNode?)visited.ExpressionBody))
        {
            AnyRewritten = true;
            return AsyncTransformHelpers.AddAsyncModifier(visited);
        }

        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;

        if (!visited.Modifiers.Any(SyntaxKind.AsyncKeyword)
            && AsyncTransformHelpers.ContainsDirectAwait(visited.Body ?? (SyntaxNode?)visited.ExpressionBody))
        {
            AnyRewritten = true;
            return AsyncTransformHelpers.AddAsyncModifierToLocalFunction(visited);
        }

        return visited;
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;

        if (!visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
            && AsyncTransformHelpers.ContainsDirectAwait(visited.Body))
        {
            AnyRewritten = true;
            return visited
                .WithAsyncKeyword(Token(SyntaxKind.AsyncKeyword)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                    .WithTrailingTrivia(Space))
                .WithParameter(visited.Parameter.WithoutLeadingTrivia());
        }

        return visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;

        if (!visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)
            && AsyncTransformHelpers.ContainsDirectAwait(visited.Body))
        {
            AnyRewritten = true;
            return visited
                .WithAsyncKeyword(Token(SyntaxKind.AsyncKeyword)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                    .WithTrailingTrivia(Space))
                .WithParameterList(visited.ParameterList.WithoutLeadingTrivia());
        }

        return visited;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Invocations — add await where the return type is Task/ValueTask
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Visit children first (bottom-up) so chained calls are handled inside-out.
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        // Already awaited — nothing to do.
        if (node.Parent is AwaitExpressionSyntax)
        {
            return visited;
        }

        // Direct-return passthrough: `return Foo();`
        // Leave these alone — the caller can choose to return the Task directly.
        if (node.Parent is ReturnStatementSyntax)
        {
            return visited;
        }

        // Check the return type using the original node (semantic model is bound to original tree).
        var typeInfo = _semanticModel.GetTypeInfo(node);
        if (!IsTaskLike(typeInfo.Type))
        {
            return visited;
        }

        AnyRewritten = true;
        var leadingTrivia = visited.GetLeadingTrivia();

        // Parenthesize when used as a chain receiver:
        //   repo.GetAsync().Name  →  (await repo.GetAsync()).Name
        if (node.Parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
        {
            var awaitExpr = AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                visited.WithoutLeadingTrivia());
            return ParenthesizedExpression(awaitExpr).WithLeadingTrivia(leadingTrivia);
        }

        // Conditional-access receiver: `obj?.GetAsync()` — await the whole expression;
        // handled by VisitConditionalAccessExpression instead, so skip here.
        if (node.Expression is MemberBindingExpressionSyntax)
        {
            return visited;
        }

        return AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                visited.WithoutLeadingTrivia())
            .WithLeadingTrivia(leadingTrivia);
    }

    public override SyntaxNode? VisitConditionalAccessExpression(ConditionalAccessExpressionSyntax node)
    {
        var visited = (ConditionalAccessExpressionSyntax)base.VisitConditionalAccessExpression(node)!;

        if (node.WhenNotNull is not InvocationExpressionSyntax originalInvocation)
        {
            return visited;
        }

        if (node.Parent is AwaitExpressionSyntax)
        {
            return visited;
        }

        if (node.Parent is ReturnStatementSyntax)
        {
            return visited;
        }

        var typeInfo = _semanticModel.GetTypeInfo(originalInvocation);
        if (!IsTaskLike(typeInfo.Type))
        {
            return visited;
        }

        AnyRewritten = true;
        var leadingTrivia = visited.GetLeadingTrivia();

        var awaitExpr = AwaitExpression(
            Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
            visited.WithoutLeadingTrivia());

        if (node.Parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
        {
            return ParenthesizedExpression(awaitExpr).WithLeadingTrivia(leadingTrivia);
        }

        return awaitExpr.WithLeadingTrivia(leadingTrivia);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helper
    // ──────────────────────────────────────────────────────────────────────────

    private static bool IsTaskLike(ITypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        var name = type is INamedTypeSymbol { IsGenericType: true } generic
            ? generic.ConstructedFrom.ToDisplayString()
            : type.ToDisplayString();

        return name is
            "System.Threading.Tasks.Task" or
            "System.Threading.Tasks.Task<TResult>" or
            "System.Threading.Tasks.ValueTask" or
            "System.Threading.Tasks.ValueTask<TResult>";
    }
}
