using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Roslyn syntax rewriter that transforms synchronous methods to async
/// </summary>
public class AsyncMethodRewriter : CSharpSyntaxRewriter
{
    private readonly SemanticModel _semanticModel;
    private readonly HashSet<string> _methodsToTransform;
    private readonly HashSet<string> _asyncMethodIds;
    private readonly HashSet<string> _syncWrapperMethodIds;
    private readonly List<int> _awaitAddedLines = new();

    public IReadOnlyList<int> AwaitAddedLines => _awaitAddedLines;

    public AsyncMethodRewriter(
        SemanticModel semanticModel,
        HashSet<string> methodsToTransform,
        HashSet<string> asyncMethodIds,
        HashSet<string>? syncWrapperMethodIds = null)
    {
        _semanticModel = semanticModel;
        _methodsToTransform = methodsToTransform;
        _asyncMethodIds = asyncMethodIds;
        _syncWrapperMethodIds = syncWrapperMethodIds ?? new HashSet<string>();
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node);
        if (methodSymbol == null)
            return base.VisitMethodDeclaration(node);

        var methodId = GetMethodId(methodSymbol);

        // If this method needs to be transformed to async
        if (_methodsToTransform.Contains(methodId) && !methodSymbol.IsAsync)
        {
            // Check if this is an interface method (no body, no expression body)
            var isInterfaceMethod = methodSymbol.ContainingType?.TypeKind == TypeKind.Interface;

            if (isInterfaceMethod)
            {
                // Interface methods only need return type change, no async keyword
                var newReturnType = TransformReturnType(node.ReturnType, methodSymbol.ReturnType);
                return node.WithReturnType(newReturnType);
            }

            // Add async modifier for non-interface methods
            var asyncModifier = SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);

            var newModifiers = node.Modifiers.Add(asyncModifier);

            // Transform return type
            var newReturnType2 = TransformReturnType(node.ReturnType, methodSymbol.ReturnType);

            // Visit method body to add await keywords
            var newBody = (BlockSyntax?)Visit(node.Body);
            var newExpressionBody = (ArrowExpressionClauseSyntax?)Visit(node.ExpressionBody);

