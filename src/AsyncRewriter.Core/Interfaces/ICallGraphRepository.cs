using AsyncRewriter.Core.Models;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Repository for storing and retrieving call graphs from Neo4j
/// </summary>
public interface ICallGraphRepository
{
    /// <summary>
    /// Stores a call graph in Neo4j
    /// </summary>
    Task StoreCallGraphAsync(CallGraph callGraph, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a call graph by ID
    /// </summary>
    Task<CallGraph?> GetCallGraphAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a call graph by project name
    /// </summary>
    Task<CallGraph?> GetCallGraphByProjectAsync(string projectName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds all callers of a specific method (upstream dependencies)
    /// </summary>
    Task<List<MethodNode>> FindCallersAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds all methods called by a specific method (downstream dependencies)
    /// </summary>
    Task<List<MethodNode>> FindCalleesAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a call graph
    /// </summary>
    Task DeleteCallGraphAsync(string id, CancellationToken cancellationToken = default);
}
