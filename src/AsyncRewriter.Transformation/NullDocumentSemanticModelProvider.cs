using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Transformation;

/// <summary>
/// A no-op <see cref="IDocumentSemanticModelProvider"/> that always returns <c>null</c>,
/// signalling to callers that they should fall back to disk-based parsing.
/// Used when no solution is available.
/// </summary>
public sealed class NullDocumentSemanticModelProvider : IDocumentSemanticModelProvider
{
    public static readonly NullDocumentSemanticModelProvider Instance = new();

    private NullDocumentSemanticModelProvider() { }

    public Task<(SyntaxNode Root, SemanticModel SemanticModel)?> GetForFileAsync(
        string filePath,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<(SyntaxNode, SemanticModel)?>(null);
}
