using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// Analyzes an entire project and builds a call graph.
    /// If a solution file (.sln) is provided, analyzes all projects in the solution.
    /// </summary>
    Task<CallGraph> AnalyzeProjectAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes an entire project and builds a call graph with external sync wrapper methods.
    /// If a solution file (.sln) is provided, analyzes all projects in the solution.
    /// </summary>
    Task<CallGraph> AnalyzeProjectAsync(
        string projectPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes all projects in a solution and builds a combined call graph
    /// </summary>
    Task<CallGraph> AnalyzeSolutionAsync(string solutionPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes all projects in a solution and builds a combined call graph with external sync wrapper methods
    /// </summary>
    Task<CallGraph> AnalyzeSolutionAsync(
        string solutionPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes source code and builds a call graph
    /// </summary>
    Task<CallGraph> AnalyzeSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default);

    /// <summary>
    /// Analyzes source code and builds a call graph with external sync wrapper methods
    /// </summary>
    Task<CallGraph> AnalyzeSourceAsync(
        string sourceCode,
        IEnumerable<string>? externalSyncWrapperMethods,
        string fileName = "source.cs",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds methods that are sync wrappers around async operations.
    /// These are methods with Func&lt;Task&gt; or Func&lt;Task&lt;TResult&gt;&gt; parameters
    /// that return void or TResult respectively.
    /// </summary>
    Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsAsync(string projectPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds methods that are sync wrappers around async operations with progress reporting.
    /// </summary>
    /// <param name="projectPath">Path to the project or solution</param>
    /// <param name="progressCallback">Callback for progress updates (currentFile, filesProcessed, totalFiles)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsAsync(
        string projectPath,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds sync wrapper methods in source code
    /// </summary>
    Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsInSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default);
}
