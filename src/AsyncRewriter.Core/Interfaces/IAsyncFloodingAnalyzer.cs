using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Analyzes which methods need to be converted to async based on call graph flooding
/// </summary>
public interface IAsyncFloodingAnalyzer
{
    /// <summary>
    /// Determines which methods need to be async based on the root methods
    /// and the call graph structure
    /// </summary>
    /// <param name="callGraph">The call graph to analyze</param>
    /// <param name="rootMethodIds">Methods that should be converted to async (starting points)</param>
    /// <returns>Updated call graph with flooding information</returns>
    Task<CallGraph> AnalyzeFloodingAsync(CallGraph callGraph, HashSet<string> rootMethodIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines which methods need to be async based on the root methods with progress reporting
    /// </summary>
    /// <param name="callGraph">The call graph to analyze</param>
    /// <param name="rootMethodIds">Methods that should be converted to async (starting points)</param>
    /// <param name="progressCallback">Callback for progress updates (currentMethod, methodsProcessed, totalMethods)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated call graph with flooding information</returns>
    Task<CallGraph> AnalyzeFloodingAsync(
        CallGraph callGraph,
        HashSet<string> rootMethodIds,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets detailed transformation information for each method that needs to be changed
    /// </summary>
    Task<List<AsyncTransformationInfo>> GetTransformationInfoAsync(CallGraph callGraph, CancellationToken cancellationToken = default);
}
