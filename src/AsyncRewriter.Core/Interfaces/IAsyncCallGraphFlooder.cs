using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Analyzes which methods need to be converted to async based on call graph flooding
/// </summary>
public interface IAsyncCallGraphFlooder
{
    /// <summary>
    /// Determines which methods need to be async, with optional blocking of generic method propagation
    /// </summary>
    /// <param name="callGraph">The call graph to analyze</param>
    /// <param name="rootMethodIds">Methods that should be converted to async (starting points)</param>
    /// <param name="newCallGraphId"></param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated call graph with flooding information</returns>
    Task<CallGraph> Flood(
        ICallGraph callGraph,
        HashSet<string> rootMethodIds,
        string? newCallGraphId = null,
        CancellationToken cancellationToken = default);

}
