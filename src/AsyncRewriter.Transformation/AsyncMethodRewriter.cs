using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using AsyncRewriter.Core.Models;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Roslyn syntax rewriter that transforms synchronous methods to async.
/// Matches methods by start line and call sites by line number.
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
            return base.VisitMethodDeclaration(node);

        // Collect which call sites in this method need await
        var awaitLines = new List<int>();
        foreach (var callSite in _callSitesByLine)
        {
            if (callSite.Key >= info.StartLine && callSite.Key <= info.EndLine)
                awaitLines.Add(callSite.Key);
        }

        var hasAwaitableCalls = awaitLines.Count > 0;
        var isSyncWrapper = _syncWrapperMethodIds.Contains(info.MethodId);

        // First, visit children to transform invocations
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        // Handle out-parameter methods with special transformation
        if (info.OutParameterInfo != null)
        {
            visited = TransformOutParameterMethod(visited, node, info, hasAwaitableCalls);

            // Apply method rename if needed
            if (info.NewMethodName != null)
            {
                visited = visited.WithIdentifier(
                    Identifier(info.NewMethodName).WithTriviaFrom(visited.Identifier));
            }

            if (info.DebugLines != null)
                visited = PrependDebugComments(visited, info.DebugLines);

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

        // Transform return type — derive from syntax-level text to preserve aliases and
        // fully qualified names, rather than using the semantic-model-resolved NewReturnType
        var originalReturnType = node.ReturnType.ToString().Trim();

        // Skip wrapping if return type is already Task-based (e.g. existing async interface methods)
        if (IsAlreadyTaskType(originalReturnType))
            return base.VisitMethodDeclaration(node);

        var newReturnType = originalReturnType == "void" ? "Task" : $"Task<{originalReturnType}>";
        var newReturnTypeSyntax = ParseTypeName(newReturnType).WithTriviaFrom(visited.ReturnType);

        visited = visited.WithReturnType(newReturnTypeSyntax);

        if (isSyncWrapper)
        {
            // Sync wrappers: just change the return type, don't add async/await
            // The wrapper body will be removed/rewritten separately
        }
        else if (hasAwaitableCalls)
        {
            // Add async modifier
            visited = AddAsyncModifier(visited);
        }
        else
        {
            // No awaitable calls but method is flooded — wrap return values
            visited = TransformBodyForNoAwait(visited, originalReturnType, newReturnType);
        }

        // Apply method rename if the async interface uses a different method name
        var effectiveMethodName = info.MethodName;
        if (info.NewMethodName != null)
        {
            visited = visited.WithIdentifier(
                Identifier(info.NewMethodName).WithTriviaFrom(visited.Identifier));
            effectiveMethodName = info.NewMethodName;
        }

        if (info.DebugLines != null)
        {
            visited = PrependDebugComments(visited, info.DebugLines);
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
            return base.VisitLocalFunctionStatement(node);

        var awaitLines = new List<int>();
        foreach (var callSite in _callSitesByLine)
        {
            if (callSite.Key >= info.StartLine && callSite.Key <= info.EndLine)
                awaitLines.Add(callSite.Key);
        }

        var hasAwaitableCalls = awaitLines.Count > 0;

        // First, visit children to transform invocations
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;

        // Transform return type
        var originalReturnType = node.ReturnType.ToString().Trim();

        // Skip wrapping if return type is already Task-based (e.g. existing async interface methods)
        if (IsAlreadyTaskType(originalReturnType))
            return base.VisitLocalFunctionStatement(node);

        var newReturnType = originalReturnType == "void" ? "Task" : $"Task<{originalReturnType}>";
        var newReturnTypeSyntax = ParseTypeName(newReturnType).WithTriviaFrom(visited.ReturnType);

        visited = visited.WithReturnType(newReturnTypeSyntax);

        if (hasAwaitableCalls)
        {
            visited = AddAsyncModifierToLocalFunction(visited);
        }
        else
        {
            visited = TransformLocalFunctionBodyForNoAwait(visited, originalReturnType, newReturnType);
        }

        if (info.DebugLines != null)
        {
            visited = PrependDebugComments(visited, info.DebugLines);
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

    private static MethodDeclarationSyntax PrependDebugComments(MethodDeclarationSyntax method, List<string> debugLines)
    {
        var commentTrivia = BuildDebugCommentTrivia(method.GetLeadingTrivia(), debugLines);
        return method.WithLeadingTrivia(commentTrivia);
    }

    private static LocalFunctionStatementSyntax PrependDebugComments(LocalFunctionStatementSyntax func, List<string> debugLines)
    {
        var commentTrivia = BuildDebugCommentTrivia(func.GetLeadingTrivia(), debugLines);
        return func.WithLeadingTrivia(commentTrivia);
    }

    private static SyntaxTriviaList BuildDebugCommentTrivia(SyntaxTriviaList existingLeading, List<string> debugLines)
    {
        var triviaList = new List<SyntaxTrivia>();

        // Preserve leading whitespace/newlines, then insert comments before the indentation of the method
        // Find the last whitespace trivia (the indentation before the method)
        var indentation = "";
        for (var i = existingLeading.Count - 1; i >= 0; i--)
        {
            if (existingLeading[i].IsKind(SyntaxKind.WhitespaceTrivia))
            {
                indentation = existingLeading[i].ToString();
                break;
            }
        }

        // Add all existing leading trivia except the last whitespace (we'll re-add it)
        for (var i = 0; i < existingLeading.Count; i++)
        {
            // Skip the last whitespace trivia — we'll add it after comments
            if (i == existingLeading.Count - 1 && existingLeading[i].IsKind(SyntaxKind.WhitespaceTrivia))
                continue;
            triviaList.Add(existingLeading[i]);
        }

        // Add debug comment lines
        foreach (var line in debugLines)
        {
            triviaList.Add(Whitespace(indentation));
            triviaList.Add(Comment($"// [async-rewriter] {line}"));
            triviaList.Add(LineFeed);
        }

        // Re-add the indentation for the method itself
        triviaList.Add(Whitespace(indentation));

        return TriviaList(triviaList);
    }

    private static LocalFunctionStatementSyntax AddAsyncModifierToLocalFunction(LocalFunctionStatementSyntax func)
    {
        if (func.Modifiers.Any(SyntaxKind.AsyncKeyword))
            return func;

        if (func.Modifiers.Count == 0)
        {
            var leadingTrivia = func.ReturnType.GetLeadingTrivia();
            var asyncToken = Token(SyntaxKind.AsyncKeyword)
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(Space);
            var newReturnType = func.ReturnType.WithoutLeadingTrivia();
            return func
                .WithModifiers(TokenList(asyncToken))
                .WithReturnType(newReturnType);
        }
        else
        {
            var asyncToken = Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space);
            var newModifiers = func.Modifiers.Add(asyncToken);
            return func.WithModifiers(newModifiers);
        }
    }

    private static LocalFunctionStatementSyntax TransformLocalFunctionBodyForNoAwait(
        LocalFunctionStatementSyntax func,
        string originalReturnType,
        string newReturnType)
    {
        if (func.Body == null && func.ExpressionBody == null)
            return func;

        if (originalReturnType == "void")
        {
            // Similar to TransformVoidMethodNoAwait but for local functions
            if (func.Body != null)
            {
                var rewriter = new BareReturnRewriter();
                var newBody = (BlockSyntax)rewriter.Visit(func.Body);

                var returnStatement = ReturnStatement(
                    MakeTaskCompletedTaskExpression()
                        .WithLeadingTrivia(Space))
                    .WithLeadingTrivia(Whitespace("        "))
                    .WithTrailingTrivia(CarriageReturnLineFeed);

                newBody = newBody.AddStatements(returnStatement);
                return func.WithBody(newBody);
            }
        }
        else
        {
            var rewriter = new ReturnValueWrapper(originalReturnType);
            if (func.ExpressionBody != null)
            {
                var wrappedExpr = (ExpressionSyntax)rewriter.Visit(func.ExpressionBody.Expression);
                return func.WithExpressionBody(func.ExpressionBody.WithExpression(wrappedExpr));
            }
            if (func.Body != null)
            {
                var newBody = (BlockSyntax)rewriter.Visit(func.Body);
                return func.WithBody(newBody);
            }
        }

        return func;
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;

        if (visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
            return visited;

        if (ContainsDirectAwait(visited.Body))
        {
            return visited.WithAsyncKeyword(
                Token(SyntaxKind.AsyncKeyword)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                    .WithTrailingTrivia(Space))
                .WithParameter(visited.Parameter.WithoutLeadingTrivia());
        }

        return visited;
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;

        if (visited.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
            return visited;

        if (ContainsDirectAwait(visited.Body))
        {
            return visited.WithAsyncKeyword(
                Token(SyntaxKind.AsyncKeyword)
                    .WithLeadingTrivia(visited.GetLeadingTrivia())
                    .WithTrailingTrivia(Space))
                .WithParameterList(visited.ParameterList.WithoutLeadingTrivia());
        }

        return visited;
    }

    /// <summary>
    /// Checks if the given node contains any AwaitExpression that is a direct child
    /// (not nested inside another lambda or local function).
    /// </summary>
    private static bool ContainsDirectAwait(SyntaxNode? node)
    {
        if (node == null)
            return false;

        foreach (var descendant in node.DescendantNodesAndSelf(n =>
            n is not SimpleLambdaExpressionSyntax
            and not ParenthesizedLambdaExpressionSyntax
            and not LocalFunctionStatementSyntax))
        {
            if (descendant is AwaitExpressionSyntax)
                return true;
        }

        return false;
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1; // 1-based
        if (!_callSitesByLine.TryGetValue(line, out var callSiteInfo))
            return visited;

        // Wrap with await — but only if not already awaited
        if (visited.Parent is AwaitExpressionSyntax)
            return visited;

        // Create await expression: move leading trivia to outer await, add space before invocation
        var leadingTrivia = visited.GetLeadingTrivia();
        var awaitExpr = AwaitExpression(
            Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
            visited.WithoutLeadingTrivia());
        return awaitExpr.WithLeadingTrivia(leadingTrivia);
    }

    /// <summary>
    /// Transforms a method with out parameters for async: removes out params, changes return type,
    /// and rewrites the body to construct AsyncOutResult or tuple returns.
    /// </summary>
    private static MethodDeclarationSyntax TransformOutParameterMethod(
        MethodDeclarationSyntax visited,
        MethodDeclarationSyntax originalNode,
        MethodTransformInfo info,
        bool hasAwaitableCalls)
    {
        var outInfo = info.OutParameterInfo!;

        // 1. Remove out parameters from parameter list
        var outIndicesSet = new HashSet<int>(outInfo.OutParameterIndices);
        var newParams = new List<ParameterSyntax>();
        for (int i = 0; i < visited.ParameterList.Parameters.Count; i++)
        {
            if (!outIndicesSet.Contains(i))
                newParams.Add(visited.ParameterList.Parameters[i]);
        }
        visited = visited.WithParameterList(
            ParameterList(SeparatedList(newParams))
                .WithTriviaFrom(visited.ParameterList));

        // 2. Change return type
        var newReturnTypeSyntax = ParseTypeName(outInfo.NewAsyncReturnType)
            .WithTriviaFrom(visited.ReturnType);
        visited = visited.WithReturnType(newReturnTypeSyntax);

        // 3. Rewrite body: transform return statements and out assignments
        if (hasAwaitableCalls)
        {
            visited = AddAsyncModifier(visited);
        }

        if (visited.Body != null)
        {
            var bodyRewriter = new OutParameterReturnRewriter(outInfo);
            var newBody = (BlockSyntax)bodyRewriter.Visit(visited.Body);
            visited = visited.WithBody(newBody);
        }
        else if (visited.ExpressionBody != null)
        {
            // Expression-bodied method with out params is unusual but handle it
            var bodyRewriter = new OutParameterReturnRewriter(outInfo);
            var wrappedExpr = (ExpressionSyntax)bodyRewriter.VisitExpressionForReturn(visited.ExpressionBody.Expression);
            visited = visited.WithExpressionBody(visited.ExpressionBody.WithExpression(wrappedExpr));
        }

        return visited;
    }

    /// <summary>
    /// Rewrites return statements and out parameter assignments in method bodies
    /// for out-parameter async transformation.
    /// </summary>
    private class OutParameterReturnRewriter : CSharpSyntaxRewriter
    {
        private readonly OutParameterTransformInfo _outInfo;

        public OutParameterReturnRewriter(OutParameterTransformInfo outInfo)
        {
            _outInfo = outInfo;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
                return node;

            var wrappedExpr = WrapReturnExpression(node.Expression);
            return node.WithExpression(wrappedExpr.WithLeadingTrivia(node.Expression.GetLeadingTrivia()));
        }

        public ExpressionSyntax VisitExpressionForReturn(ExpressionSyntax expr)
        {
            return WrapReturnExpression(expr);
        }

        private ExpressionSyntax WrapReturnExpression(ExpressionSyntax returnExpr)
        {
            if (_outInfo.IsTryPattern)
            {
                // Wrap: return Task.FromResult(new AsyncOutResult<T>(outValue, boolResult))
                // For single out param: new AsyncOutResult<Type>(outName, returnExpr)
                // For multiple out params: new AsyncOutResult<(Type1 n1, Type2 n2)>((n1, n2), returnExpr)
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
                    var tupleArgs = _outInfo.OutParameterNames
                        .Select(n => Argument(IdentifierName(n)));
                    valueArg = TupleExpression(SeparatedList(tupleArgs));
                }

                var newExpr = ObjectCreationExpression(
                    ParseTypeName($"AsyncOutResult<{innerType}>").WithLeadingTrivia(Space))
                    .WithArgumentList(ArgumentList(SeparatedList(new[]
                    {
                        Argument(valueArg),
                        Argument(returnExpr.WithoutLeadingTrivia()).WithLeadingTrivia(Space)
                    })));

                return WrapInTaskFromResult(newExpr, $"AsyncOutResult<{innerType}>");
            }
            else
            {
                // Tuple pattern: return Task.FromResult((returnExpr, out1, out2, ...))
                var tupleArgs = new List<ArgumentSyntax>
                {
                    Argument(returnExpr.WithoutLeadingTrivia())
                };
                foreach (var name in _outInfo.OutParameterNames)
                    tupleArgs.Add(Argument(IdentifierName(name)));

                var tupleExpr = TupleExpression(SeparatedList(tupleArgs));

                // Build the tuple type string for Task.FromResult
                var elements = new List<string> { $"{returnExpr}" };
                // We don't know the original return type string from here, just use the tuple directly
                return WrapInTaskFromResult(tupleExpr);
            }
        }

        private static ExpressionSyntax WrapInTaskFromResult(ExpressionSyntax expr, string? typeArg = null)
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
                                    SingletonSeparatedList(
                                        ParseTypeName(typeArg))))))
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

    private static MethodDeclarationSyntax AddAsyncModifier(MethodDeclarationSyntax method)
    {
        if (method.Modifiers.Any(SyntaxKind.AsyncKeyword))
            return method;

        if (method.Modifiers.Count == 0)
        {
            // No existing modifiers: move return type's leading trivia to async token
            var leadingTrivia = method.ReturnType.GetLeadingTrivia();
            var asyncToken = Token(SyntaxKind.AsyncKeyword)
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(Space);
            var newReturnType = method.ReturnType.WithoutLeadingTrivia();
            return method
                .WithModifiers(TokenList(asyncToken))
                .WithReturnType(newReturnType);
        }
        else
        {
            // Has existing modifiers: add async after last modifier
            var asyncToken = Token(SyntaxKind.AsyncKeyword).WithTrailingTrivia(Space);
            var newModifiers = method.Modifiers.Add(asyncToken);
            return method.WithModifiers(newModifiers);
        }
    }

    private static MethodDeclarationSyntax TransformBodyForNoAwait(
        MethodDeclarationSyntax method,
        string originalReturnType,
        string newReturnType)
    {
        if (method.Body == null && method.ExpressionBody == null)
            return method;

        if (originalReturnType == "void")
        {
            return TransformVoidMethodNoAwait(method);
        }

        return TransformReturningMethodNoAwait(method, originalReturnType);
    }

    /// <summary>
    /// void method with no awaitable calls: transform bare "return;" to "return Task.CompletedTask;"
    /// and append "return Task.CompletedTask;" at end
    /// </summary>
    private static MethodDeclarationSyntax TransformVoidMethodNoAwait(MethodDeclarationSyntax method)
    {
        if (method.ExpressionBody != null)
        {
            // expression-bodied void method: e.g. void Foo() => Bar();
            // transform to: Task Foo() { Bar(); return Task.CompletedTask; }
            var exprStatement = ExpressionStatement(method.ExpressionBody.Expression)
                .WithLeadingTrivia(Whitespace("        "))
                .WithTrailingTrivia(CarriageReturnLineFeed);

            var returnStatement = ReturnStatement(
                MakeTaskCompletedTaskExpression()
                    .WithLeadingTrivia(Space))
                .WithLeadingTrivia(Whitespace("        "))
                .WithTrailingTrivia(CarriageReturnLineFeed);

            var body = Block(exprStatement, returnStatement);
            return method
                .WithExpressionBody(null)
                .WithSemicolonToken(Token(SyntaxKind.None))
                .WithBody(body);
        }

        if (method.Body != null)
        {
            // First, rewrite existing bare "return;" statements to "return Task.CompletedTask;"
            var rewriter = new BareReturnRewriter();
            var newBody = (BlockSyntax)rewriter.Visit(method.Body);

            var returnStatement = ReturnStatement(
                MakeTaskCompletedTaskExpression()
                    .WithLeadingTrivia(Space))
                .WithLeadingTrivia(Whitespace("        "))
                .WithTrailingTrivia(CarriageReturnLineFeed);

            newBody = newBody.AddStatements(returnStatement);
            return method.WithBody(newBody);
        }

        return method;
    }

    private static ExpressionSyntax MakeTaskCompletedTaskExpression()
    {
        return MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            IdentifierName("Task"),
            IdentifierName("CompletedTask"));
    }

    /// <summary>
    /// Rewrites bare "return;" statements to "return Task.CompletedTask;"
    /// </summary>
    private class BareReturnRewriter : CSharpSyntaxRewriter
    {
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
            {
                return node.WithExpression(
                    MakeTaskCompletedTaskExpression()
                        .WithLeadingTrivia(Space));
            }
            return base.VisitReturnStatement(node);
        }
    }

    /// <summary>
    /// Non-void method with no awaitable calls: wrap return expressions with Task.FromResult
    /// </summary>
    private static MethodDeclarationSyntax TransformReturningMethodNoAwait(
        MethodDeclarationSyntax method,
        string originalReturnType)
    {
        var rewriter = new ReturnValueWrapper(originalReturnType);

        if (method.ExpressionBody != null)
        {
            var wrappedExpr = (ExpressionSyntax)rewriter.Visit(method.ExpressionBody.Expression);
            return method.WithExpressionBody(method.ExpressionBody.WithExpression(wrappedExpr));
        }

        if (method.Body != null)
        {
            var newBody = (BlockSyntax)rewriter.Visit(method.Body);
            return method.WithBody(newBody);
        }

        return method;
    }

    private static bool IsAlreadyTaskType(string returnType)
    {
        return returnType == "Task"
            || returnType.StartsWith("Task<")
            || returnType == "System.Threading.Tasks.Task"
            || returnType.StartsWith("System.Threading.Tasks.Task<")
            || returnType == "ValueTask"
            || returnType.StartsWith("ValueTask<")
            || returnType.StartsWith("System.Threading.Tasks.ValueTask");
    }

    /// <summary>
    /// Wraps return value expressions with Task.FromResult
    /// </summary>
    private class ReturnValueWrapper : CSharpSyntaxRewriter
    {
        private readonly string _originalReturnType;

        public ReturnValueWrapper(string originalReturnType)
        {
            _originalReturnType = originalReturnType;
        }

        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node)
        {
            if (node.Expression == null)
                return node;

            var taskFromResult = InvocationExpression(
                MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    IdentifierName("Task"),
                    GenericName(Identifier("FromResult"))
                        .WithTypeArgumentList(
                            TypeArgumentList(
                                SingletonSeparatedList(
                                    ParseTypeName(_originalReturnType))))))
                .WithArgumentList(
                    ArgumentList(
                        SingletonSeparatedList(
                            Argument(node.Expression.WithoutLeadingTrivia()))))
                .WithLeadingTrivia(node.Expression.GetLeadingTrivia());

            return node.WithExpression(taskFromResult);
        }
    }
}

/// <summary>
/// Info about a method that needs transformation, keyed by start line
/// </summary>
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

/// <summary>
/// Describes how out parameters should be transformed for an async method.
/// </summary>
public class OutParameterTransformInfo
{
    /// <summary>Whether this is a bool-return Try* pattern or a tuple pattern.</summary>
    public required bool IsTryPattern { get; init; }
    /// <summary>Indices of out parameters in the original parameter list.</summary>
    public required List<int> OutParameterIndices { get; init; }
    /// <summary>Types of the out parameters.</summary>
    public required List<string> OutParameterTypes { get; init; }
    /// <summary>Names of the out parameters.</summary>
    public required List<string> OutParameterNames { get; init; }
    /// <summary>The new async return type (already includes Task wrapper).</summary>
    public required string NewAsyncReturnType { get; init; }
}

/// <summary>
/// Info about a call site that needs await, keyed by line number
/// </summary>
public class CallSiteInfo
{
    public required string CalleeMethodId { get; init; }
    public required int LineNumber { get; init; }
}
