using System.Collections.Generic;
using System;
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

        var explicitSpecifierOverride = UpdateExplicitInterfaceSpecifier(node, methodSymbol);
        if (explicitSpecifierOverride != null)
        {
            node = node.WithExplicitInterfaceSpecifier(explicitSpecifierOverride);
        }

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

            var interfaceReturnTypeOverride = GetInterfaceReturnTypeOverride(methodSymbol);
            var genericInterfaceOverride = GetGenericInterfaceTaskReturnOverride(methodSymbol);

            // Check if the method body actually contains calls that need await
            var needsAwait = MethodBodyNeedsAwait(node);
            var returnsVoid = methodSymbol.ReturnType.SpecialType == SpecialType.System_Void;

            // Transform return type
            var newReturnType2 = interfaceReturnTypeOverride != null
                ? TransformReturnType(node.ReturnType, interfaceReturnTypeOverride)
                : genericInterfaceOverride ?? TransformReturnType(node.ReturnType, methodSymbol.ReturnType);

            if (needsAwait)
            {
                // Check if we can directly return the task instead of using async/await
                var directReturnTransform = TryTransformToDirectTaskReturn(node, returnsVoid);
                if (directReturnTransform != null)
                {
                    return directReturnTransform.WithReturnType(newReturnType2);
                }

                // Method has async calls - add async modifier and awaits
                var asyncModifier = SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space);

                var newModifiers = node.Modifiers.Add(asyncModifier);

                // Visit method body to add await keywords
                var newBody = (BlockSyntax?)Visit(node.Body);
                var newExpressionBody = (ArrowExpressionClauseSyntax?)Visit(node.ExpressionBody);

                return node
                    .WithModifiers(newModifiers)
                    .WithReturnType(newReturnType2)
                    .WithBody(newBody)
                    .WithExpressionBody(newExpressionBody);
            }
            else
            {
                // Method has no async calls - use Task.FromResult/Task.CompletedTask instead
                var newBody = TransformBodyForTaskFromResult(node.Body, returnsVoid, node.ReturnType);
                var newExpressionBody = TransformExpressionBodyForTaskFromResult(node.ExpressionBody, returnsVoid, node.ReturnType);

                return node
                    .WithReturnType(newReturnType2)
                    .WithBody(newBody)
                    .WithExpressionBody(newExpressionBody);
            }
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

            var throwAsyncInvocation = TryTransformThrowToThrowAsync(node, methodSymbol);
            if (throwAsyncInvocation != null)
            {
                if (throwAsyncInvocation.Parent is AwaitExpressionSyntax)
                {
                    return throwAsyncInvocation;
                }

                return WrapWithAwait(throwAsyncInvocation);
            }

            var notThrowAsyncInvocation = TryTransformNotThrowToNotThrowAsync(node, methodSymbol);
            if (notThrowAsyncInvocation != null)
            {
                if (notThrowAsyncInvocation.Parent is AwaitExpressionSyntax)
                {
                    return notThrowAsyncInvocation;
                }

                return WrapWithAwait(notThrowAsyncInvocation);
            }

            if (IsFluentAssertionAsyncInvocation(methodSymbol) && node.Parent is not AwaitExpressionSyntax)
            {
                return WrapWithAwait(node);
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

                return WrapWithAwait(node);
            }
        }

        return base.VisitInvocationExpression(node);
    }

    private InvocationExpressionSyntax? TryTransformThrowToThrowAsync(InvocationExpressionSyntax node, IMethodSymbol methodSymbol)
    {
        if (!IsAssertionThrow(methodSymbol))
        {
            return null;
        }

        return TryTransformAssertion(node, "ThrowAsync");
    }

    private InvocationExpressionSyntax? TryTransformNotThrowToNotThrowAsync(InvocationExpressionSyntax node, IMethodSymbol methodSymbol)
    {
        if (!IsAssertionNotThrow(methodSymbol))
        {
            return null;
        }

        return TryTransformAssertion(node, "NotThrowAsync");
    }

    private InvocationExpressionSyntax? TryTransformAssertion(InvocationExpressionSyntax node, string asyncMethodName)
    {
        if (!ShouldUseAsyncAssertion(node))
        {
            return null;
        }

        var newExpression = node.Expression;

        if (newExpression is MemberAccessExpressionSyntax memberAccess)
        {
            newExpression = memberAccess.WithName(SyntaxFactory.IdentifierName(asyncMethodName));
        }
        else if (newExpression is MemberBindingExpressionSyntax memberBinding)
        {
            newExpression = memberBinding.WithName(SyntaxFactory.IdentifierName(asyncMethodName));
        }

        return node.WithExpression(newExpression);
    }

    private bool ShouldUseAsyncAssertion(InvocationExpressionSyntax node)
    {
        var subject = GetAssertionSubject(node.Expression);
        if (subject == null)
        {
            return false;
        }

        var subjectType = _semanticModel.GetTypeInfo(subject).Type;
        if (IsAsyncDelegateType(subjectType))
        {
            return true;
        }

        if (subject is LambdaExpressionSyntax lambda)
        {
            return LambdaContainsAsyncCall(lambda);
        }

        if (subject is IdentifierNameSyntax identifier)
        {
            var symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
            return SymbolRepresentsAsyncDelegate(symbol);
        }

        return false;
    }

    private ExpressionSyntax? GetAssertionSubject(ExpressionSyntax expression)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is InvocationExpressionSyntax invocation && IsShouldInvocation(invocation))
            {
                return GetShouldReceiver(invocation.Expression);
            }
        }
        else if (expression is MemberBindingExpressionSyntax &&
                 expression.Parent is ConditionalAccessExpressionSyntax conditionalAccess &&
                 conditionalAccess.Expression is InvocationExpressionSyntax invocation &&
                 IsShouldInvocation(invocation))
        {
            return GetShouldReceiver(invocation.Expression);
        }

        return null;
    }

    private bool IsShouldInvocation(InvocationExpressionSyntax invocation)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Name.Identifier.Text == "Should";
        }

        if (invocation.Expression is MemberBindingExpressionSyntax memberBinding)
        {
            return memberBinding.Name.Identifier.Text == "Should";
        }

        return false;
    }

    private ExpressionSyntax? GetShouldReceiver(ExpressionSyntax shouldExpression)
    {
        if (shouldExpression is MemberAccessExpressionSyntax memberAccess)
        {
            return memberAccess.Expression;
        }

        if (shouldExpression is MemberBindingExpressionSyntax &&
            shouldExpression.Parent is ConditionalAccessExpressionSyntax conditionalAccess)
        {
            return conditionalAccess.Expression;
        }

        return null;
    }

    private bool SymbolRepresentsAsyncDelegate(ISymbol? symbol)
    {
        return symbol switch
        {
            ILocalSymbol local => IsAsyncDelegateType(local.Type) || LambdaInitializerContainsAsyncCall(local.DeclaringSyntaxReferences),
            IParameterSymbol parameter => IsAsyncDelegateType(parameter.Type),
            IFieldSymbol field => IsAsyncDelegateType(field.Type) || LambdaInitializerContainsAsyncCall(field.DeclaringSyntaxReferences),
            _ => false
        };
    }

    private bool LambdaInitializerContainsAsyncCall(IEnumerable<SyntaxReference> references)
    {
        foreach (var reference in references)
        {
            if (reference.GetSyntax() is VariableDeclaratorSyntax declarator &&
                declarator.Initializer?.Value is LambdaExpressionSyntax lambda &&
                LambdaContainsAsyncCall(lambda))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAsyncDelegateType(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is not INamedTypeSymbol namedType || namedType.Name != "Func")
        {
            return false;
        }

        if (namedType.TypeArguments.Length == 0)
        {
            return false;
        }

        var returnType = namedType.TypeArguments[^1];
        return IsTaskType(returnType);
    }

    private bool IsTaskType(ITypeSymbol typeSymbol)
    {
        return typeSymbol.ToDisplayString().StartsWith("System.Threading.Tasks.Task", StringComparison.Ordinal);
    }

    private bool LambdaContainsAsyncCall(LambdaExpressionSyntax lambda)
    {
        var invocations = lambda.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol == null)
            {
                continue;
            }

            var methodId = GetMethodId(methodSymbol);
            if (methodSymbol.IsAsync || _asyncMethodIds.Contains(methodId))
            {
                return true;
            }

            if (_syncWrapperMethodIds.Contains(methodId))
            {
                return true;
            }
        }

        return false;
    }

    private bool IsAssertionThrow(IMethodSymbol methodSymbol)
    {
        return methodSymbol.Name == "Throw" && IsAssertionType(methodSymbol.ContainingType);
    }

    private bool IsAssertionNotThrow(IMethodSymbol methodSymbol)
    {
        return methodSymbol.Name == "NotThrow" && IsAssertionType(methodSymbol.ContainingType);
    }

    private bool IsFluentAssertionAsyncInvocation(IMethodSymbol methodSymbol)
    {
        return IsAssertionType(methodSymbol.ContainingType) &&
            methodSymbol.Name.EndsWith("Async", StringComparison.Ordinal);
    }

    private bool IsAssertionType(INamedTypeSymbol? typeSymbol)
    {
        if (typeSymbol == null)
        {
            return false;
        }

        return typeSymbol.Name is "DelegateAssertions" or "ActionAssertions";
    }

    private InvocationExpressionSyntax? GetThrowTargetExpression(ExpressionSyntax expression)
    {
        if (expression is MemberAccessExpressionSyntax memberAccess)
        {
            if (memberAccess.Expression is InvocationExpressionSyntax invocation)
            {
                return invocation;
            }
        }
        else if (expression is MemberBindingExpressionSyntax memberBinding)
        {
            if (memberBinding.Parent is ConditionalAccessExpressionSyntax conditionalAccess &&
                conditionalAccess.Expression is InvocationExpressionSyntax invocation)
            {
                return invocation;
            }
        }

        return null;
    }

    private ExpressionSyntax WrapInvocationInAsyncLambda(InvocationExpressionSyntax invocation)
    {
        var awaitExpression = SyntaxFactory.AwaitExpression(invocation.WithoutLeadingTrivia());
        var block = SyntaxFactory.Block(
            SyntaxFactory.ExpressionStatement(awaitExpression));

        return SyntaxFactory.ParenthesizedLambdaExpression(block)
            .WithAsyncKeyword(SyntaxFactory.Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(SyntaxFactory.Space));
    }

    private SyntaxNode WrapWithAwait(InvocationExpressionSyntax node)
    {
        var leadingTrivia = node.GetLeadingTrivia();
        var nodeWithoutLeadingTrivia = node.WithoutLeadingTrivia();

        var awaitExpression = SyntaxFactory.AwaitExpression(nodeWithoutLeadingTrivia)
            .WithAwaitKeyword(
                SyntaxFactory.Token(SyntaxKind.AwaitKeyword)
                    .WithLeadingTrivia(leadingTrivia)
                    .WithTrailingTrivia(SyntaxFactory.Space));

        var lineSpan = node.GetLocation().GetLineSpan();
        _awaitAddedLines.Add(lineSpan.StartLinePosition.Line + 1);

        return awaitExpression;
    }

    /// <summary>
    /// Attempts to unwrap a sync wrapper call like AsyncHelper.RunSync(() => SomeAsyncMethod())
    /// into await SomeAsyncMethod()
    /// </summary>
    private bool LambdaHasAwait(LambdaExpressionSyntax node)
    {
        if (node.ExpressionBody is AwaitExpressionSyntax)
        {
            return true;
        }

        if (node.Body is BlockSyntax block)
        {
            return block.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
        }

        return false;
    }

    private bool IsSystemActionType(INamedTypeSymbol? typeSymbol)
    {
        return typeSymbol != null &&
            typeSymbol.Name == "Action" &&
            typeSymbol.ContainingNamespace.ToDisplayString() == "System";
    }

    private bool IsActionTypeSyntax(TypeSyntax typeSyntax)
    {
        return typeSyntax is IdentifierNameSyntax identifier && identifier.Identifier.Text == "Action";
    }

    private bool HasAsyncLambdaInitializer(SeparatedSyntaxList<VariableDeclaratorSyntax> variables)
    {
        foreach (var variable in variables)
        {
            if (variable.Initializer?.Value is LambdaExpressionSyntax lambda && LambdaHasAwait(lambda))
            {
                return true;
            }
        }

        return false;
    }

    private TypeSyntax CreateFuncTypeSyntax(INamedTypeSymbol typeSymbol, TypeSyntax originalType)
    {
        var taskType = SyntaxFactory.IdentifierName("Task");

        return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("Func"),
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList<TypeSyntax>(taskType)))
            .WithLeadingTrivia(originalType.GetLeadingTrivia())
            .WithTrailingTrivia(originalType.GetTrailingTrivia());
    }

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

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var updated = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        if (updated.AsyncKeyword != default)
        {
            return updated;
        }

        if (LambdaHasAwait(updated))
        {
            return updated.WithAsyncKeyword(
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space));
        }

        return updated;
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var updated = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        if (updated.AsyncKeyword != default)
        {
            return updated;
        }

        if (LambdaHasAwait(updated))
        {
            return updated.WithAsyncKeyword(
                SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space));
        }

        return updated;
    }

    public override SyntaxNode? VisitVariableDeclaration(VariableDeclarationSyntax node)
    {
        var updated = (VariableDeclarationSyntax)base.VisitVariableDeclaration(node)!;
        var typeSymbol = _semanticModel.GetTypeInfo(node.Type).Type as INamedTypeSymbol;

        if (!IsSystemActionType(typeSymbol))
        {
            return updated;
        }

        if (!IsActionTypeSyntax(node.Type))
        {
            return updated;
        }

        if (!HasAsyncLambdaInitializer(updated.Variables))
        {
            return updated;
        }

        var newType = CreateFuncTypeSyntax(typeSymbol!, node.Type);
        return updated.WithType(newType);
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
            // Check if the function body actually contains calls that need await
            var needsAwait = LocalFunctionBodyNeedsAwait(node);
            var returnsVoid = methodSymbol.ReturnType.SpecialType == SpecialType.System_Void;

            var newReturnType = TransformReturnType(node.ReturnType, methodSymbol.ReturnType);

            if (needsAwait)
            {
                // Check if we can directly return the task instead of using async/await
                var directReturnTransform = TryTransformLocalFunctionToDirectTaskReturn(node, returnsVoid);
                if (directReturnTransform != null)
                {
                    return directReturnTransform.WithReturnType(newReturnType);
                }

                // Function has async calls - add async modifier and awaits
                var asyncModifier = SyntaxFactory.Token(SyntaxKind.AsyncKeyword)
                    .WithTrailingTrivia(SyntaxFactory.Space);

                var newModifiers = node.Modifiers.Add(asyncModifier);

                var newBody = (BlockSyntax?)Visit(node.Body);
                var newExpressionBody = (ArrowExpressionClauseSyntax?)Visit(node.ExpressionBody);

                return node
                    .WithModifiers(newModifiers)
                    .WithReturnType(newReturnType)
                    .WithBody(newBody)
                    .WithExpressionBody(newExpressionBody);
            }
            else
            {
                // Function has no async calls - use Task.FromResult/Task.CompletedTask instead
                var newBody = TransformBodyForTaskFromResult(node.Body, returnsVoid, node.ReturnType);
                var newExpressionBody = TransformExpressionBodyForTaskFromResult(node.ExpressionBody, returnsVoid, node.ReturnType);

                return node
                    .WithReturnType(newReturnType)
                    .WithBody(newBody)
                    .WithExpressionBody(newExpressionBody);
            }
        }

        return base.VisitLocalFunctionStatement(node);
    }

    /// <summary>
    /// Tries to transform a local function to directly return a task instead of using async/await.
    /// </summary>
    private LocalFunctionStatementSyntax? TryTransformLocalFunctionToDirectTaskReturn(LocalFunctionStatementSyntax node, bool returnsVoid)
    {
        // Handle expression body
        if (node.ExpressionBody != null)
        {
            var asyncCall = GetAsyncCallExpression(node.ExpressionBody.Expression);
            if (asyncCall != null)
            {
                var newExpressionBody = node.ExpressionBody.WithExpression(asyncCall);
                return node.WithExpressionBody(newExpressionBody);
            }
            return null;
        }

        // Handle block body
        if (node.Body == null || node.Body.Statements.Count != 1)
            return null;

        var statement = node.Body.Statements[0];

        if (returnsVoid)
        {
            if (statement is ExpressionStatementSyntax exprStatement)
            {
                var asyncCall = GetAsyncCallExpression(exprStatement.Expression);
                if (asyncCall != null)
                {
                    var returnStatement = SyntaxFactory.ReturnStatement(
                            SyntaxFactory.Token(SyntaxKind.ReturnKeyword)
                                .WithLeadingTrivia(exprStatement.GetLeadingTrivia())
                                .WithTrailingTrivia(SyntaxFactory.Space),
                            asyncCall.WithoutLeadingTrivia(),
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                                .WithTrailingTrivia(exprStatement.GetTrailingTrivia()));

                    var newBody = node.Body.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(returnStatement));
                    return node.WithBody(newBody);
                }
            }
        }
        else
        {
            if (statement is ReturnStatementSyntax returnStatement && returnStatement.Expression != null)
            {
                var asyncCall = GetAsyncCallExpression(returnStatement.Expression);
                if (asyncCall != null)
                {
                    var newReturnStatement = returnStatement.WithExpression(asyncCall);
                    var newBody = node.Body.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(newReturnStatement));
                    return node.WithBody(newBody);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a local function body contains any calls that would need await
    /// </summary>
    private bool LocalFunctionBodyNeedsAwait(LocalFunctionStatementSyntax node)
    {
        var invocations = node.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol != null)
            {
                var methodId = GetMethodId(methodSymbol);

                if (_syncWrapperMethodIds.Contains(methodId))
                {
                    return true;
                }

                if (methodSymbol.IsAsync || _asyncMethodIds.Contains(methodId))
                {
                    if (invocation.Parent is not AwaitExpressionSyntax)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
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

    private ITypeSymbol? GetInterfaceReturnTypeOverride(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.ExplicitInterfaceImplementations.Length == 0)
        {
            return null;
        }

        foreach (var explicitImpl in methodSymbol.ExplicitInterfaceImplementations)
        {
            if (!explicitImpl.ContainingType.IsGenericType)
            {
                continue;
            }

            var originalDefinition = explicitImpl.ContainingType.OriginalDefinition;
            if (originalDefinition.TypeParameters.Length < 2)
            {
                continue;
            }

            var returnTypeArgument = originalDefinition.TypeParameters[^1];
            var returnTypeArgumentIndex = originalDefinition.TypeParameters.IndexOf(returnTypeArgument);
            if (returnTypeArgumentIndex < 0)
            {
                continue;
            }

            var returnType = explicitImpl.ReturnType as ITypeParameterSymbol;
            if (returnType == null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(returnType, returnTypeArgument))
            {
                continue;
            }

            var concreteArgument = explicitImpl.ContainingType.TypeArguments[returnTypeArgumentIndex];
            if (concreteArgument is not INamedTypeSymbol namedType || namedType.Name != "Task")
            {
                continue;
            }

            var typeArguments = namedType.TypeArguments;
            if (typeArguments.Length != 1)
            {
                continue;
            }

            return typeArguments[0];
        }

        return null;
    }

    private TypeSyntax? GetGenericInterfaceTaskReturnOverride(IMethodSymbol methodSymbol)
    {
        var interfaceMethods = GetImplementedInterfaceMethods(methodSymbol);
        if (interfaceMethods.Count == 0)
        {
            return null;
        }

        foreach (var interfaceMethod in interfaceMethods)
        {
            if (!interfaceMethod.ContainingType.IsGenericType)
            {
                continue;
            }

            var originalDefinition = interfaceMethod.ContainingType.OriginalDefinition;
            if (originalDefinition.TypeParameters.Length < 2)
            {
                continue;
            }

            var returnTypeArgument = originalDefinition.TypeParameters[^1];
            var returnTypeArgumentIndex = originalDefinition.TypeParameters.IndexOf(returnTypeArgument);
            if (returnTypeArgumentIndex < 0)
            {
                continue;
            }

            var returnType = interfaceMethod.ReturnType as ITypeParameterSymbol;
            if (returnType == null)
            {
                continue;
            }

            if (!SymbolEqualityComparer.Default.Equals(returnType, returnTypeArgument))
            {
                continue;
            }

            var constructedInterface = interfaceMethod.ContainingType as INamedTypeSymbol;
            if (constructedInterface == null)
            {
                continue;
            }

            var concreteArgument = constructedInterface.TypeArguments[returnTypeArgumentIndex];
            if (concreteArgument is not INamedTypeSymbol namedType || namedType.Name != "Task")
            {
                continue;
            }

            var typeArguments = namedType.TypeArguments;
            if (typeArguments.Length != 1)
            {
                continue;
            }

            var typeSyntax = SyntaxFactory.ParseTypeName(typeArguments[0].ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("Task"),
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(typeSyntax)));
        }

        return null;
    }

    private static List<IMethodSymbol> GetImplementedInterfaceMethods(IMethodSymbol methodSymbol)
    {
        var interfaceMethods = new List<IMethodSymbol>();
        var containingType = methodSymbol.ContainingType;
        if (containingType == null)
        {
            return interfaceMethods;
        }

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                if (SymbolEqualityComparer.Default.Equals(implementation, methodSymbol))
                {
                    interfaceMethods.Add(interfaceMember);
                }
            }
        }

        return interfaceMethods;
    }

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
        {
            return base.VisitClassDeclaration(node);
        }

        var updatedBaseList = UpdateInterfaceBaseList(node.BaseList, symbol);
        if (updatedBaseList != null)
        {
            node = node.WithBaseList(updatedBaseList);
        }

        return base.VisitClassDeclaration(node);
    }

    public override SyntaxNode? VisitStructDeclaration(StructDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
        {
            return base.VisitStructDeclaration(node);
        }

        var updatedBaseList = UpdateInterfaceBaseList(node.BaseList, symbol);
        if (updatedBaseList != null)
        {
            node = node.WithBaseList(updatedBaseList);
        }

        return base.VisitStructDeclaration(node);
    }

    public override SyntaxNode? VisitRecordDeclaration(RecordDeclarationSyntax node)
    {
        var symbol = _semanticModel.GetDeclaredSymbol(node);
        if (symbol == null)
        {
            return base.VisitRecordDeclaration(node);
        }

        var updatedBaseList = UpdateInterfaceBaseList(node.BaseList, symbol);
        if (updatedBaseList != null)
        {
            node = node.WithBaseList(updatedBaseList);
        }

        return base.VisitRecordDeclaration(node);
    }

    private string GetMethodId(IMethodSymbol methodSymbol)
    {
        // Use OriginalDefinition and MinimallyQualifiedFormat to match the analyzer's format
        var originalMethod = methodSymbol.OriginalDefinition;
        var parameters = string.Join(", ", originalMethod.Parameters.Select(p => p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        var signature = $"{originalMethod.Name}({parameters})";
        return $"{originalMethod.ContainingType?.ToDisplayString()}.{signature}";
    }

    private ExplicitInterfaceSpecifierSyntax? UpdateExplicitInterfaceSpecifier(MethodDeclarationSyntax node, IMethodSymbol methodSymbol)
    {
        if (node.ExplicitInterfaceSpecifier == null || methodSymbol.ExplicitInterfaceImplementations.Length == 0)
        {
            return null;
        }

        if (!_methodsToTransform.Contains(GetMethodId(methodSymbol)))
        {
            return null;
        }

        var explicitInterface = methodSymbol.ExplicitInterfaceImplementations[0].ContainingType as INamedTypeSymbol;
        if (explicitInterface == null)
        {
            return null;
        }

        var updatedTypeSyntax = GetUpdatedInterfaceTypeSyntax(node.ExplicitInterfaceSpecifier.Name, explicitInterface, methodSymbol.ContainingType);
        if (updatedTypeSyntax is not NameSyntax nameSyntax)
        {
            return null;
        }

        return node.ExplicitInterfaceSpecifier.WithName(nameSyntax);
    }

    private BaseListSyntax? UpdateInterfaceBaseList(BaseListSyntax? baseList, INamedTypeSymbol typeSymbol)
    {
        if (baseList == null)
        {
            return null;
        }

        var updatedTypes = new List<BaseTypeSyntax>();
        foreach (var baseType in baseList.Types)
        {
            var interfaceSymbol = _semanticModel.GetTypeInfo(baseType.Type).Type as INamedTypeSymbol;
            if (interfaceSymbol == null || interfaceSymbol.TypeKind != TypeKind.Interface)
            {
                updatedTypes.Add(baseType);
                continue;
            }

            var updatedType = GetUpdatedInterfaceTypeSyntax(baseType.Type, interfaceSymbol, typeSymbol);
            updatedTypes.Add(updatedType != null ? baseType.WithType(updatedType) : baseType);
        }

        return baseList.WithTypes(SyntaxFactory.SeparatedList(updatedTypes, baseList.Types.GetSeparators()));
    }

    private TypeSyntax? GetUpdatedInterfaceTypeSyntax(TypeSyntax typeSyntax, INamedTypeSymbol interfaceSymbol, INamedTypeSymbol containingTypeSymbol)
    {
        if (interfaceSymbol.TypeArguments.Length < 2)
        {
            return null;
        }

        if (interfaceSymbol.TypeArguments[^1] is INamedTypeSymbol namedType && namedType.Name == "Task")
        {
            return null;
        }

        if (!ShouldUpdateInterfaceImplementation(typeSyntax, interfaceSymbol, containingTypeSymbol))
        {
            return null;
        }

        var returnTypeArgument = interfaceSymbol.OriginalDefinition.TypeParameters[^1];
        foreach (var interfaceMethod in interfaceSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (interfaceMethod.ReturnType is not ITypeParameterSymbol returnTypeParam ||
                !SymbolEqualityComparer.Default.Equals(returnTypeParam, returnTypeArgument))
            {
                continue;
            }

            if (_asyncMethodIds.Contains(GetMethodId(interfaceMethod)))
            {
                continue;
            }

            var implementation = containingTypeSymbol.FindImplementationForInterfaceMember(interfaceMethod) as IMethodSymbol;
            if (implementation == null)
            {
                continue;
            }

            if (!_methodsToTransform.Contains(GetMethodId(implementation)))
            {
                continue;
            }

            var typeArguments = ExtractTypeArguments(typeSyntax).ToList();
            if (typeArguments.Count == 0)
            {
                return null;
            }

            var lastArgument = typeArguments[^1];
            var wrappedArgument = WrapInTaskType(lastArgument);
            return ReplaceLastTypeArgument(typeSyntax, wrappedArgument);
        }

        return null;
    }

    private static IEnumerable<TypeSyntax> ExtractTypeArguments(TypeSyntax typeSyntax)
    {
        return typeSyntax switch
        {
            GenericNameSyntax generic => generic.TypeArgumentList.Arguments,
            QualifiedNameSyntax qualified => ExtractTypeArguments(qualified.Right),
            AliasQualifiedNameSyntax alias => ExtractTypeArguments(alias.Name),
            _ => Enumerable.Empty<TypeSyntax>()
        };
    }

    private static TypeSyntax ReplaceLastTypeArgument(TypeSyntax typeSyntax, TypeSyntax newArgument)
    {
        switch (typeSyntax)
        {
            case GenericNameSyntax generic:
                var arguments = generic.TypeArgumentList.Arguments;
                if (arguments.Count == 0)
                {
                    return typeSyntax;
                }

                var updatedArguments = arguments.Replace(arguments[^1], newArgument.WithTriviaFrom(arguments[^1]));
                return generic.WithTypeArgumentList(SyntaxFactory.TypeArgumentList(updatedArguments));
            case QualifiedNameSyntax qualified:
                var newRight = ReplaceLastTypeArgument(qualified.Right, newArgument) as SimpleNameSyntax;
                return newRight == null ? typeSyntax : qualified.WithRight(newRight);
            case AliasQualifiedNameSyntax alias:
                var newName = ReplaceLastTypeArgument(alias.Name, newArgument) as SimpleNameSyntax;
                return newName == null ? typeSyntax : alias.WithName(newName);
            default:
                return typeSyntax;
        }
    }

    private static TypeSyntax WrapInTaskType(TypeSyntax originalType)
    {
        return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("Task"),
                SyntaxFactory.TypeArgumentList(
                    SyntaxFactory.SingletonSeparatedList(originalType.WithoutLeadingTrivia().WithoutTrailingTrivia())))
            .WithLeadingTrivia(originalType.GetLeadingTrivia())
            .WithTrailingTrivia(originalType.GetTrailingTrivia());
    }

    private bool ShouldUpdateInterfaceImplementation(TypeSyntax typeSyntax, INamedTypeSymbol interfaceSymbol, INamedTypeSymbol containingTypeSymbol)
    {
        if (interfaceSymbol.GetMembers().OfType<IMethodSymbol>()
            .Any(interfaceMethod => _asyncMethodIds.Contains(GetMethodId(interfaceMethod))))
        {
            return false;
        }

        var returnTypeParameter = interfaceSymbol.OriginalDefinition.TypeParameters[^1];
        var candidateMethods = interfaceSymbol.GetMembers().OfType<IMethodSymbol>()
            .Where(interfaceMethod => interfaceMethod.ReturnType is ITypeParameterSymbol returnTypeParam &&
                SymbolEqualityComparer.Default.Equals(returnTypeParam, returnTypeParameter))
            .ToList();

        if (candidateMethods.Count != 1)
        {
            return false;
        }

        var candidateMethod = candidateMethods[0];
        var implementation = containingTypeSymbol.FindImplementationForInterfaceMember(candidateMethod) as IMethodSymbol;
        if (implementation == null)
        {
            return false;
        }

        return _methodsToTransform.Contains(GetMethodId(implementation));
    }

    /// <summary>
    /// Tries to transform a method to directly return a task instead of using async/await.
    /// This is possible when:
    /// - Void method has a single statement that is just an async call (return the task)
    /// - Method has a single return statement that just returns an async call result
    /// </summary>
    private MethodDeclarationSyntax? TryTransformToDirectTaskReturn(MethodDeclarationSyntax node, bool returnsVoid)
    {
        // Handle expression body: T Method() => AsyncCall();
        if (node.ExpressionBody != null)
        {
            var asyncCall = GetAsyncCallExpression(node.ExpressionBody.Expression);
            if (asyncCall != null)
            {
                // Transform to: Task<T> Method() => AsyncCall();
                var newExpressionBody = node.ExpressionBody.WithExpression(asyncCall);
                return node.WithExpressionBody(newExpressionBody);
            }
            return null;
        }

        // Handle block body
        if (node.Body == null || node.Body.Statements.Count != 1)
            return null;

        var statement = node.Body.Statements[0];

        if (returnsVoid)
        {
            // Void method: look for single expression statement like: AsyncCall();
            if (statement is ExpressionStatementSyntax exprStatement)
            {
                var asyncCall = GetAsyncCallExpression(exprStatement.Expression);
                if (asyncCall != null)
                {
                    // Transform to: return AsyncCall();
                    // Preserve statement's leading trivia, add space after return keyword
                    var returnStatement = SyntaxFactory.ReturnStatement(
                            SyntaxFactory.Token(SyntaxKind.ReturnKeyword)
                                .WithLeadingTrivia(exprStatement.GetLeadingTrivia())
                                .WithTrailingTrivia(SyntaxFactory.Space),
                            asyncCall.WithoutLeadingTrivia(),
                            SyntaxFactory.Token(SyntaxKind.SemicolonToken)
                                .WithTrailingTrivia(exprStatement.GetTrailingTrivia()));

                    var newBody = node.Body.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(returnStatement));
                    return node.WithBody(newBody);
                }
            }
        }
        else
        {
            // Non-void method: look for single return statement like: return AsyncCall();
            if (statement is ReturnStatementSyntax returnStatement && returnStatement.Expression != null)
            {
                var asyncCall = GetAsyncCallExpression(returnStatement.Expression);
                if (asyncCall != null)
                {
                    // Transform to: return AsyncCall(); (just use the call directly without await)
                    var newReturnStatement = returnStatement.WithExpression(asyncCall);
                    var newBody = node.Body.WithStatements(SyntaxFactory.SingletonList<StatementSyntax>(newReturnStatement));
                    return node.WithBody(newBody);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the async call expression if the expression is (or will become) an async call.
    /// Returns null if not an async call or if the call can't be directly returned.
    /// </summary>
    private InvocationExpressionSyntax? GetAsyncCallExpression(ExpressionSyntax expression)
    {
        // Handle case where it's already a direct invocation
        if (expression is InvocationExpressionSyntax invocation)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol != null)
            {
                var methodId = GetMethodId(methodSymbol);

                // Check if this call is to an async method or will become async
                if (methodSymbol.IsAsync || _asyncMethodIds.Contains(methodId))
                {
                    return invocation;
                }

                // Check if this is a sync wrapper that will be unwrapped
                // In that case, we can't directly return (it needs unwrapping)
                if (_syncWrapperMethodIds.Contains(methodId))
                {
                    return null;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a method body contains any calls that would need await
    /// </summary>
    private bool MethodBodyNeedsAwait(MethodDeclarationSyntax node)
    {
        // Check all invocations in the method body
        var invocations = node.DescendantNodes().OfType<InvocationExpressionSyntax>();

        foreach (var invocation in invocations)
        {
            var symbolInfo = _semanticModel.GetSymbolInfo(invocation);
            var methodSymbol = symbolInfo.Symbol as IMethodSymbol;

            if (methodSymbol != null)
            {
                var methodId = GetMethodId(methodSymbol);

                // Check if this is a sync wrapper call that would be unwrapped
                if (_syncWrapperMethodIds.Contains(methodId))
                {
                    return true;
                }

                // Check if this call is to an async method or a method that will be async
                if (methodSymbol.IsAsync || _asyncMethodIds.Contains(methodId))
                {
                    // Only count if not already awaited
                    if (invocation.Parent is not AwaitExpressionSyntax)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Transforms a block body to use Task.FromResult/Task.CompletedTask instead of async
    /// </summary>
    private BlockSyntax? TransformBodyForTaskFromResult(BlockSyntax? body, bool returnsVoid, TypeSyntax originalReturnType)
    {
        if (body == null)
            return null;

        var newStatements = new List<StatementSyntax>();
        var hasReturn = false;

        foreach (var statement in body.Statements)
        {
            if (statement is ReturnStatementSyntax returnStatement)
            {
                hasReturn = true;
                newStatements.Add(TransformReturnStatement(returnStatement, returnsVoid, originalReturnType));
            }
            else
            {
                newStatements.Add(statement);
            }
        }

        // If void-returning method has no explicit return, add return Task.CompletedTask at the end
        if (returnsVoid && !hasReturn)
        {
            var completedTaskReturn = SyntaxFactory.ReturnStatement(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("Task"),
                    SyntaxFactory.IdentifierName("CompletedTask")))
                .WithLeadingTrivia(body.Statements.LastOrDefault()?.GetTrailingTrivia() ?? SyntaxFactory.TriviaList())
                .NormalizeWhitespace()
                .WithTrailingTrivia(SyntaxFactory.EndOfLine("\n"));

            newStatements.Add(completedTaskReturn);
        }

        return body.WithStatements(SyntaxFactory.List(newStatements));
    }

    /// <summary>
    /// Transforms an expression body to use Task.FromResult
    /// </summary>
    private ArrowExpressionClauseSyntax? TransformExpressionBodyForTaskFromResult(
        ArrowExpressionClauseSyntax? expressionBody,
        bool returnsVoid,
        TypeSyntax originalReturnType)
    {
        if (expressionBody == null)
            return null;

        var expression = expressionBody.Expression;

        if (returnsVoid)
        {
            // For void methods with expression body (like: void Foo() => DoSomething();)
            // This is rare but possible - we can't easily convert to Task.CompletedTask
            // For now, keep the expression as-is and it will need manual adjustment
            // A block body would be needed: { DoSomething(); return Task.CompletedTask; }
            return expressionBody;
        }

        // Wrap the expression with Task.FromResult
        var taskFromResult = CreateTaskFromResultExpression(expression, originalReturnType);
        return expressionBody.WithExpression(taskFromResult);
    }

    /// <summary>
    /// Transforms a return statement to use Task.FromResult or Task.CompletedTask
    /// </summary>
    private ReturnStatementSyntax TransformReturnStatement(
        ReturnStatementSyntax returnStatement,
        bool returnsVoid,
        TypeSyntax originalReturnType)
    {
        if (returnsVoid || returnStatement.Expression == null)
        {
            // return; -> return Task.CompletedTask;
            var completedTask = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("Task"),
                SyntaxFactory.IdentifierName("CompletedTask"));

            return returnStatement
                .WithExpression(completedTask)
                .WithLeadingTrivia(returnStatement.GetLeadingTrivia())
                .WithTrailingTrivia(returnStatement.GetTrailingTrivia());
        }

        // return value; -> return Task.FromResult(value);
        var taskFromResult = CreateTaskFromResultExpression(returnStatement.Expression, originalReturnType);

        return returnStatement
            .WithExpression(taskFromResult)
            .WithLeadingTrivia(returnStatement.GetLeadingTrivia())
            .WithTrailingTrivia(returnStatement.GetTrailingTrivia());
    }

    /// <summary>
    /// Creates a Task.FromResult(expression) invocation
    /// </summary>
    private InvocationExpressionSyntax CreateTaskFromResultExpression(ExpressionSyntax expression, TypeSyntax returnType)
    {
        // Create Task.FromResult<T>(expression) or Task.FromResult(expression)
        var taskFromResult = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName("Task"),
                SyntaxFactory.GenericName(
                    SyntaxFactory.Identifier("FromResult"),
                    SyntaxFactory.TypeArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            returnType.WithoutLeadingTrivia().WithoutTrailingTrivia())))),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(expression.WithoutLeadingTrivia()))));

        return taskFromResult;
    }
}
