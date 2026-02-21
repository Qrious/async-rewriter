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
        string callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentDictionary<string, IMethodCall> calls,
        CancellationToken cancellationToken = default);

    Task Extract(
        string callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentDictionary<string, IMethodCall> calls,
        ISemanticModelResolver semanticModelResolver,
        CancellationToken cancellationToken = default);

}