            return node
                .WithModifiers(newModifiers)
                .WithReturnType(newReturnType2)
                .WithBody(newBody)
                .WithExpressionBody(newExpressionBody);
        }

        return base.VisitMethodDeclaration(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(node);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

        if (methodSymbol != null)
        {
            var methodId = GetMethodId(methodSymbol);

            // Check if this is a sync wrapper call that should be unwrapped
            if (_syncWrapperMethodIds.Contains(methodId))
            {
                var unwrapped = TryUnwrapSyncWrapperCall(node);
                if (unwrapped != null)
                {
                    return unwrapped;
                }
            }

            // If this invocation calls an async method or a method that will be async
            if (methodSymbol.IsAsync || _asyncMethodIds.Contains(methodId))
            {
                // Check if already awaited
                var parent = node.Parent;
                if (parent is AwaitExpressionSyntax)
                {
                    return base.VisitInvocationExpression(node);
                }

                // Add await - preserve leading trivia from the invocation
                var leadingTrivia = node.GetLeadingTrivia();
                var nodeWithoutLeadingTrivia = node.WithoutLeadingTrivia();

                var awaitExpression = SyntaxFactory.AwaitExpression(nodeWithoutLeadingTrivia)
                    .WithAwaitKeyword(
                        SyntaxFactory.Token(SyntaxKind.AwaitKeyword)
                            .WithLeadingTrivia(leadingTrivia)
                            .WithTrailingTrivia(SyntaxFactory.Space));

                // Track line number
                var lineSpan = node.GetLocation().GetLineSpan();
                _awaitAddedLines.Add(lineSpan.StartLinePosition.Line + 1);

                return awaitExpression;
            }
        }

        return base.VisitInvocationExpression(node);
    }

    /// <summary>
    /// Attempts to unwrap a sync wrapper call like AsyncHelper.RunSync(() => SomeAsyncMethod())
    /// into await SomeAsyncMethod()
    /// </summary>
    private SyntaxNode? TryUnwrapSyncWrapperCall(InvocationExpressionSyntax node)
    {
        // Find the lambda/delegate argument
        var arguments = node.ArgumentList.Arguments;
        if (arguments.Count == 0)
            return null;

        // The first argument should be the Func<Task> or Func<Task<T>>
        var funcArgument = arguments[0].Expression;

        ExpressionSyntax? asyncCallExpression = null;

        // Handle lambda expression: () => SomeAsyncMethod()
        if (funcArgument is ParenthesizedLambdaExpressionSyntax lambda)
        {
            if (lambda.ExpressionBody != null)
            {
                // Simple expression body: () => GetDataAsync()
                asyncCallExpression = lambda.ExpressionBody;
            }
            else if (lambda.Block != null)
            {
                // Block body: () => { return GetDataAsync(); }
                var returnStatement = lambda.Block.Statements
                    .OfType<ReturnStatementSyntax>()
                    .FirstOrDefault();
                if (returnStatement?.Expression != null)
                {
                    asyncCallExpression = returnStatement.Expression;
                }
            }
        }
        // Handle simple lambda: x => SomeAsyncMethod(x)
        else if (funcArgument is SimpleLambdaExpressionSyntax simpleLambda)
        {
            if (simpleLambda.ExpressionBody != null)
            {
                asyncCallExpression = simpleLambda.ExpressionBody;
            }
        }

        if (asyncCallExpression == null)
            return null;

        // Preserve leading trivia from the original call
        var leadingTrivia = node.GetLeadingTrivia();

        // Create await expression for the unwrapped async call
        var awaitExpression = SyntaxFactory.AwaitExpression(
                asyncCallExpression.WithoutLeadingTrivia())
            .WithAwaitKeyword(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword)
                    .WithLeadingTrivia(leadingTrivia)
                    .WithTrailingTrivia(SyntaxFactory.Space));

        // Track line number
        var lineSpan = node.GetLocation().GetLineSpan();
        _awaitAddedLines.Add(lineSpan.StartLinePosition.Line + 1);

        return awaitExpression;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol == null)
            return base.VisitLocalFunctionStatement(node);

        var methodId = GetMethodId(methodSymbol);

        // If this local function needs to be transformed to async
        if (_methodsToTransform.Contains(methodId) && !methodSymbol.IsAsync)
        {
            var asyncModifier = SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                .WithTrailingTrivia(SyntaxFactory.Space);

            var newModifiers = node.Modifiers.Add(asyncModifier);
            var newReturnType = TransformReturnType(node.ReturnType, methodSymbol.ReturnType);

            var newBody = (BlockSyntax?)Visit(node.Body);
            var newExpressionBody = (ArrowExpressionClauseSyntax?)Visit(node.ExpressionBody);

            return node
                .WithModifiers(newModifiers)
                .WithReturnType(newReturnType)
                .WithBody(newBody)
                .WithExpressionBody(newExpressionBody);
        }

        return base.VisitLocalFunctionStatement(node);
    }

    private TypeSyntax TransformReturnType(TypeSyntax originalType, ITypeSymbol typeSymbol)
    {
        var typeString = typeSymbol.ToDisplayString();

        // If already Task or Task<T>, keep it
        if (typeString.StartsWith("System.Threading.Tasks.Task"))
        {
            return originalType;
        }

        // Preserve leading and trailing trivia from original type
        var leadingTrivia = originalType.GetLeadingTrivia();
        var trailingTrivia = originalType.GetTrailingTrivia();

        // If void, return Task
        if (typeString == "void")
        {
            return SyntaxFactory.ParseTypeName("Task")
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(trailingTrivia);
        }

        // Otherwise wrap in Task<T>
        var taskType = SyntaxFactory.GenericName(
            SyntaxFactory.Identifier("Task"),
            SyntaxFactory.TypeArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    originalType.WithoutLeadingTrivia().WithoutTrailingTrivia())));

        return taskType
            .WithLeadingTrivia(leadingTrivia)
            .WithTrailingTrivia(trailingTrivia);
    }

    private string GetMethodId(IMethodSymbol methodSymbol)
    {
        // Use OriginalDefinition and MinimallyQualifiedFormat to match the analyzer's format
        var originalMethod = methodSymbol.OriginalDefinition;
        var parameters = string.Join(", ", originalMethod.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        var signature = $"{originalMethod.Name}({parameters})";
        return $"{originalMethod.ContainingType?.ToDisplayString()}.{signature}";
    }
}
