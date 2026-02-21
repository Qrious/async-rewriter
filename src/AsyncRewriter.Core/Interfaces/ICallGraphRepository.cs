using System;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncRewriter.Core.Interfaces;

/// <summary>
/// Repository for storing and retrieving call graphs from Neo4j
/// </summary>
public interface ICallGraphRepository
{
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

    /// <summary>
    /// Stores a call graph in Neo4j
    /// </summary>
    public Task Save(ICallGraph callGraph, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads a call graph from Neo4j by its id
    /// </summary>
    /// <param name="id">The id of the callgraph</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<ICallGraph> Load(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a call graph with metadata in Neo4j.
    /// </summary>
    /// <param name="callGraphWithMetadata">The call graph</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <typeparam name="TMethodMetadata">Type of metdata</typeparam>
    /// <typeparam name="TCallMetadata">Type of metadata</typeparam>
    /// <returns></returns>
    public Task Save<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata>(
        ICallGraphWithMetadata<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata> callGraphWithMetadata, CancellationToken cancellationToken)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
        where TCallMetadata : IGraphMetadata<TCallMetadata>
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
        where TOverridesMetadata : IGraphMetadata<TOverridesMetadata>;

    /// <summary>
    /// Loads a call graph with metadata from Neo4j by its id.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="TMethodMetadata"></typeparam>
    /// <typeparam name="TCallMetadata"></typeparam>
    /// <typeparam name="TImplementsMetadata"></typeparam>
    /// <typeparam name="TOverridesMetadata"></typeparam>
    /// <returns></returns>
    Task<ICallGraphWithMetadata<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata>> Load<TMethodMetadata, TCallMetadata, TImplementsMetadata,
        TOverridesMetadata>(string id, CancellationToken cancellationToken)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
        where TCallMetadata : IGraphMetadata<TCallMetadata>
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
        where TOverridesMetadata : IGraphMetadata<TOverridesMetadata>;
}