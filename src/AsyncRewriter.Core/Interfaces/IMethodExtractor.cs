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
        string callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentDictionary<string, IInterfaceImplementation> interfaceImplementations,
        ConcurrentDictionary<string, IMethodOverride> methodOverrides,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves all method declarations in a syntax tree, including interface methods and generic instantiations
    /// </summary>
    Task Extract(
        string callGraphId,
        SyntaxNode root,
        SemanticModel semanticModel,
        string filePath,
        ConcurrentDictionary<string, IMethodNode> methods,
        ConcurrentDictionary<string, IInterfaceImplementation> interfaceImplementations,
        ConcurrentDictionary<string, IMethodOverride> methodOverrides,
        ConcurrentDictionary<string, IGenericInstantiation> genericInstantiations,
        CancellationToken cancellationToken = default);
}
