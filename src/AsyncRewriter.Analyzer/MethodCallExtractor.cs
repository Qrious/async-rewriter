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
