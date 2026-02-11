using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Resolves a <see cref="SemanticModel"/> for a given syntax tree,
/// enabling cross-project symbol resolution when the tree belongs
/// to a different compilation than the one currently being analyzed.
/// </summary>
public interface ISemanticModelResolver
{
    SemanticModel? Resolve(SyntaxTree syntaxTree);
}
