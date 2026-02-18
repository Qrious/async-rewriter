using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    public Task StoreCallGraphAsync(ICallGraph callGraph, CancellationToken cancellationToken = default);


    /// <summary>
    /// Deletes a call graph
    /// </summary>
    public Task DeleteCallGraphAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all call graphs (use with caution!)
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task DeleteAllCallGraphsAsync(CancellationToken cancellationToken = default);
}