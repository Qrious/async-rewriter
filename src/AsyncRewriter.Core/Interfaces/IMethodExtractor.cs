using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Resolves method declarations from a syntax tree
/// </summary>
public interface IMethodExtractor
{
    /// <summary>
    /// Resolves all method declarations in a syntax tree, including interface methods
    /// </summary>
    Task Extract(
        Guid callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, MethodNode> methods,
        CancellationToken cancellationToken = default);
}
