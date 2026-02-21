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
    /// Determines which methods need to be async, with optional blocking of generic method propagation.
    /// Returns a call graph with metadata containing the flooding information for each flooded method.
    /// </summary>
    /// <param name="callGraph">The call graph to analyze</param>
    /// <param name="rootMethodIds">Methods that should be converted to async (starting points)</param>
    /// <param name="blockedGenericMethodIds">Generic method IDs that should not propagate flooding to/from their instantiations</param>
    /// <param name="newCallGraphId">Optional ID for the resulting call graph</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated call graph with flooding metadata per method</returns>
    Task<ICallGraphWithMetadata<FloodingMethodMetadata, FloodingCallMetadata, EmptyGraphMetadata, EmptyGraphMetadata>> Flood(
        ICallGraph callGraph,
        HashSet<string> rootMethodIds,
        HashSet<string>? blockedGenericMethodIds = null,
        string? newCallGraphId = null,
        CancellationToken cancellationToken = default);
}
