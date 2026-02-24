using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Rewrites LINQ method calls whose lambda arguments are async (i.e. have the <c>async</c>
/// keyword) to their async counterparts, appending "Async" to the method name, and wraps
/// the rewritten call in <c>await</c>.
/// <para>
/// Handles method chains correctly by parenthesizing the await when the call is the receiver
/// of a further chain member:
/// <code>
/// // Before:
/// items.Select(async x => await FooAsync(x)).ToList()
/// // After:
/// (await items.SelectAsync(async x => await FooAsync(x))).ToList()
/// </code>
/// </para>
/// <para>
/// Multi-async chains are also supported:
/// <code>
/// // Before:
/// items.Where(async x => await IsActiveAsync(x)).Select(async x => await MapAsync(x)).ToList()
/// // After:
/// (await (await items.WhereAsync(async x => await IsActiveAsync(x))).SelectAsync(async x => await MapAsync(x))).ToList()
/// </code>
/// </para>
/// <para>
/// The async overloads are assumed to live in the namespace supplied at construction time;
/// a <c>using</c> directive for that namespace is added to every compilation unit that
/// had at least one rewrite applied.
/// </para>
/// </summary>
public sealed class LinqAsyncOverloadRewriter : CSharpSyntaxRewriter
{
    /// <summary>
    /// LINQ methods whose lambda/func parameters are candidates for an async overload.
    /// Methods are matched by name only (extension-method receiver style) so this covers
    /// both <c>Enumerable</c> and custom extension methods with the same names.
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
    private bool _anyRewritten;

    public bool AnyRewritten => _anyRewritten;

    public LinqAsyncOverloadRewriter(string asyncLinqNamespace)
    {
        _asyncLinqNamespace = asyncLinqNamespace;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Entry point for a whole compilation unit — also adds the using directive
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
        // Visit children first so inner/nested LINQ calls are rewritten before outer ones.
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        if (!TryGetLinqMethodName(visited, out var methodName, out var memberAccess))
        {
            return visited;
        }

        // Only rewrite when at least one argument is an async lambda.
        if (!HasAsyncLambdaArgument(visited.ArgumentList))
        {
            return visited;
        }

        var asyncMethodName = methodName + "Async";

        // Replace the method name in the member-access expression.
        var newMemberAccess = memberAccess.WithName(
            memberAccess.Name.WithIdentifier(
                Identifier(asyncMethodName)
                    .WithTriviaFrom(memberAccess.Name.Identifier)));

        var rewritten = visited.WithExpression(newMemberAccess);
        _anyRewritten = true;

        // Don't double-await if the call is already directly inside an await expression.
        if (node.Parent is AwaitExpressionSyntax)
        {
            return rewritten;
        }

        var leadingTrivia = rewritten.GetLeadingTrivia();

        // If this invocation is the receiver of a further chain call or element access
        // (checked via the original tree's parent), parenthesize the await so the chain
        // continues correctly:
        //   items.SelectAsync(…).ToList()  →  (await items.SelectAsync(…)).ToList()
        if (node.Parent is MemberAccessExpressionSyntax or ElementAccessExpressionSyntax)
        {
            var awaitExpr = AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                rewritten.WithoutLeadingTrivia());
            return ParenthesizedExpression(awaitExpr).WithLeadingTrivia(leadingTrivia);
        }

        // Standalone / end-of-chain: just add await.
        return AwaitExpression(
                Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
                rewritten.WithoutLeadingTrivia())
            .WithLeadingTrivia(leadingTrivia);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when the invocation looks like a LINQ extension-method call
    /// (i.e. <c>expr.MethodName(…)</c> where <c>MethodName</c> is in <see cref="LinqMethodNames"/>).
    /// Excludes calls that already end in "Async".
    /// </summary>
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

        // Skip if already the async variant.
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
    /// Returns true when the argument list contains at least one async lambda
    /// (simple or parenthesized lambda that carries the <c>async</c> keyword).
    /// </summary>
    private static bool HasAsyncLambdaArgument(ArgumentListSyntax argumentList)
    {
        foreach (var arg in argumentList.Arguments)
        {
            if (IsAsyncLambda(arg.Expression))
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
