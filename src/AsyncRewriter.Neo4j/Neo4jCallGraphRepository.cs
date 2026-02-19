using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace AsyncRewriter.Neo4j;

/// <summary>
/// Neo4j implementation of ICallGraphRepository.
/// Uses CREATE statements with indexes for fast insertion (no deduplication).
/// Call <see cref="EnsureIndexesAsync"/> once before first use.
/// </summary>
public class Neo4jCallGraphRepository : ICallGraphRepository, IAsyncDisposable, IDisposable
{
    private readonly ILogger _logger;
    private readonly IDriver _driver;
    private const int BatchSize = 500;

    public Neo4jCallGraphRepository(Neo4JCredentials credentials, ILogger logger)
    {
        _logger = logger;
        _driver = GraphDatabase.Driver(credentials.Url, AuthTokens.Basic(credentials.Username, credentials.Password));
    }

    /// <summary>
    /// Creates indexes for Method nodes.
    /// Should be called once at startup before storing data.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        // Node indexes
        await session.RunAsync("CREATE INDEX method_id IF NOT EXISTS FOR (m:Method) ON (m.CallGraphId, m.Id)");
        await session.RunAsync("CREATE INDEX method_name IF NOT EXISTS FOR (m:Method) ON (m.CallGraphId, m.name)");
        await session.RunAsync("CREATE INDEX method_type IF NOT EXISTS FOR (m:Method) ON (m.CallGraphId, m.ContainingType)");

