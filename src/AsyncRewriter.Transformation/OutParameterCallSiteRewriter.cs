using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Rewrites call sites of methods whose out parameters have been removed during async transformation.
///
/// For Try* pattern (bool return + out params):
///   Before: if (obj.TryGetValue(key, out var x)) { Use(x); }
///   After:  var __tryGetValueResult = await obj.TryGetValueAsync(key);
///           if (__tryGetValueResult.TryGetValue(out var x)) { Use(x); }
///
/// For tuple pattern (non-bool return + out params):
///   Before: var r = obj.Process(out var s);
///   After:  var (__processResult, s) = await obj.ProcessAsync();
///           var r = __processResult;
/// </summary>
public class OutParameterCallSiteRewriter : CSharpSyntaxRewriter
{
    private readonly Dictionary<int, OutParameterCallSiteInfo> _callSitesByLine;
    private readonly List<(StatementSyntax Original, List<StatementSyntax> Replacements)> _replacements = new();

    public OutParameterCallSiteRewriter(Dictionary<int, OutParameterCallSiteInfo> callSitesByLine)
    {
        _callSitesByLine = callSitesByLine;
    }

    public bool AnyTransformed => _replacements.Count > 0;

    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var newStatements = new List<StatementSyntax>();
        bool anyChanged = false;

        foreach (var statement in node.Statements)
        {
            var replacement = TryTransformStatement(statement);
            if (replacement != null)
            {
                newStatements.AddRange(replacement);
                anyChanged = true;
            }
            else
            {
                // Still visit children for nested blocks
                var visited = (StatementSyntax)Visit(statement)!;
                newStatements.Add(visited);
            }
        }

