using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Analyzes C# code to build a method call graph
/// </summary>
public interface ICallGraphAnalyzer
{
    /// <summary>
    /// Analyzes a single C# file and builds a call graph
    /// </summary>
    Task<CallGraph> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes an entire project and builds a call graph
    /// </summary>
    Task<CallGraph> AnalyzeProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes source code and builds a call graph
    /// </summary>
    Task<CallGraph> AnalyzeSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default);
}
