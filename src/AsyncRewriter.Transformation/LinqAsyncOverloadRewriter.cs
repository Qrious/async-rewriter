using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Rewrites LINQ method calls whose func/lambda arguments resolve to Task-returning
/// delegate types to their async counterparts, appending "Async" to the method name,
/// and wraps the rewritten call in <c>await</c>.
///
/// <para>
/// Detection uses the Roslyn <see cref="SemanticModel"/> to inspect the <em>converted
/// delegate type</em> of each argument rather than just looking for the <c>async</c>
/// keyword.  This catches all three forms:
/// <list type="bullet">
///   <item><c>async x =&gt; await FooAsync(x)</c> — explicit async lambda</item>
///   <item><c>x =&gt; FooAsync(x)</c> — implicit Task-returning lambda (no async keyword)</item>
///   <item><c>FooAsync</c> — method group whose signature returns Task</item>
/// </list>
/// </para>
///
/// <para>
/// Handles method chains correctly by parenthesizing the await when the call is the
/// receiver of a further chain member:
/// <code>
/// items.Select(x => FooAsync(x)).ToList()
/// → (await items.SelectAsync(x => FooAsync(x))).ToList()
/// </code>
/// </para>
/// <para>
/// A <c>using</c> directive for the supplied async LINQ namespace is added to every
/// compilation unit that had at least one rewrite applied.
/// </para>
/// </summary>
public sealed class LinqAsyncOverloadRewriter : CSharpSyntaxRewriter
{
    /// <summary>
    /// LINQ methods whose func/delegate parameters are candidates for an async overload.
    /// </summary>
    private static readonly HashSet<string> LinqMethodNames = new(StringComparer.Ordinal)
    {
        "Select",
        "SelectMany",
        "Where",
        "Any",
        "All",
        "Count",
        "LongCount",
        "First",
        "FirstOrDefault",
        "Single",
        "SingleOrDefault",
        "Last",
        "LastOrDefault",
        "OrderBy",
        "OrderByDescending",
        "ThenBy",
        "ThenByDescending",
        "GroupBy",
        "GroupJoin",
        "Join",
        "Zip",
        "Aggregate",
        "ForEach",
        "Sum",
        "Min",
        "Max",
        "Average",
    };

    private readonly string _asyncLinqNamespace;
    private readonly SemanticModel _semanticModel;
    private bool _anyRewritten;

    public bool AnyRewritten => _anyRewritten;