        return anyChanged ? node.WithStatements(List(newStatements)) : base.VisitBlock(node);
    }

    private List<StatementSyntax>? TryTransformStatement(StatementSyntax statement)
    {
        // Find invocations in this statement that match our call sites
        foreach (var invocation in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (!_callSitesByLine.TryGetValue(line, out var callSiteInfo))
            {
                continue;
            }

            // Verify the invocation's method name matches the expected call site
            var invokedName = GetInvokedMethodName(invocation);
            if (invokedName != null && !MethodNameMatches(invokedName, callSiteInfo.MethodName))
            {
                continue;
            }

            if (callSiteInfo.IsTryPattern)
            {
                return TransformTryPatternCallSite(statement, invocation, callSiteInfo);
            }
            else
            {
                return TransformTuplePatternCallSite(statement, invocation, callSiteInfo);
            }
        }

        return null;
    }

    private List<StatementSyntax>? TransformTryPatternCallSite(
        StatementSyntax statement,
        InvocationExpressionSyntax invocation,
        OutParameterCallSiteInfo info)
    {
        var results = new List<StatementSyntax>();
        var resultVarName = $"__{ToCamelCase(info.MethodName)}Result";

        // Build new argument list without out params
        var newArgs = RemoveOutArguments(invocation.ArgumentList, info.OutParameterIndices);
        var newInvocation = invocation
            .WithArgumentList(ArgumentList(SeparatedList(newArgs)));

        // var __result = await method(args);
        var awaitExpr = AwaitExpression(
            Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
            newInvocation);
        var resultDecl = LocalDeclarationStatement(
            VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(
                    VariableDeclarator(Identifier(resultVarName).WithLeadingTrivia(Space))
                        .WithInitializer(EqualsValueClause(awaitExpr).WithLeadingTrivia(Space)))))
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia());
        results.Add(resultDecl);

        // Replace the invocation in the original statement with __result.TryGetValue(out var x)
        var tryGetCall = InvocationExpression(
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                IdentifierName(resultVarName),
                IdentifierName("TryGetValue")))
            .WithArgumentList(BuildOutArgumentList(info));

        var newStatement = statement.ReplaceNode(invocation, tryGetCall);
        results.Add(newStatement);

        return results;
    }

    private List<StatementSyntax>? TransformTuplePatternCallSite(
        StatementSyntax statement,
        InvocationExpressionSyntax invocation,
        OutParameterCallSiteInfo info)
    {
        var results = new List<StatementSyntax>();
        var resultVarName = $"__{ToCamelCase(info.MethodName)}Result";

        // Build new argument list without out params
        var newArgs = RemoveOutArguments(invocation.ArgumentList, info.OutParameterIndices);
        var newInvocation = invocation
            .WithArgumentList(ArgumentList(SeparatedList(newArgs)));

        // var (__result, outName1, outName2) = await method(args);
        var awaitExpr = AwaitExpression(
            Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
            newInvocation);

        var tupleElements = new List<ArgumentSyntax>
        {
            Argument(DeclarationExpression(
                IdentifierName("var"),
                SingleVariableDesignation(Identifier(resultVarName))))
        };
        foreach (var name in info.OutParameterNames)
        {
            tupleElements.Add(Argument(IdentifierName(name)));
        }

        // Use a deconstruction: var (__result, s) = await ...
        var deconstructDecl = LocalDeclarationStatement(
            VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(
                    VariableDeclarator(Identifier($"({resultVarName}, {string.Join(", ", info.OutParameterNames)})").WithLeadingTrivia(Space))
                        .WithInitializer(EqualsValueClause(awaitExpr).WithLeadingTrivia(Space)))))
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia());
        results.Add(deconstructDecl);

        // Replace the invocation result usage if in an assignment/declaration
        // For simplicity, replace the original invocation with just the result var
        var newStatement = statement.ReplaceNode(invocation,
            IdentifierName(resultVarName).WithTriviaFrom(invocation));
        results.Add(newStatement);

        return results;
    }

    private static List<ArgumentSyntax> RemoveOutArguments(
        ArgumentListSyntax argList, List<int> outIndices)
    {
        var outSet = new HashSet<int>(outIndices);
        var result = new List<ArgumentSyntax>();
        for (int i = 0; i < argList.Arguments.Count; i++)
        {
            if (!outSet.Contains(i))
            {
                result.Add(argList.Arguments[i]);
            }
        }
        return result;
    }

    private static ArgumentListSyntax BuildOutArgumentList(OutParameterCallSiteInfo info)
    {
        if (info.OutParameterNames.Count == 1)
        {
            return ArgumentList(SingletonSeparatedList(
                Argument(
                    null,
                    Token(SyntaxKind.OutKeyword).WithTrailingTrivia(Space),
                    DeclarationExpression(
                        IdentifierName("var").WithTrailingTrivia(Space),
                        SingleVariableDesignation(Identifier(info.OutParameterNames[0]))))));
        }

        var args = info.OutParameterNames.Select(name =>
            Argument(
                null,
                Token(SyntaxKind.OutKeyword).WithTrailingTrivia(Space),
                DeclarationExpression(
                    IdentifierName("var").WithTrailingTrivia(Space),
                    SingleVariableDesignation(Identifier(name))))).ToList();
        return ArgumentList(SeparatedList(args));
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation)
    {
        return invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.Text,
            IdentifierNameSyntax identifier => identifier.Identifier.Text,
            _ => null
        };
    }

    /// <summary>
    /// Checks if the invoked method name matches the expected call site method name.
    /// The call site MethodName may be the async version (e.g., "TryGetAsync") while
    /// the invocation may still use the original sync name (e.g., "TryGet").
    /// </summary>
    private static bool MethodNameMatches(string invokedName, string callSiteMethodName)
    {
        if (string.Equals(invokedName, callSiteMethodName, StringComparison.Ordinal))
        {
            return true;
        }

        // callSiteMethodName might be the async variant: check if adding "Async" to invokedName matches
        if (string.Equals(invokedName + "Async", callSiteMethodName, StringComparison.Ordinal))
        {
            return true;
        }

        // Or the invokedName might already be the async variant
        if (string.Equals(invokedName, callSiteMethodName + "Async", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}

/// <summary>
/// Information about a call site to an out-parameter method that needs transformation.
/// </summary>
public class OutParameterCallSiteInfo
{
    public required string MethodName { get; init; }
    public required bool IsTryPattern { get; init; }
    public required List<int> OutParameterIndices { get; init; }
    public required List<string> OutParameterNames { get; init; }
    public required int LineNumber { get; init; }
}
