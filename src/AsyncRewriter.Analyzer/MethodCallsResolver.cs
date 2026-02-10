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
public class MethodCallsResolver : AsyncCSharpSyntaxWalker, IMethodCallsResolver
{
    private readonly List<MethodCall> _calls = new();
    private readonly Dictionary<string, MethodNode> _discoveredExternalMethods = new();
    private SemanticModel _semanticModel = null!;
    private string _filePath = string.Empty;
    private IReadOnlyDictionary<string, MethodNode> _knownMethods = null!;
    private IMethodSymbol? _currentMethodSymbol;

    public async Task<IReadOnlyList<MethodCall>> ResolveCallsAsync(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        IReadOnlyDictionary<string, MethodNode> knownMethods,
        CancellationToken cancellationToken = default)
    {
        _calls.Clear();
        _discoveredExternalMethods.Clear();
        _semanticModel = semanticModel;
        _filePath = filePath;
        _knownMethods = knownMethods;
        _currentMethodSymbol = null;

        await VisitAsync(root, cancellationToken);

        return _calls;
    }

    /// <summary>
    /// External methods discovered during call resolution that weren't in the known methods set.
    /// These should be added to the call graph.
    /// </summary>
    public IReadOnlyDictionary<string, MethodNode> DiscoveredExternalMethods => _discoveredExternalMethods;

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
                var callerId = MethodsResolver.GetMethodId(_currentMethodSymbol);
                var calleeId = MethodsResolver.GetMethodId(invokedSymbol);

                // Create a method node for the callee if it doesn't exist in known methods
                if (!_knownMethods.ContainsKey(calleeId) && !_discoveredExternalMethods.ContainsKey(calleeId))
                {
                    _discoveredExternalMethods[calleeId] = CreateMethodNodeFromSymbol(invokedSymbol, "external");
                }

                var methodCall = new MethodCall
                {
                    CallerId = callerId,
                    CalleeId = calleeId,
                    CallerSignature = MethodsResolver.GetMethodSignature(_currentMethodSymbol),
                    CalleeSignature = MethodsResolver.GetMethodSignature(invokedSymbol),
                    LineNumber = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    FilePath = _filePath
                };

                _calls.Add(methodCall);
            }
        }

        // Continue walking into children to find nested invocations (e.g., inside lambdas)
        await DefaultVisitAsync(node, cancellationToken);
    }

    private static MethodNode CreateMethodNodeFromSymbol(IMethodSymbol methodSymbol, string filePath)
    {
        return new MethodNode
        {
            Id = MethodsResolver.GetMethodId(methodSymbol),
            Name = methodSymbol.Name,
            ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
            ContainingNamespace = methodSymbol.ContainingNamespace?.ToDisplayString() ?? "",
            ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            Parameters = methodSymbol.Parameters.Select(p => $"{p.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} {p.Name}").ToList(),
            FilePath = filePath,
            IsAsync = methodSymbol.IsAsync,
            Signature = MethodsResolver.GetMethodSignature(methodSymbol)
        };
    }
}
