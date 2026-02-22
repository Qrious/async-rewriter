using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Implements <see cref="IDocumentSemanticModelProvider"/> by looking up Roslyn
/// <see cref="Document"/> objects in a pre-loaded <see cref="Solution"/>.
/// Use <see cref="Create"/> to construct an instance from an open solution.
/// </summary>
public sealed class SolutionDocumentSemanticModelProvider : IDocumentSemanticModelProvider
{
    private readonly IReadOnlyDictionary<string, Document> _documentsByPath;

    private SolutionDocumentSemanticModelProvider(IReadOnlyDictionary<string, Document> documentsByPath)
    {
        _documentsByPath = documentsByPath;
    }

    /// <summary>
    /// Creates a provider from the given solution, indexing all documents by their file path.
    /// When multiple documents share the same path (e.g. linked files) the first one wins.
    /// </summary>
    public static SolutionDocumentSemanticModelProvider Create(Solution solution)
    {
        var documentsByPath = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath != null)
            .GroupBy(d => d.FilePath!, System.StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), System.StringComparer.OrdinalIgnoreCase);

        return new SolutionDocumentSemanticModelProvider(documentsByPath);
    }

    /// <inheritdoc />
    public async Task<(SyntaxNode Root, SemanticModel SemanticModel)?> GetForFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (!_documentsByPath.TryGetValue(filePath, out var document))
        {
            return null;
        }

        var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
        if (semanticModel == null)
        {
            return null;
        }

        var root = await document.GetSyntaxRootAsync(cancellationToken);
        if (root == null)
        {
            return null;
        }

        return (root, semanticModel);
    }
}