    public LinqAsyncOverloadRewriter(string asyncLinqNamespace, SemanticModel semanticModel)
    {
        _asyncLinqNamespace = asyncLinqNamespace;
        _semanticModel = semanticModel;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Entry point — adds using directive when anything was rewritten
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitCompilationUnit(CompilationUnitSyntax node)
    {
        var visited = (CompilationUnitSyntax)base.VisitCompilationUnit(node)!;

        if (_anyRewritten)
        {
            visited = EnsureUsingDirective(visited, _asyncLinqNamespace);
        }

        return visited;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Invocation rewriting
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Visit children first so nested LINQ chains are rewritten inside-out.
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        if (!TryGetLinqMethodName(visited, out var methodName, out var memberAccess))
        {
            return visited;
        }

        // Use the original node for semantic queries — the model is bound to the original tree.
        if (!HasTaskReturningFuncArgument(node.ArgumentList))
        {
            return visited;
        }

        var asyncMethodName = methodName + "Async";

        var newMemberAccess = memberAccess.WithName(
            memberAccess.Name.WithIdentifier(
                Identifier(asyncMethodName)
                    .WithTriviaFrom(memberAccess.Name.Identifier)));

        var rewritten = visited.WithExpression(newMemberAccess);
        _anyRewritten = true;

        // Don't double-await if already directly inside an await expression.
        if (node.Parent is AwaitExpressionSyntax)
        {
            return rewritten;
        }

        var leadingTrivia = rewritten.GetLeadingTrivia();

        // Parenthesize when the result feeds into a further chain or element access:
        //   items.SelectAsync(…).ToList()  →  (await items.SelectAsync(…)).ToList()
        if (node.Parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
        {
            var awaitExpr = AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                rewritten.WithoutLeadingTrivia());
            return ParenthesizedExpression(awaitExpr).WithLeadingTrivia(leadingTrivia);
        }

        return AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                rewritten.WithoutLeadingTrivia())
            .WithLeadingTrivia(leadingTrivia);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Detection helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static bool TryGetLinqMethodName(
        InvocationExpressionSyntax invocation,
        out string methodName,
        out MemberAccessExpressionSyntax memberAccess)
    {
        methodName = string.Empty;
        memberAccess = null!;

        if (invocation.Expression is not MemberAccessExpressionSyntax ma)
        {
            return false;
        }

        var name = ma.Name.Identifier.ValueText;

        if (name.EndsWith("Async", StringComparison.Ordinal))
        {
            return false;
        }

        if (!LinqMethodNames.Contains(name))
        {
            return false;
        }

        methodName = name;
        memberAccess = ma;
        return true;
    }

    /// <summary>
    /// Returns true when at least one argument converts to a delegate type whose
    /// return type is <c>Task</c> or <c>Task&lt;T&gt;</c> (or ValueTask equivalents).
    /// This covers async lambdas, plain Task-returning lambdas, and method groups.
    /// </summary>
    private bool HasTaskReturningFuncArgument(ArgumentListSyntax argumentList)
    {
        foreach (var arg in argumentList.Arguments)
        {
            if (ArgumentHasTaskReturningDelegate(arg.Expression))
            {
                return true;
            }
        }

        return false;
    }

    private bool ArgumentHasTaskReturningDelegate(ExpressionSyntax expr)
    {
        // Fast path: lambda already carries async keyword — no semantic query needed.
        if (IsAsyncLambda(expr))
        {
            return true;
        }

        // For lambdas: inspect the body's actual return type rather than the converted
        // delegate type.  The converted type is unreliable here because the code we are
        // looking at is deliberately mis-typed — the lambda returns Task<T> but the LINQ
        // overload still expects a plain Func<T, TResult>.  Roslyn therefore can't resolve
        // a converted type, but it can still type-check the body expression in isolation.
        return expr switch
        {
            SimpleLambdaExpressionSyntax simple =>
                IsTaskReturningBody(simple.Body),
            ParenthesizedLambdaExpressionSyntax paren =>
                IsTaskReturningBody(paren.Body),
            // Method group: check the candidate symbols' return types.
            IdentifierNameSyntax or MemberAccessExpressionSyntax =>
                IsTaskReturningMethodGroup(expr),
            _ => false
        };
    }

    /// <summary>
    /// Returns true when a lambda body (expression or block) evaluates to / returns a
    /// Task-like type.
    /// </summary>
    private bool IsTaskReturningBody(CSharpSyntaxNode body)
    {
        // Expression-bodied lambda: check the expression's type directly.
        if (body is ExpressionSyntax expr)
        {
            return IsTaskLike(_semanticModel.GetTypeInfo(expr).Type);
        }

        // Block-bodied lambda: look for any return statement whose expression is Task-like.
        foreach (var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>())
        {
            if (ret.Expression != null
                && IsTaskLike(_semanticModel.GetTypeInfo(ret.Expression).Type))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when a method-group expression resolves to at least one overload
    /// whose return type is Task-like.
    /// </summary>
    private bool IsTaskReturningMethodGroup(ExpressionSyntax expr)
    {
        var symbolInfo = _semanticModel.GetSymbolInfo(expr);

        var candidates = symbolInfo.Symbol != null
            ? [symbolInfo.Symbol]
            : symbolInfo.CandidateSymbols;

        foreach (var candidate in candidates)
        {
            if (candidate is IMethodSymbol method && IsTaskLike(method.ReturnType))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAsyncLambda(ExpressionSyntax expr) =>
        expr switch
        {
            SimpleLambdaExpressionSyntax lambda => lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            ParenthesizedLambdaExpressionSyntax lambda => lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            AnonymousMethodExpressionSyntax anon => anon.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword),
            _ => false
        };

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

    // ──────────────────────────────────────────────────────────────────────────
    // Using-directive helper
    // ──────────────────────────────────────────────────────────────────────────

    private static CompilationUnitSyntax EnsureUsingDirective(CompilationUnitSyntax root, string namespaceName)
    {
        if (root.Usings.Any(u => u.Name?.ToString() == namespaceName))
        {
            return root;
        }

        var usingDirective = UsingDirective(ParseName(namespaceName).WithLeadingTrivia(Space))
            .WithTrailingTrivia(LineFeed);

        return root.AddUsings(usingDirective);
    }
}
