using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Resolves method call relationships from a syntax tree
/// </summary>
public interface IMethodCallExtractor
{
    /// <summary>
    /// Extract all method calls in a syntax tree
    /// </summary>
    Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        ConcurrentBag<MethodCall> call,
        CancellationToken cancellationToken = default);
}