        // Composite index for lookups by namespace + type
        await session.RunAsync("CREATE INDEX method_ns_type IF NOT EXISTS FOR (m:Method) ON (m.CallGraphId, m.ContainingNamespace, m.ContainingType)");
    }

    public Task Save(
        ICallGraph callGraph,
        CancellationToken cancellationToken = default) =>
        Save(new CallGraphWithMetadata<EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(callGraph.Id, callGraph, new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(), new Dictionary<string,EmptyGraphMetadata>(), new Dictionary<string, EmptyGraphMetadata>()), cancellationToken);

    public async Task<ICallGraph> Load(Guid id, CancellationToken cancellationToken = default)
    {
        return await Load<EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(id, cancellationToken);
    }

    public async Task DeleteCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("MATCH (m:Method {CallGraphId: $id}) DETACH DELETE m", new { id });
        });
    }

    public Task DeleteAllCallGraphsAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(async () =>
        {
            await using var session = _driver.AsyncSession();

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync("MATCH (n:Method) DETACH DELETE n");
            });
        }, cancellationToken);
    }

    public async Task Save<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata>(
        ICallGraphWithMetadata<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata> callGraphWithMetadata, CancellationToken cancellationToken)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
        where TCallMetadata : IGraphMetadata<TCallMetadata>
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
        where TOverridesMetadata : IGraphMetadata<TOverridesMetadata>
    {
        await using var session = _driver.AsyncSession();

        // 1. Clean any existing data for this call graph
        _logger.LogInformation("Clearing existing data");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync("MATCH (n:Method {CallGraphId: $callGraphId}) DETACH DELETE n", new
            {
                callGraphId = callGraphWithMetadata.Id
            });
        });

        // 2. Create Method nodes in batches
        var methods = callGraphWithMetadata.Methods.Values.ToList();
        var totalMethods = methods.Count;
        await Batch<IMethodNode>(totalMethods, BatchSize, async (batchStart, batchEnd) =>
        {
            var batch = methods.Skip(batchStart).Take(batchEnd - batchStart).ToList();
            await session.ExecuteWriteAsync(async tx => await WriteMethodBatch(tx, batch, new Dictionary<string, TMethodMetadata>()));
        }, cancellationToken);

        // 3. Create CALLS relationships in batches
        var calls = callGraphWithMetadata.Calls.ToList();
        await Batch<IMethodCall>(calls.Count, BatchSize, async (batchStart, batchEnd) =>
        {
            var batch = calls.Skip(batchStart).Take(batchEnd - batchStart).ToList();
            await session.ExecuteWriteAsync(async tx => await WriteCallBatch(tx, batch, new Dictionary<string, TCallMetadata>()));
        }, cancellationToken);

        // 4. Create IMPLEMENTS relationships in batches
        var implementations = callGraphWithMetadata.InterfaceImplementations.ToList();

        await Batch<IInterfaceImplementation>(implementations.Count, BatchSize, async (batchStart, batchEnd) =>
        {
            var batch = implementations.Skip(batchStart).Take(batchEnd - batchStart).ToList();
            await session.ExecuteWriteAsync(async tx => await WriteImplementsBatch(tx, batch, new Dictionary<string, EmptyGraphMetadata>()));
        }, cancellationToken);

        // 5. Create OVERRIDES relationships in batches
        var overrides = callGraphWithMetadata.MethodOverrides.ToList();
        await Batch<IInterfaceImplementation>(overrides.Count, BatchSize, async (batchStart, batchEnd) =>
        {
            var batch = overrides.Skip(batchStart).Take(batchEnd - batchStart).ToList();
            await session.ExecuteWriteAsync(async tx => await WriteOverridesBatch(tx, batch, new Dictionary<string, EmptyGraphMetadata>()));
        }, cancellationToken);

        _logger.LogInformation("Finished storing call graph");
    }

    private static async Task WriteMethodBatch<TMethodMetadata>(IAsyncQueryRunner tx, List<IMethodNode> batch, IReadOnlyDictionary<string, TMethodMetadata> metadata)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
    {
        await tx.RunAsync(
            @"UNWIND $methods AS method
                  CREATE (m:Method)
                  SET m = method",
            new
            {
                methods = batch.Select(m => AddMetadata(metadata, m, m.ToDictionary()))
            });
    }

    private static async Task WriteCallBatch<TCallMetadata>(IAsyncQueryRunner tx, List<IMethodCall> batch, IReadOnlyDictionary<string, TCallMetadata> metadata)
        where TCallMetadata : IGraphMetadata<TCallMetadata>
    {
        await tx.RunAsync(
            @"UNWIND $calls AS call
                      MATCH (caller:Method {Id: call.CallerId, CallGraphId: call.CallGraphId})
                      MATCH (callee:Method {Id: call.CalleeId, CallGraphId: call.CallGraphId})
                      CREATE (caller)-[r:CALLS]->(callee)
                      SET r = call",
            new
            {
                calls = batch.Select(m => AddMetadata(metadata, m, m.ToDictionary()))
            });
    }

    private static async Task WriteImplementsBatch<TImplementsMetadata>(IAsyncQueryRunner tx, List<IInterfaceImplementation> batch,
        IReadOnlyDictionary<string, TImplementsMetadata> metadata)
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
    {
        await tx.RunAsync(
            @"UNWIND $impls AS i
                    MATCH (impl:Method {Id: i.ImplementingMethodId, CallGraphId: i.CallGraphId})
                    MATCH (iface:Method {Id: i.InterfaceMethodId, CallGraphId: i.CallGraphId})
                    CREATE (impl)-[r:IMPLEMENTS]->(iface)
                    CREATE (iface)-[r2:IMPLEMENTED_BY]->(impl)
                    SET r = i
                    SET r2 = i",
            new
            {
                impls = batch.Select(m => AddMetadata(metadata, m, m.ToDictionary()))
            });
    }

    private static async Task WriteOverridesBatch<TImplementsMetadata>(IAsyncQueryRunner tx, List<IMethodOverride> batch, IReadOnlyDictionary<string, TImplementsMetadata> metadata)
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
    {
        await tx.RunAsync(
            @"UNWIND $overrides AS o
                    MATCH (overriding:Method {Id: o.OverridingMethodId, CallGraphId: o.CallGraphId})
                    MATCH (base:Method {Id: o.BaseMethodId, CallGraphId: o.CallGraphId})
                    CREATE (overriding)-[r:OVERRIDES]->(base)
                    CREATE (base)-[r2:OVERRIDDEN_BY]->(overriding)
                    SET r = o
                    SET r2 = o",
            new
            {
                overrides = batch.Select(m => AddMetadata(metadata, m, m.ToDictionary()))
            });
    }

    private static IDictionary<string, string> AddMetadata<TNode, TImplementsMetadata>(IReadOnlyDictionary<string, TImplementsMetadata> metadata, TNode m,
        IDictionary<string, string> data)
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
        where TNode : IIdentifiable
    {
        // Add metadata if available
        if (metadata.TryGetValue(m.Id, out var methodMetadata))
        {
            foreach (var kvp in methodMetadata.ToDictionary())
            {
                data[$"meta_{kvp.Key}"] = kvp.Value;
            }
        }

        return data;
    }

    public async Task<ICallGraphWithMetadata<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata>> Load<TMethodMetadata, TCallMetadata, TImplementsMetadata,
        TOverridesMetadata>(Guid id, CancellationToken cancellationToken)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
        where TCallMetadata : IGraphMetadata<TCallMetadata>
        where TOverridesMetadata : IGraphMetadata<TOverridesMetadata>
        where TImplementsMetadata : IGraphMetadata<TImplementsMetadata>
    {
        await using var session = _driver.AsyncSession();

        _logger.LogInformation("Loading call graph with metadata from Neo4j...");

        var (methods, methodsMetadata) = await LoadMethodsWithMetadata<TMethodMetadata>(id, session);
        _logger.LogInformation("Loaded {totalMethods} methods with metadata", methods.Count);

        var (calls, callMetadata) = await LoadCallsWithMetadata<TCallMetadata>(id, session);
        _logger.LogInformation("Loaded {TotalCalls} calls with metadata", calls.Count);

        // Implements
        var (interfaceImplementations, interfaceImplementationsMetadata) = await LoadImplementsWithMetadata<TOverridesMetadata>(id, session);
        _logger.LogInformation("Loaded {TotalInterfaceImplementations} interface implementations with metadata", interfaceImplementations.Count);

        // Overrides
        var (methodOverrides, methodOverridesMetadata) = await LoadOverridesWithMetadata<TOverridesMetadata>(id, session);

        _logger.LogInformation("Call graph with metadata loaded successfully");

        // Create the base call graph
        var callGraph = new CallGraph(id.ToString(), methods, calls, interfaceImplementations, methodOverrides);

        // Cast metadata dictionaries to the generic types
        var typedMethodMetadata = methodsMetadata.ToDictionary(
            kvp => kvp.Key,
            kvp => TMethodMetadata.FromDictionary(kvp.Value)
        );

        var typedCallMetadata = callMetadata.ToDictionary(
            kvp => kvp.Key,
            kvp => TCallMetadata.FromDictionary(kvp.Value)
        );

        var typedImplementsMetadata = interfaceImplementationsMetadata.ToDictionary(
            kvp => kvp.Key,
            kvp => TImplementsMetadata.FromDictionary(kvp.Value)
        );

        var typedOverridesMetadata = methodOverridesMetadata.ToDictionary(
            kvp => kvp.Key,
            kvp => TOverridesMetadata.FromDictionary(kvp.Value)
        );

        return new CallGraphWithMetadata<TMethodMetadata, TCallMetadata, TImplementsMetadata, TOverridesMetadata>(
            id.ToString(),
            callGraph,
            typedMethodMetadata,
            typedCallMetadata,
            typedImplementsMetadata,
            typedOverridesMetadata
        );
    }

    private static async Task<(ConcurrentBag<IMethodCall> Calls, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)> LoadCallsWithMetadata<TCallMetadata>(Guid id,
        IAsyncSession session)
        where TCallMetadata : IGraphMetadata<TCallMetadata>
    {
        var callsCursor = await session.RunAsync(
            @"MATCH (caller:Method)-[r:CALLS]->(callee:Method)
              WHERE r.CallGraphId = $callGraphId
              RETURN r",
            new
            {
                callGraphId = id.ToString()
            });

        return await callsCursor
            .Select(record => record["r"].As<IRelationship>())
            .Select(relationship => (
                    MethodCall: MethodCall.Create(relationship.Properties),
                    Metadata: relationship.Properties
                        .Where(kvp => kvp.Key.StartsWith("meta_"))
                        .ToDictionary(kvp => kvp.Key.Substring(5), kvp => kvp.Value?.ToString() ?? string.Empty)
                )
            ).AggregateAsync(
                (new ConcurrentBag<IMethodCall>(), new ConcurrentDictionary<string, Dictionary<string, string>>()),
                ((ConcurrentBag<IMethodCall> Calls, ConcurrentDictionary<string, Dictionary<string, string>> Metadata) result,
                    (IMethodCall Call, Dictionary<string, string> Metadata) tuple, CancellationToken cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Calls.Add(tuple.Call);
                    result.Metadata[tuple.Call.Id] = tuple.Metadata;

                    return new ValueTask<(ConcurrentBag<IMethodCall> Calls, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(result);
                },
                (accumulate, _) =>
                    new ValueTask<(ConcurrentBag<IMethodCall> Calls, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(accumulate));
    }

    private static async Task<(ConcurrentDictionary<string, IMethodNode> Methods, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>
        LoadMethodsWithMetadata<TMethodMetadata>(Guid id, IAsyncSession session)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
    {
        var methodsCursor = await session.RunAsync(
            @"MATCH (m:Method)
              WHERE m.CallGraphId = $callGraphId
              RETURN m",
            new
            {
                callGraphId = id.ToString()
            });

        return await methodsCursor
            .Select(record => record["m"].As<INode>())
            .Select(node => (
                    MethodNode: MethodNode.Create(node.Properties),
                    Metadata: node.Properties
                        .Where(kvp => kvp.Key.StartsWith("meta_"))
                        .ToDictionary(kvp => kvp.Key.Substring(5), kvp => kvp.Value?.ToString() ?? string.Empty)
                )
            ).AggregateAsync(
                (new ConcurrentDictionary<string, IMethodNode>(), new ConcurrentDictionary<string, Dictionary<string, string>>()),
                ((ConcurrentDictionary<string, IMethodNode> Methods, ConcurrentDictionary<string, Dictionary<string, string>> Metadata) result,
                    (IMethodNode Method, Dictionary<string, string> Metadata) tuple, CancellationToken cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Item1[tuple.Method.Id] = tuple.Method;
                    result.Metadata[tuple.Method.Id] = tuple.Metadata;

                    return new ValueTask<(ConcurrentDictionary<string, IMethodNode> Methods, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(result);
                },
                (accumulate, _) =>
                    new ValueTask<(ConcurrentDictionary<string, IMethodNode> Methods, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(accumulate));
    }

    private static async Task<(ConcurrentBag<IInterfaceImplementation> InterfaceImplementations, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>
        LoadImplementsWithMetadata<TImplementationMetadata>(Guid id, IAsyncSession session)
        where TImplementationMetadata : IGraphMetadata<TImplementationMetadata>
    {
        var implementscursor = await session.RunAsync(
            "MATCH (impl:Method {CallGraphId: $callGraphId})-[r:IMPLEMENTS]->(iface:Method) " +
            "RETURN r",
            new
            {
                callGraphId = id.ToString()
            });

        return await implementscursor
            .Select(record => record["r"].As<IRelationship>())
            .Select(relationship => (
                    Implementation: InterfaceImplementation.Create(relationship.Properties),
                    Metadata: relationship.Properties
                        .Where(kvp => kvp.Key.StartsWith("meta_"))
                        .ToDictionary(kvp => kvp.Key.Substring(5), kvp => kvp.Value?.ToString() ?? string.Empty)
                )
            ).AggregateAsync(
                (new ConcurrentBag<IInterfaceImplementation>(), new ConcurrentDictionary<string, Dictionary<string, string>>()),
                ((ConcurrentBag<IInterfaceImplementation> Implementations, ConcurrentDictionary<string, Dictionary<string, string>> Metadata) result,
                    (IInterfaceImplementation Implementation, Dictionary<string, string> Metadata) tuple, CancellationToken cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Implementations.Add(tuple.Implementation);
                    result.Metadata[tuple.Implementation.Id] = tuple.Metadata;

                    return new ValueTask<(ConcurrentBag<IInterfaceImplementation> Implementation, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(result);
                },
                (accumulate, _) =>
                    new ValueTask<(ConcurrentBag<IInterfaceImplementation> Implementation, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(accumulate));
    }

    private static async Task<(ConcurrentBag<IMethodOverride> MethodOverrides, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>
        LoadOverridesWithMetadata<TMethodOverrideMetadata>(Guid id, IAsyncSession session)
        where TMethodOverrideMetadata : IGraphMetadata<TMethodOverrideMetadata>
    {
        var overridesCursor = await session.RunAsync(
            "MATCH (overriding:Method {CallGraphId: $callGraphId})-[r:OVERRIDES]->(base:Method) " +
            "RETURN r",
            new
            {
                callGraphId = id.ToString()
            });

        return await overridesCursor
            .Select(record => record["r"].As<IRelationship>())
            .Select(relationship => (
                    MethodOverride: MethodOverride.Create(relationship.Properties),
                    Metadata: relationship.Properties
                        .Where(kvp => kvp.Key.StartsWith("meta_"))
                        .ToDictionary(kvp => kvp.Key.Substring(5), kvp => kvp.Value?.ToString() ?? string.Empty)
                )
            ).AggregateAsync(
                (new ConcurrentBag<IMethodOverride>(), new ConcurrentDictionary<string, Dictionary<string, string>>()),
                ((ConcurrentBag<IMethodOverride> Overrides, ConcurrentDictionary<string, Dictionary<string, string>> Metadata) result,
                    (IMethodOverride Override, Dictionary<string, string> Metadata) tuple, CancellationToken cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    result.Overrides.Add(tuple.Override);
                    result.Metadata[tuple.Override.Id] = tuple.Metadata;

                    return new ValueTask<(ConcurrentBag<IMethodOverride> Overrides, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(result);
                },
                (accumulate, _) =>
                    new ValueTask<(ConcurrentBag<IMethodOverride> Overrides, ConcurrentDictionary<string, Dictionary<string, string>> Metadata)>(accumulate));
    }

    private async Task Batch<T>(int total, int batchSize, Func<int, int, Task> processBatch, CancellationToken cancellationToken)
    {
        for (int i = 0; i < total; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Creating ({Processed}/{Total}) {Type}", i, total, typeof(T).Name);
            var batchStart = i;
            var batchEnd = Math.Min(i + batchSize, total);
            await processBatch(batchStart, batchEnd);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }

    public void Dispose()
    {
        _driver.Dispose();
    }
}