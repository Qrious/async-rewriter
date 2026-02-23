using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Shared static helpers for the async transformation rewriters.
/// Responsible solely for producing transformed Roslyn syntax nodes —
/// all matching/lookup decisions are the caller's responsibility.
/// </summary>
internal static class AsyncTransformHelpers
{
    // ──────────────────────────────────────────────────────────────────────────
    // Return-type helpers
    // ──────────────────────────────────────────────────────────────────────────

    internal static bool IsAlreadyTaskType(string returnType) =>
        returnType is "Task" or "System.Threading.Tasks.Task" or "ValueTask" or "System.Threading.Tasks.ValueTask"
        || returnType.StartsWith("Task<")
        || returnType.StartsWith("System.Threading.Tasks.Task<")
        || returnType.StartsWith("ValueTask<")
        || returnType.StartsWith("System.Threading.Tasks.ValueTask<");

    // ──────────────────────────────────────────────────────────────────────────
    // Modifier helpers
    // ──────────────────────────────────────────────────────────────────────────

    internal static MethodDeclarationSyntax AddAsyncModifier(MethodDeclarationSyntax method)
    {
        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            return method;
        }

        if (method.Modifiers.Count == 0)
        {
            var leadingTrivia = method.ReturnType.GetLeadingTrivia();
            var asyncToken = Token(SyntaxKind.AsyncKeyword)
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(Space);
            return method
                .WithModifiers(TokenList(asyncToken))
                .WithReturnType(method.ReturnType.WithoutLeadingTrivia());
        }

