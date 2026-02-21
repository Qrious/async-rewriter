using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Builds a method call graph from C# code
/// </summary>
public interface ICallGraphBuilder
{
    /// <summary>
    /// Analyzes all projects in a solution and builds a combined call graph
    /// </summary>
    Task<CallGraph> Build(string solutionPath, string callGraphId, CancellationToken cancellationToken = default);
}
