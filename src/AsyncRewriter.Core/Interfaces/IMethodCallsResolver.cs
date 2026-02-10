using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Resolves method call relationships from a syntax tree
/// </summary>
public interface IMethodCallsResolver
{
    /// <summary>
    /// Resolves all method calls in a syntax tree
    /// </summary>
    Task<IReadOnlyList<MethodCall>> ResolveCallsAsync(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        IReadOnlyDictionary<string, MethodNode> knownMethods,
        CancellationToken cancellationToken = default);
}
