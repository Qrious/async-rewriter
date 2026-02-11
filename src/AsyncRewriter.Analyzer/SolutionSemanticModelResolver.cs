using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using Microsoft.CodeAnalysis;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Resolves semantic models across all projects in a solution,
/// enabling cross-project symbol resolution during call graph analysis.
/// </summary>
public class SolutionSemanticModelResolver : ISemanticModelResolver
{
    private readonly List<Compilation> _compilations;

    private SolutionSemanticModelResolver(List<Compilation> compilations)
    {
        _compilations = compilations;
    }

    public static async Task<SolutionSemanticModelResolver> CreateAsync(
        Solution solution, CancellationToken cancellationToken = default)
    {
        var compilations = new List<Compilation>();
        foreach (var project in solution.Projects)
        {
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation != null)
                compilations.Add(compilation);
        }
        return new SolutionSemanticModelResolver(compilations);
    }

    public SemanticModel? Resolve(SyntaxTree syntaxTree)
    {
        foreach (var compilation in _compilations)
        {
            if (compilation.ContainsSyntaxTree(syntaxTree))
                return compilation.GetSemanticModel(syntaxTree);
        }
        return null;
    }
}
