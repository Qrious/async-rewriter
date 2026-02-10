using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Resolves method declarations from a syntax tree
/// </summary>
public interface IMethodsResolver
{
    /// <summary>
    /// Resolves all method declarations in a syntax tree, including interface methods
    /// </summary>
    Task<IReadOnlyDictionary<string, MethodNode>> ResolveMethodsAsync(
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        CancellationToken cancellationToken = default);
}
