using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Provides the syntax root and semantic model for a source file by its file path.
/// Allows the transformation layer to perform symbol-based analysis without
/// depending on Roslyn workspace infrastructure directly.
/// </summary>
public interface IDocumentSemanticModelProvider
{
    /// <summary>
    /// Gets the syntax root and semantic model for the given source file path.
    /// Returns <c>null</c> when the file is not part of the solution.
    /// </summary>
    Task<(SyntaxNode Root, SemanticModel SemanticModel)?> GetForFileAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}
