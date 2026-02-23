using System.Collections.Generic;
using System.Linq;
using AsyncRewriter.Core;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Rewrites call sites of methods whose out parameters have been removed during async transformation.
/// Uses a <see cref="SemanticModel"/> and the flooded call graph (with <see cref="OutParameterMetadata"/>)
/// to identify call sites by symbol rather than by source line.
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
    private readonly SemanticModel _semanticModel;

    /// <summary>
    /// Out-parameter metadata keyed by call-graph method ID (callee).
    /// Only contains entries for methods with <see cref="OutParameterTransformKind"/> != None.
    /// </summary>
    private readonly IReadOnlyDictionary<string, OutParameterMetadata> _outParamMethodsById;

    /// <summary>
    /// Set of flooded method IDs.  We only transform call sites that reside
    /// inside a flooded method (the caller must itself be async-transformed).
    /// </summary>
    private readonly IReadOnlySet<string> _floodedMethodIds;

    /// <summary>
    /// Scope stack tracking the current enclosing method / local function.
    /// Each frame holds the method ID when inside a flooded method, or <c>null</c>
    /// when inside a non-flooded method.  Lambdas do not push a frame.
    /// </summary>
    private readonly Stack<string?> _scopeStack = new();

    private readonly List<(StatementSyntax Original, List<StatementSyntax> Replacements)> _replacements = new();
    private bool _usedBoolTryPattern;

    public OutParameterCallSiteRewriter(
        SemanticModel semanticModel,
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph)
    {
        _semanticModel = semanticModel;

        // Build out-param method lookup: method ID → OutParameterMetadata
        // (only entries where the method actually has out-parameter transformation)
        var outParamMethods = new Dictionary<string, OutParameterMetadata>();
        foreach (var (methodId, composite) in callGraph.MethodMetadata)
        {
            var outMeta = composite.Fourth;
            if (outMeta.TransformKind != OutParameterTransformKind.None)
            {
                outParamMethods[methodId] = outMeta;
            }
        }
        _outParamMethodsById = outParamMethods;

        // Build flooded method set: methods with non-empty FloodingMethodMetadata
        _floodedMethodIds = new HashSet<string>(
            callGraph.MethodMetadata
                .Where(kvp => !string.IsNullOrEmpty(kvp.Value.First.OriginalReturnType))
                .Select(kvp => kvp.Key));
    }

    public bool AnyTransformed => _replacements.Count > 0;

    /// <summary>
    /// True if at least one <see cref="OutParameterTransformKind.BoolTryPattern"/> call site was rewritten.
    /// When true the caller should ensure a <c>using</c> directive for the namespace that contains
    /// <c>AsyncOutResult&lt;T&gt;</c> is present in the file.
    /// </summary>
    public bool UsedBoolTryPattern => _usedBoolTryPattern;

    // ──────────────────────────────────────────────────────────────────────────
    // Scope tracking for methods / local functions
    // ──────────────────────────────────────────────────────────────────────────

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var methodId = ResolveMethodId(node);
        var isFlooded = methodId != null && _floodedMethodIds.Contains(methodId);

        _scopeStack.Push(isFlooded ? methodId : null);
        var visited = base.VisitMethodDeclaration(node);
        _scopeStack.Pop();

        return visited;
    }

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var methodId = ResolveLocalFunctionId(node);
        var isFlooded = methodId != null && _floodedMethodIds.Contains(methodId);

        _scopeStack.Push(isFlooded ? methodId : null);
        var visited = base.VisitLocalFunctionStatement(node);
        _scopeStack.Pop();

        return visited;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Block / statement transformation
    // ──────────────────────────────────────────────────────────────────────────

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
        // Only transform when inside a flooded method scope
        var currentCallerId = CurrentTransformingMethodId;
        if (currentCallerId == null)
        {
            return null;
        }

        // Find invocations in this statement that call an out-parameter method
        foreach (var invocation in statement.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            // Resolve the callee symbol via the semantic model
            var calleeSymbol = _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            if (calleeSymbol == null)
            {
                continue;
            }

            var calleeId = MethodIdFactory.GetMethodId(calleeSymbol);
            if (!_outParamMethodsById.TryGetValue(calleeId, out var outParamMeta))
            {
                continue;
            }

            if (outParamMeta.TransformKind == OutParameterTransformKind.BoolTryPattern)
            {
                return TransformTryPatternCallSite(statement, invocation, calleeSymbol, outParamMeta);
            }
            else
            {
                return TransformTuplePatternCallSite(statement, invocation, calleeSymbol, outParamMeta);
            }
        }

        return null;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Pattern-specific transformations
    // ──────────────────────────────────────────────────────────────────────────

    private List<StatementSyntax>? TransformTryPatternCallSite(
        StatementSyntax statement,
        InvocationExpressionSyntax invocation,
        IMethodSymbol calleeSymbol,
        OutParameterMetadata meta)
    {
        var results = new List<StatementSyntax>();
        var resultVarName = $"__{ToCamelCase(calleeSymbol.Name)}Result";

        // Build new argument list without out params
        var newArgs = RemoveOutArguments(invocation.ArgumentList, meta.OutParameterIndices);
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
            .WithArgumentList(BuildOutArgumentList(meta.OutParameterNames));

        var newStatement = statement.ReplaceNode(invocation, tryGetCall);
        results.Add(newStatement);

        _replacements.Add((statement, results));
        _usedBoolTryPattern = true;
        return results;
    }

    private List<StatementSyntax>? TransformTuplePatternCallSite(
        StatementSyntax statement,
        InvocationExpressionSyntax invocation,
        IMethodSymbol calleeSymbol,
        OutParameterMetadata meta)
    {
        var results = new List<StatementSyntax>();
        var resultVarName = $"__{ToCamelCase(calleeSymbol.Name)}Result";

        // Build new argument list without out params
        var newArgs = RemoveOutArguments(invocation.ArgumentList, meta.OutParameterIndices);
        var newInvocation = invocation
            .WithArgumentList(ArgumentList(SeparatedList(newArgs)));

        // var (__result, outName1, outName2) = await method(args);
        var awaitExpr = AwaitExpression(
            Token(SyntaxKind.AwaitKeyword).WithTrailingTrivia(Space),
            newInvocation);

        // Use a deconstruction: var (__result, s) = await ...
        var deconstructDecl = LocalDeclarationStatement(
            VariableDeclaration(IdentifierName("var"))
                .WithVariables(SingletonSeparatedList(
                    VariableDeclarator(Identifier($"({resultVarName}, {string.Join(", ", meta.OutParameterNames)})").WithLeadingTrivia(Space))
                        .WithInitializer(EqualsValueClause(awaitExpr).WithLeadingTrivia(Space)))))
            .WithLeadingTrivia(statement.GetLeadingTrivia())
            .WithTrailingTrivia(statement.GetTrailingTrivia());
        results.Add(deconstructDecl);

        // Replace the invocation result usage if in an assignment/declaration
        // For simplicity, replace the original invocation with just the result var
        var newStatement = statement.ReplaceNode(invocation,
            IdentifierName(resultVarName).WithTriviaFrom(invocation));
        results.Add(newStatement);

        _replacements.Add((statement, results));
        return results;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────────────

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

    private static ArgumentListSyntax BuildOutArgumentList(List<string> outParameterNames)
    {
        if (outParameterNames.Count == 1)
        {
            return ArgumentList(SingletonSeparatedList(
                Argument(
                    null,
                    Token(SyntaxKind.OutKeyword).WithTrailingTrivia(Space),
                    DeclarationExpression(
                        IdentifierName("var").WithTrailingTrivia(Space),
                        SingleVariableDesignation(Identifier(outParameterNames[0]))))));
        }

        var args = outParameterNames.Select(name =>
            Argument(
                null,
                Token(SyntaxKind.OutKeyword).WithTrailingTrivia(Space),
                DeclarationExpression(
                    IdentifierName("var").WithTrailingTrivia(Space),
                    SingleVariableDesignation(Identifier(name))))).ToList();
        return ArgumentList(SeparatedList(args));
    }

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

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        return char.ToLowerInvariant(name[0]) + name.Substring(1);
    }
}