        return method.WithModifiers(method.Modifiers.Add(
            Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space)));
    }

    internal static LocalFunctionStatementSyntax AddAsyncModifierToLocalFunction(
        LocalFunctionStatementSyntax func)
    {
        if (func.Modifiers.Any(SyntaxKind.AsyncKeyword))
        {
            return func;
        }

        if (func.Modifiers.Count == 0)
        {
            var leadingTrivia = func.ReturnType.GetLeadingTrivia();
            var asyncToken = Token(SyntaxKind.AsyncKeyword)
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(Space);
            return func
                .WithModifiers(TokenList(asyncToken))
                .WithReturnType(func.ReturnType.WithoutLeadingTrivia());
        }

        return func.WithModifiers(func.Modifiers.Add(
            Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space)));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // "No-await" body transformation
    // ──────────────────────────────────────────────────────────────────────────

    internal static MethodDeclarationSyntax TransformBodyForNoAwait(
        MethodDeclarationSyntax method,
        string originalReturnType,
        string newReturnType)
    {
        if (method.Body == null && method.ExpressionBody == null)
        {
            return method;
        }

        return originalReturnType == "void"
            ? TransformVoidMethodNoAwait(method)
            : TransformReturningMethodNoAwait(method, originalReturnType);
    }

    /// <summary>void method with no awaitable calls — add "return Task.CompletedTask;" and rewrite bare returns.</summary>
    internal static MethodDeclarationSyntax TransformVoidMethodNoAwait(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody != null)
        {
            var exprStatement = ExpressionStatement(method.ExpressionBody.Expression)
                .WithLeadingTrivia(Whitespace("        "))
                .WithTrailingTrivia(CarriageReturnLineFeed);

            var returnStatement = ReturnStatement(MakeTaskCompletedTaskExpression().WithLeadingTrivia(Space))
                .WithLeadingTrivia(Whitespace("        "))
                .WithTrailingTrivia(CarriageReturnLineFeed);

            return method
                .WithExpressionBody(null)
                .WithSemicolonToken(Token(SyntaxKind.None))
                .WithBody(Block(exprStatement, returnStatement));
        }

        if (method.Body != null)
        {
            var newBody = (BlockSyntax)new BareReturnRewriter().Visit(method.Body);
            var returnStatement = ReturnStatement(MakeTaskCompletedTaskExpression().WithLeadingTrivia(Space))
                .WithLeadingTrivia(Whitespace("        "))
                .WithTrailingTrivia(CarriageReturnLineFeed);
            return method.WithBody(newBody.AddStatements(returnStatement));
        }

        return method;
    }

    /// <summary>Non-void method with no awaitable calls — wrap return values with Task.FromResult.</summary>
    internal static MethodDeclarationSyntax TransformReturningMethodNoAwait(
        MethodDeclarationSyntax method, string originalReturnType)
    {
        var wrapper = new ReturnValueWrapper(originalReturnType);

        if (method.ExpressionBody != null)
        {
            var wrapped = (ExpressionSyntax)wrapper.Visit(method.ExpressionBody.Expression);
            return method.WithExpressionBody(method.ExpressionBody.WithExpression(wrapped));
        }

        if (method.Body != null)
        {
            return method.WithBody((BlockSyntax)wrapper.Visit(method.Body));
        }

        return method;
    }

    internal static LocalFunctionStatementSyntax TransformLocalFunctionBodyForNoAwait(
        LocalFunctionStatementSyntax func,
        string originalReturnType,
        string newReturnType)
    {
        if (func.Body == null && func.ExpressionBody == null)
        {
            return func;
        }

        if (originalReturnType == "void")
        {
            if (func.Body != null)
            {
                var newBody = (BlockSyntax)new BareReturnRewriter().Visit(func.Body);
                var returnStatement = ReturnStatement(MakeTaskCompletedTaskExpression().WithLeadingTrivia(Space))
                    .WithLeadingTrivia(Whitespace("        "))
                    .WithTrailingTrivia(CarriageReturnLineFeed);
                return func.WithBody(newBody.AddStatements(returnStatement));
            }
        }
        else
        {
            var wrapper = new ReturnValueWrapper(originalReturnType);
            if (func.ExpressionBody != null)
            {
                var wrapped = (ExpressionSyntax)wrapper.Visit(func.ExpressionBody.Expression);
                return func.WithExpressionBody(func.ExpressionBody.WithExpression(wrapped));
            }

            if (func.Body != null)
            {
                return func.WithBody((BlockSyntax)wrapper.Visit(func.Body));
            }
        }

        return func;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Direct-Task-return optimisations
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Single-await method: remove await and return the Task directly (no async overhead).
    /// Returns <c>null</c> when the optimisation cannot be applied.
    /// </summary>
    internal static MethodDeclarationSyntax? TryOptimizeDirectTaskReturn(
        MethodDeclarationSyntax method, string originalReturnType)
    {
        if (method.Body == null || method.Body.Statements.Count != 1)
        {
            return null;
        }

        var stmt = method.Body.Statements[0];

        // void method: { await expr; } → { return expr; }
        if (originalReturnType == "void"
            && stmt is ExpressionStatementSyntax { Expression: AwaitExpressionSyntax awaitExpr1 })
        {
            var ret = ReturnStatement(awaitExpr1.Expression.WithLeadingTrivia(Space))
                .WithLeadingTrivia(stmt.GetLeadingTrivia())
                .WithTrailingTrivia(stmt.GetTrailingTrivia());
            return method.WithBody(method.Body.WithStatements(SingletonList<StatementSyntax>(ret)));
        }

        // returning method: { return await expr; } → { return expr; }
        if (stmt is ReturnStatementSyntax { Expression: AwaitExpressionSyntax awaitExpr2 })
        {
            var newReturn = ((ReturnStatementSyntax)stmt).WithExpression(
                awaitExpr2.Expression.WithLeadingTrivia(awaitExpr2.GetLeadingTrivia()));
            return method.WithBody(method.Body.WithStatements(SingletonList<StatementSyntax>(newReturn)));
        }

        // expression-bodied: => await expr → => expr
        if (method.ExpressionBody?.Expression is AwaitExpressionSyntax awaitExpr3)
        {
            return method.WithExpressionBody(
                method.ExpressionBody.WithExpression(
                    awaitExpr3.Expression.WithLeadingTrivia(awaitExpr3.GetLeadingTrivia())));
        }

        return null;
    }

    /// <summary>
    /// Unwraps an external sync-wrapper call that already contains an async lambda,
    /// e.g. <c>RunSync(async () => await _repo.Open())</c> → <c>return _repo.Open();</c>.
    /// Returns <c>null</c> when the optimisation cannot be applied.
    /// </summary>
    internal static MethodDeclarationSyntax? TryOptimizeExternalSyncWrapperUnwrap(
        MethodDeclarationSyntax method, string originalReturnType)
    {
        if (method.Body == null || method.Body.Statements.Count != 1)
        {
            return null;
        }

        var stmt = method.Body.Statements[0];

        if (originalReturnType == "void"
            && stmt is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax inv })
        {
            var inner = TryExtractAsyncLambdaBody(inv);
            if (inner != null)
            {
                var ret = ReturnStatement(inner.WithLeadingTrivia(Space))
                    .WithLeadingTrivia(stmt.GetLeadingTrivia())
                    .WithTrailingTrivia(stmt.GetTrailingTrivia());
                return method.WithBody(method.Body.WithStatements(SingletonList<StatementSyntax>(ret)));
            }
        }

        if (stmt is ReturnStatementSyntax { Expression: InvocationExpressionSyntax retInv })
        {
            var inner = TryExtractAsyncLambdaBody(retInv);
            if (inner != null)
            {
                var newReturn = ReturnStatement(inner.WithLeadingTrivia(Space))
                    .WithLeadingTrivia(stmt.GetLeadingTrivia())
                    .WithTrailingTrivia(stmt.GetTrailingTrivia());
                return method.WithBody(method.Body.WithStatements(SingletonList<StatementSyntax>(newReturn)));
            }
        }

        return null;
    }

    private static ExpressionSyntax? TryExtractAsyncLambdaBody(InvocationExpressionSyntax invocation)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            ExpressionSyntax? body = null;

            if (arg.Expression is ParenthesizedLambdaExpressionSyntax pl
                && pl.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
            {
                body = pl.Body as ExpressionSyntax;
                if (body == null && pl.Body is BlockSyntax blk && blk.Statements.Count == 1)
                {
                    body = blk.Statements[0] is ExpressionStatementSyntax es
                        ? es.Expression
                        : (blk.Statements[0] is ReturnStatementSyntax { Expression: not null } rs
                            ? rs.Expression
                            : null);
                }
            }
            else if (arg.Expression is SimpleLambdaExpressionSyntax sl
                     && sl.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
            {
                body = sl.Body as ExpressionSyntax;
            }

            if (body is AwaitExpressionSyntax awaitExpr)
            {
                return awaitExpr.Expression;
            }
        }

        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Sync-wrapper unwrap helper
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts the lambda body from a sync-wrapper call such as
    /// <c>RunSync(() => _repo.GetAsync())</c> → <c>_repo.GetAsync()</c>.
    /// Returns <c>null</c> if the call cannot be unwrapped.
    /// </summary>
    internal static ExpressionSyntax? TryUnwrapSyncWrapperCall(InvocationExpressionSyntax invocation)
    {
        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is ParenthesizedLambdaExpressionSyntax parenLambda)
            {
                if (parenLambda.Body is ExpressionSyntax expr)
                {
                    return expr;
                }

                if (parenLambda.Body is BlockSyntax block
                    && block.Statements.Count == 1
                    && block.Statements[0] is ReturnStatementSyntax { Expression: not null } ret)
                {
                    return ret.Expression;
                }
            }

            if (arg.Expression is SimpleLambdaExpressionSyntax simpleLambda)
            {
                if (simpleLambda.Body is ExpressionSyntax simpleExpr)
                {
                    return simpleExpr;
                }

                if (simpleLambda.Body is BlockSyntax simpleBlock
                    && simpleBlock.Statements.Count == 1
                    && simpleBlock.Statements[0] is ReturnStatementSyntax { Expression: not null } simpleRet)
                {
                    return simpleRet.Expression;
                }
            }
        }

        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Invocation helpers
    // ──────────────────────────────────────────────────────────────────────────

    internal static string? GetInvokedMethodName(InvocationExpressionSyntax node) =>
        node.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };

    /// <summary>
    /// Checks whether <paramref name="node"/> contains any AwaitExpression that is a
    /// direct child (not nested inside another lambda or local function).
    /// </summary>
    internal static bool ContainsDirectAwait(SyntaxNode? node)
    {
        if (node == null)
        {
            return false;
        }

        foreach (var descendant in node.DescendantNodesAndSelf(
                     n => n is not SimpleLambdaExpressionSyntax
                              and not ParenthesizedLambdaExpressionSyntax
                              and not LocalFunctionStatementSyntax))
        {
            if (descendant is AwaitExpressionSyntax)
            {
                return true;
            }
        }

        return false;
    }

    internal static ExpressionSyntax MakeTaskCompletedTaskExpression() =>
        MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            IdentifierName("Task"),
            IdentifierName("CompletedTask"));

    // ──────────────────────────────────────────────────────────────────────────
    // Out-parameter method transformation
    // ──────────────────────────────────────────────────────────────────────────

    internal static MethodDeclarationSyntax TransformOutParameterMethod(
        MethodDeclarationSyntax visited,
        MethodTransformInfo info,
        bool hasAwaitableCalls)
    {
        var outInfo = info.OutParameterInfo!;

        // 1. Remove out parameters
        var outSet = new HashSet<int>(outInfo.OutParameterIndices);
        var newParams = visited.ParameterList.Parameters
            .Where((_, i) => !outSet.Contains(i))
            .ToList();
        visited = visited.WithParameterList(
            ParameterList(SeparatedList(newParams)).WithTriviaFrom(visited.ParameterList));

        // 2. Change return type
        visited = visited.WithReturnType(
            ParseTypeName(outInfo.NewAsyncReturnType).WithTriviaFrom(visited.ReturnType));

        // 3. Rewrite body
        if (hasAwaitableCalls)
        {
            visited = AddAsyncModifier(visited);
        }

        if (visited.Body != null)
        {
            var localDecls = BuildOutParamLocalDeclarations(outInfo, visited);
            var bodyRewriter = new OutParameterReturnRewriter(outInfo, isAsync: hasAwaitableCalls);
            var newBody = (BlockSyntax)bodyRewriter.Visit(visited.Body);

            if (localDecls.Count > 0)
            {
                newBody = newBody.WithStatements(List(localDecls.Concat(newBody.Statements)));
            }

            visited = visited.WithBody(newBody);
        }
        else if (visited.ExpressionBody != null)
        {
            var bodyRewriter = new OutParameterReturnRewriter(outInfo);
            var wrapped = (ExpressionSyntax)bodyRewriter.VisitExpressionForReturn(
                visited.ExpressionBody.Expression);
            visited = visited.WithExpressionBody(visited.ExpressionBody.WithExpression(wrapped));
        }

        return visited;
    }

    private static List<StatementSyntax> BuildOutParamLocalDeclarations(
        OutParameterTransformInfo outInfo,
        MethodDeclarationSyntax method)
    {
        var result = new List<StatementSyntax>();

        for (var i = 0; i < outInfo.OutParameterNames.Count; i++)
        {
            var typeName = outInfo.OutParameterTypes[i];
            var varName = outInfo.OutParameterNames[i];

            var decl = LocalDeclarationStatement(
                    VariableDeclaration(ParseTypeName(typeName).WithTrailingTrivia(Space))
                        .WithVariables(SingletonSeparatedList(
                            VariableDeclarator(Identifier(varName))
                                .WithInitializer(EqualsValueClause(
                                    PostfixUnaryExpression(
                                        SyntaxKind.SuppressNullableWarningExpression,
                                        LiteralExpression(SyntaxKind.DefaultLiteralExpression,
                                            Token(SyntaxKind.DefaultKeyword)))
                                    .WithLeadingTrivia(Space))
                                    .WithLeadingTrivia(Space)))))
                .WithLeadingTrivia(method.Body?.Statements.Count > 0
                    ? method.Body.Statements[0].GetLeadingTrivia()
                    : TriviaList(Whitespace("        ")))
                .WithTrailingTrivia(LineFeed);

            result.Add(decl);
        }

        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Debug-comment helpers
    // ──────────────────────────────────────────────────────────────────────────

    internal static MethodDeclarationSyntax PrependDebugComments(
        MethodDeclarationSyntax method, List<string> debugLines)
    {
        var commentTrivia = BuildDebugCommentTrivia(method.GetLeadingTrivia(), debugLines);
        return method.WithLeadingTrivia(commentTrivia);
    }

    internal static LocalFunctionStatementSyntax PrependDebugComments(
        LocalFunctionStatementSyntax func, List<string> debugLines)
    {
        var commentTrivia = BuildDebugCommentTrivia(func.GetLeadingTrivia(), debugLines);
        return func.WithLeadingTrivia(commentTrivia);
    }

    private static SyntaxTriviaList BuildDebugCommentTrivia(
        SyntaxTriviaList existing, List<string> debugLines)
    {
        var result = new List<SyntaxTrivia>();
        var indentation = "";

        for (var i = existing.Count - 1; i >= 0; i--)
        {
            if (existing[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                indentation = existing[i].ToString();
                break;
            }
        }

        for (var i = 0; i < existing.Count; i++)
        {
            if (i == existing.Count - 1 && existing[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                continue;
            }

            result.Add(existing[i]);
        }

        foreach (var line in debugLines)
        {
            result.Add(Whitespace(indentation));
            result.Add(Comment($"// [async-rewriter] {line}"));
            result.Add(LineFeed);
        }

        result.Add(Whitespace(indentation));
        return TriviaList(result);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Inner rewriter helpers (shared nested types)
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>Rewrites bare <c>return;</c> statements to <c>return Task.CompletedTask;</c>.</summary>
    internal sealed class BareReturnRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
            {
                return node.WithExpression(MakeTaskCompletedTaskExpression().WithLeadingTrivia(Space));
            }

            return base.VisitReturnStatement(node);
        }
    }

    /// <summary>Wraps return-value expressions with <c>Task.FromResult&lt;T&gt;(expr)</c>.</summary>
    internal sealed class ReturnValueWrapper : CSharpSyntaxRewriter
    {
        private readonly string _originalReturnType;

        internal ReturnValueWrapper(string originalReturnType)
        {
            _originalReturnType = originalReturnType;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
            {
                return node;
            }

            var taskFromResult = InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        IdentifierName("Task"),
                        GenericName(Identifier("FromResult"))
                            .WithTypeArgumentList(
                                TypeArgumentList(
                                    SingletonSeparatedList(ParseTypeName(_originalReturnType))))))
                .WithArgumentList(
                    ArgumentList(
                        SingletonSeparatedList(
                            Argument(node.Expression.WithoutLeadingTrivia()))))
                .WithLeadingTrivia(node.Expression.GetLeadingTrivia());

            return node.WithExpression(taskFromResult);
        }
    }

    /// <summary>
    /// Rewrites return statements and out-parameter assignments in bodies
    /// that have out-parameter async transformations applied.
    /// </summary>
    internal sealed class OutParameterReturnRewriter : CSharpSyntaxRewriter
    {
        private readonly OutParameterTransformInfo _outInfo;
        private readonly bool _isAsync;

        internal OutParameterReturnRewriter(OutParameterTransformInfo outInfo, bool isAsync = false)
        {
            _outInfo = outInfo;
            _isAsync = isAsync;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
            {
                return node;
            }

            var wrapped = WrapReturnExpression(node.Expression);
            return node.WithExpression(wrapped.WithLeadingTrivia(node.Expression.GetLeadingTrivia()));
        }

        public ExpressionSyntax VisitExpressionForReturn(ExpressionSyntax expr) =>
            WrapReturnExpression(expr);

        private ExpressionSyntax WrapReturnExpression(ExpressionSyntax returnExpr)
        {
            if (_outInfo.IsTryPattern)
            {
                string innerType;
                ExpressionSyntax valueArg;

                if (_outInfo.OutParameterTypes.Count == 1)
                {
                    innerType = _outInfo.OutParameterTypes[0];
                    valueArg = IdentifierName(_outInfo.OutParameterNames[0]);
                }
                else
                {
                    var tupleElements = _outInfo.OutParameterTypes
                        .Zip(_outInfo.OutParameterNames, (t, n) => $"{t} {n}");
                    innerType = $"({string.Join(", ", tupleElements)})";
                    valueArg = TupleExpression(
                        SeparatedList(_outInfo.OutParameterNames.Select(n => Argument(IdentifierName(n)))));
                }

                var newExpr = ObjectCreationExpression(
                        ParseTypeName($"AsyncOutResult<{innerType}>").WithLeadingTrivia(Space))
                    .WithArgumentList(ArgumentList(SeparatedList(new[]
                    {
                        Argument(valueArg),
                        Argument(returnExpr.WithoutLeadingTrivia()).WithLeadingTrivia(Space)
                    })));

                return _isAsync
                    ? (ExpressionSyntax)newExpr
                    : WrapInTaskFromResult(newExpr, $"AsyncOutResult<{innerType}>");
            }
            else
            {
                var tupleArgs = new List<ArgumentSyntax>
                {
                    Argument(returnExpr.WithoutLeadingTrivia())
                };
                tupleArgs.AddRange(_outInfo.OutParameterNames.Select(n => Argument(IdentifierName(n))));
                var tupleExpr = TupleExpression(SeparatedList(tupleArgs));

                return _isAsync
                    ? (ExpressionSyntax)tupleExpr
                    : WrapInTaskFromResult(tupleExpr);
            }
        }

        private static ExpressionSyntax WrapInTaskFromResult(
            ExpressionSyntax expr, string? typeArg = null)
        {
            if (typeArg != null)
            {
                return InvocationExpression(
                        MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            IdentifierName("Task"),
                            GenericName(Identifier("FromResult"))
                                .WithTypeArgumentList(
                                    TypeArgumentList(
                                        SingletonSeparatedList(ParseTypeName(typeArg))))))
                    .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(expr))));
            }

            return InvocationExpression(
                    MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        IdentifierName("Task"),
                        IdentifierName("FromResult")))
                .WithArgumentList(ArgumentList(SingletonSeparatedList(Argument(expr))));
        }
    }

    /// <summary>
    /// Recursively walks <paramref name="type"/> and replaces every generic type argument
    /// whose string representation matches <paramref name="originalReturnType"/> with a
    /// freshly-parsed <paramref name="newReturnType"/> node.
    /// Non-generic types that match directly are also replaced.
    /// </summary>
    public static TypeSyntax TransformTypeSyntax(TypeSyntax type, string originalReturnType, string newReturnType)
    {
        // Direct match (e.g. the whole type is the one we want to replace)
        if (type.ToString().Trim() == originalReturnType.Trim())
        {
            return ParseTypeName(newReturnType).WithTriviaFrom(type);
        }

        // Recurse into generic type arguments, e.g. IMapper<TSource, TDestination>
        if (type is GenericNameSyntax genericName)
        {
            var args = genericName.TypeArgumentList.Arguments;
            var newArgs = args.Select(arg => TransformTypeSyntax(arg, originalReturnType, newReturnType));
            var newTypeArgList = genericName.TypeArgumentList.WithArguments(
                SeparatedList(newArgs, args.GetSeparators()));
            return genericName.WithTypeArgumentList(newTypeArgList);
        }

        // Recurse into qualified names, e.g. System.Collections.Generic.IEnumerable<T>
        if (type is QualifiedNameSyntax qualifiedName)
        {
            var newRight = (SimpleNameSyntax)TransformTypeSyntax(qualifiedName.Right, originalReturnType, newReturnType);
            return qualifiedName.WithRight(newRight);
        }

        return type;
    }
}
