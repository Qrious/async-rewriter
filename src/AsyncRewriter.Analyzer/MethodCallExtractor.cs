using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Resolves method call relationships from a syntax tree using an async visitor pattern
/// </summary>
public class MethodCallExtractor : AsyncCSharpSyntaxWalker, IMethodCallExtractor
{
    private ConcurrentBag<MethodCall> _calls = new();
    private SemanticModel _semanticModel = null!;
    private string _filePath = string.Empty;
    private IMethodSymbol? _currentMethodSymbol;
    private ConcurrentDictionary<string, MethodNode> _methods;
    private Guid _callGraphId;

    public async Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        ConcurrentBag<MethodCall> calls,
        CancellationToken cancellationToken = default)
    {
        _callGraphId = callGraphId;
        _calls = calls;
        _methods = methods;
        _semanticModel = semanticModel;
        _filePath = filePath;
        _currentMethodSymbol = null;

        await VisitAsync(root, cancellationToken);
    }
    
    public override async Task VisitMethodDeclarationAsync(MethodDeclarationSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol == null)
            return;

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        // Visit children to find invocations within this method
        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    public override async Task VisitLocalFunctionStatementAsync(LocalFunctionStatementSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetDeclaredSymbol(node) as IMethodSymbol;
        if (methodSymbol == null)
            return;

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        // Visit children to find invocations within this local function
        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    public override async Task VisitParenthesizedLambdaExpressionAsync(ParenthesizedLambdaExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (methodSymbol == null)
            return;

        RecordLambdaCall(node, methodSymbol);

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    public override async Task VisitSimpleLambdaExpressionAsync(SimpleLambdaExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        var methodSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
        if (methodSymbol == null)
            return;

        RecordLambdaCall(node, methodSymbol);

        var previousSymbol = _currentMethodSymbol;
        _currentMethodSymbol = methodSymbol;

        await DefaultVisitAsync(node, cancellationToken);

        _currentMethodSymbol = previousSymbol;
    }

    private void RecordLambdaCall(SyntaxNode node, IMethodSymbol lambdaSymbol)
    {
        if (_currentMethodSymbol == null)
            return;

        var callerId = MethodExtractor.GetMethodId(_currentMethodSymbol);
        var calleeId = MethodExtractor.GetMethodId(lambdaSymbol);

        if (!_methods.ContainsKey(calleeId))
        {
            _methods.TryAdd(calleeId, CreateMethodNodeFromSymbol(lambdaSymbol, _filePath));
        }

        _calls.Add(new MethodCall
        {
            CallGraphId = _callGraphId.ToString(),
            Id = Guid.NewGuid().ToString(),
            CallerId = callerId,
            CalleeId = calleeId,
            LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
            FilePath = _filePath
        });
    }

    public override async Task VisitObjectCreationExpressionAsync(ObjectCreationExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        if (_currentMethodSymbol != null)
        {
            var constructorSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (constructorSymbol != null)
            {
                ResolveLambdaArgsThroughConstructor(node.ArgumentList, constructorSymbol);
            }
        }

        await DefaultVisitAsync(node, cancellationToken);
    }

    /// <summary>
    /// When a lambda is passed as a constructor argument, traces it through
    /// parameter → field assignment → field invocation to create call edges
    /// from the invoking methods to the original lambda.
    /// </summary>
    private void ResolveLambdaArgsThroughConstructor(BaseArgumentListSyntax? argumentList, IMethodSymbol constructorSymbol)
    {
        var args = argumentList?.Arguments;
        if (args == null) return;

        for (int i = 0; i < args.Value.Count; i++)
        {
            var argExpr = args.Value[i].Expression;
            if (argExpr is not (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax))
                continue;

            var lambdaSymbol = _semanticModel.GetSymbolInfo(argExpr).Symbol as IMethodSymbol;
            if (lambdaSymbol == null || i >= constructorSymbol.Parameters.Length)
                continue;

            // Use OriginalDefinition so the parameter matches the unsubstituted constructor body
            var originalConstructor = constructorSymbol.OriginalDefinition;
            var param = originalConstructor.Parameters[i];

            // Find fields assigned from this parameter in the constructor body
            var fields = FindFieldsAssignedFromParameter(originalConstructor, param);

            // For each field, find methods in the type that invoke it as a delegate
            foreach (var field in fields)
            {
                LinkDelegateFieldInvocationsToLambda(field, lambdaSymbol);
            }
        }
    }

    private List<IFieldSymbol> FindFieldsAssignedFromParameter(IMethodSymbol constructorSymbol, IParameterSymbol param)
    {
        var fields = new List<IFieldSymbol>();

        var constructorSyntax = constructorSymbol.DeclaringSyntaxReferences
            .FirstOrDefault()?.GetSyntax();
        if (constructorSyntax == null) return fields;

        var constructorModel = _semanticModel.Compilation
            .GetSemanticModel(constructorSyntax.SyntaxTree);

        foreach (var assignment in constructorSyntax.DescendantNodes().OfType<AssignmentExpressionSyntax>())
        {
            var rightSymbol = constructorModel.GetSymbolInfo(assignment.Right).Symbol;
            if (!SymbolEqualityComparer.Default.Equals(rightSymbol, param))
                continue;

            var leftSymbol = constructorModel.GetSymbolInfo(assignment.Left).Symbol as IFieldSymbol;
            if (leftSymbol != null)
                fields.Add(leftSymbol);
        }

        return fields;
    }

    private void LinkDelegateFieldInvocationsToLambda(IFieldSymbol field, IMethodSymbol lambdaSymbol)
    {
        var containingType = field.ContainingType;
        if (containingType == null) return;

        var lambdaId = MethodExtractor.GetMethodId(lambdaSymbol);

        foreach (var member in containingType.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.MethodKind == MethodKind.Constructor)
                continue;

            var memberSyntax = member.DeclaringSyntaxReferences
                .FirstOrDefault()?.GetSyntax();
            if (memberSyntax == null) continue;

            var memberModel = _semanticModel.Compilation
                .GetSemanticModel(memberSyntax.SyntaxTree);

            foreach (var invocation in memberSyntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                // Check if the invocation target is the delegate field
                var exprSymbol = memberModel.GetSymbolInfo(invocation.Expression).Symbol;
                if (exprSymbol is not IFieldSymbol invokedField)
                    continue;
                if (!SymbolEqualityComparer.Default.Equals(invokedField, field))
                    continue;

                var callerId = MethodExtractor.GetMethodId(member);

                if (!_methods.ContainsKey(lambdaId))
                {
                    _methods.TryAdd(lambdaId, CreateMethodNodeFromSymbol(lambdaSymbol, _filePath));
                }

                _calls.Add(new MethodCall
                {
                    CallGraphId = _callGraphId.ToString(),
                    Id = Guid.NewGuid().ToString(),
                    CallerId = callerId,
                    CalleeId = lambdaId,
                    LineNumber = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    FilePath = memberSyntax.SyntaxTree.FilePath
                });
            }
        }
    }

    public override async Task VisitInvocationExpressionAsync(InvocationExpressionSyntax node, CancellationToken cancellationToken = default)
    {
        if (_currentMethodSymbol != null)
        {
            var invokedSymbol = _semanticModel.GetSymbolInfo(node).Symbol as IMethodSymbol;
            if (invokedSymbol != null)
            {
                var callerId = MethodExtractor.GetMethodId(_currentMethodSymbol);
                var calleeId = MethodExtractor.GetMethodId(invokedSymbol);

                // Create a method node for the callee if it doesn't exist in known methods
                if (!_methods.ContainsKey(calleeId))
                {
                    _methods.TryAdd(calleeId, CreateMethodNodeFromSymbol(invokedSymbol, "external"));
                }

                var methodCall = new MethodCall
                {
                    CallGraphId = _callGraphId.ToString(),
                    Id = Guid.NewGuid().ToString(),
                    CallerId = callerId,
                    CalleeId = calleeId,
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    FilePath = _filePath
                };

                _calls.Add(methodCall);
            }
        }

        // Continue walking into children to find nested invocations (e.g., inside lambdas)
        await DefaultVisitAsync(node, cancellationToken);
    }

    private MethodNode CreateMethodNodeFromSymbol(IMethodSymbol methodSymbol, string filePath)
    {
        return new MethodNode
        {
            CallGraphId = _callGraphId.ToString(),
            Id = MethodExtractor.GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = filePath,
            StartLine = methodSymbol.Locations.FirstOrDefault()?.GetLineSpan().StartLinePosition.Line + 1 ?? 0,
            EndLine = methodSymbol.Locations.FirstOrDefault()?.GetLineSpan().EndLinePosition.Line + 1 ?? 0,
        };
    }
}
