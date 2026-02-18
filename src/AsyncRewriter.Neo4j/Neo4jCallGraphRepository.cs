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
        await session.RunAsync("CREATE INDEX method_id IF NOT EXISTS FOR (m:Method) ON (m.callGraphId, m.id)");
        await session.RunAsync("CREATE INDEX method_name IF NOT EXISTS FOR (m:Method) ON (m.callGraphId, m.name)");
        await session.RunAsync("CREATE INDEX method_type IF NOT EXISTS FOR (m:Method) ON (m.callGraphId, m.containingType)");

        // Composite index for lookups by namespace + type
        await session.RunAsync("CREATE INDEX method_ns_type IF NOT EXISTS FOR (m:Method) ON (m.callGraphId, m.containingNamespace, m.containingType)");
    }

    public async Task StoreCallGraphAsync(
        ICallGraph callGraph,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        // 1. Clean any existing data for this call graph
        _logger.LogInformation("Clearing existing data");
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (n:Method {callGraphId: $callGraphId}) DETACH DELETE n",
                new { callGraphId = callGraph.Id });
        });

        // 2. Create Method nodes in batches
        var methods = callGraph.Methods.Values.ToList();
        var totalMethods = methods.Count;
        var totalCalls = callGraph.Calls.Count;

        for (int i = 0; i < totalMethods; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = methods.Skip(i).Take(BatchSize).ToList();
            var processed = Math.Min(i + BatchSize, totalMethods);
            _logger.LogInformation("Creating Method nodes", processed, totalMethods);

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $methods AS m
                    CREATE (method:Method {
                        callGraphId: m.callGraphId,
                        id: m.id,
                        name: m.name,
                        containingType: m.containingType,
                        containingNamespace: m.containingNamespace,
                        returnType: m.returnType,
                        parameters: m.parameters,
                        filePath: m.filePath,
                        startLine: m.startLine,
                        endLine: m.endLine
                    })",
                    new
                    {
                        methods = batch.Select(m => new Dictionary<string, object?>
                        {
                            ["callGraphId"] = m.CallGraphId,
                            ["id"] = m.Id,
                            ["name"] = m.Name,
                            ["containingType"] = m.ContainingType,
                            ["containingNamespace"] = m.ContainingNamespace,
                            ["returnType"] = m.ReturnType,
                            ["parameters"] = m.Parameters,
                            ["filePath"] = m.FilePath,
                            ["startLine"] = m.StartLine,
                            ["endLine"] = m.EndLine
                        }).ToList()
                    });
            });
        }

        // 3. Create CALLS relationships in batches
        var calls = callGraph.Calls;
        for (int i = 0; i < totalCalls; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = calls.Skip(i).Take(BatchSize).ToList();
            var processed = Math.Min(i + BatchSize, totalCalls);
            _logger.LogInformation("Creating ({Processed}/{Total}) CALLS relationships", processed, totalCalls);

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $calls AS c
                    MATCH (caller:Method {id: c.callerId, callGraphId: c.callGraphId})
                    MATCH (callee:Method {id: c.calleeId, callGraphId: c.callGraphId})
                    CREATE (caller)-[:CALLS {
                        callGraphId: c.callGraphId,
                        id: c.id,
                        lineNumber: c.lineNumber,
                        filePath: c.filePath
                    }]->(callee)",
                    new
                    {
                        calls = batch.Select(c => new Dictionary<string, object>
                        {
                            ["callGraphId"] = c.CallGraphId,
                            ["id"] = c.Id,
                            ["callerId"] = c.CallerId,
                            ["calleeId"] = c.CalleeId,
                            ["lineNumber"] = c.LineNumber,
                            ["filePath"] = c.FilePath,
                        }).ToList()
                    });
            });
        }

        // 4. Create IMPLEMENTS relationships in batches
        var implementations = callGraph.InterfaceImplementations.ToList();
        var totalImpls = implementations.Count;

        for (int i = 0; i < totalImpls; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = implementations.Skip(i).Take(BatchSize).ToList();
            var processed = Math.Min(i + BatchSize, totalImpls);
            _logger.LogInformation("Creating ({Processed}/{Total}) IMPLEMENTS relationships", processed, totalImpls);

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $impls AS i
                    MATCH (impl:Method {id: i.implementingMethodId, callGraphId: i.callGraphId})
                    MATCH (iface:Method {id: i.interfaceMethodId, callGraphId: i.callGraphId})
                    CREATE (impl)-[:IMPLEMENTS {callGraphId: i.callGraphId}]->(iface)
                    CREATE (iface)-[:IMPLEMENTED_BY {callGraphId: i.callGraphId}]->(impl)",
                    new
                    {
                        impls = batch.Select(impl => new Dictionary<string, object>
                        {
                            ["callGraphId"] = impl.CallGraphId,
                            ["implementingMethodId"] = impl.ImplementingMethodId,
                            ["interfaceMethodId"] = impl.InterfaceMethodId,
                        }).ToList()
                    });
            });
        }

        // 5. Create OVERRIDES relationships in batches
        var overrides = callGraph.MethodOverrides.ToList();
        var totalOverrides = overrides.Count;

        for (int i = 0; i < totalOverrides; i += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = overrides.Skip(i).Take(BatchSize).ToList();
            var processed = Math.Min(i + BatchSize, totalOverrides);
            _logger.LogInformation("Creating ({Processed}/{Total}) OVERRIDES relationships", processed, totalOverrides);

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $overrides AS o
                    MATCH (overriding:Method {id: o.overridingMethodId, callGraphId: o.callGraphId})
                    MATCH (base:Method {id: o.baseMethodId, callGraphId: o.callGraphId})
                    CREATE (overriding)-[:OVERRIDES {callGraphId: o.callGraphId}]->(base)
                    CREATE (base)-[:OVERRIDDEN_BY {callGraphId: o.callGraphId}]->(overriding)",
                    new
                    {
                        overrides = batch.Select(o => new Dictionary<string, object>
                        {
                            ["callGraphId"] = o.CallGraphId,
                            ["overridingMethodId"] = o.OverridingMethodId,
                            ["baseMethodId"] = o.BaseMethodId,
                        }).ToList()
                    });
            });
        }
        _logger.LogInformation("Finished storing call graph");
    }

    public async Task DeleteCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (m:Method {callGraphId: $id}) DETACH DELETE m",
                new { id });
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

    private async Task<CallGraph> ReadCallGraphFromTransaction(IAsyncQueryRunner tx, string callGraphId)
    {
        // Fetch all methods
        var methodsResult = await tx.RunAsync(
            "MATCH (m:Method {callGraphId: $callGraphId}) RETURN m",
            new { callGraphId });
        var methodRecords = await methodsResult.ToListAsync();

        var methods = new ConcurrentDictionary<string, IMethodNode>();
        foreach (var record in methodRecords)
        {
            var method = MapToMethodNode(record["m"].As<INode>());
            methods[method.Id] = method;
        }

        // Fetch all call relationships
        var callsResult = await tx.RunAsync(
            "MATCH (caller:Method {callGraphId: $callGraphId})-[r:CALLS]->(callee:Method) " +
            "RETURN r, caller.id AS callerId, callee.id AS calleeId",
            new { callGraphId });
        var callRecords = await callsResult.ToListAsync();

        var callsBag = new ConcurrentBag<IMethodCall>();
        foreach (var record in callRecords)
        {
            var rel = record["r"].As<IRelationship>();
            var call = new MethodCall
            {
                CallGraphId = callGraphId,
                Id = rel["id"].As<string>(),
                CallerId = record["callerId"].As<string>(),
                CalleeId = record["calleeId"].As<string>(),
                LineNumber = rel["lineNumber"].As<int>(),
                FilePath = rel["filePath"].As<string>(),
            };
            callsBag.Add(call);
        }

        // Fetch all IMPLEMENTS relationships
        var implsResult = await tx.RunAsync(
            "MATCH (impl:Method {callGraphId: $callGraphId})-[r:IMPLEMENTS]->(iface:Method) " +
            "RETURN impl.id AS implementingMethodId, iface.id AS interfaceMethodId",
            new { callGraphId });
        var implRecords = await implsResult.ToListAsync();

        var implsBag = new ConcurrentBag<IInterfaceImplementation>();
        foreach (var record in implRecords)
        {
            implsBag.Add(new InterfaceImplementation
            {
                CallGraphId = callGraphId,
                ImplementingMethodId = record["implementingMethodId"].As<string>(),
                InterfaceMethodId = record["interfaceMethodId"].As<string>(),
            });
        }

        // Fetch all OVERRIDES relationships
        var overridesResult = await tx.RunAsync(
            "MATCH (overriding:Method {callGraphId: $callGraphId})-[r:OVERRIDES]->(base:Method) " +
            "RETURN overriding.id AS overridingMethodId, base.id AS baseMethodId",
            new { callGraphId });
        var overrideRecords = await overridesResult.ToListAsync();

        var overridesBag = new ConcurrentBag<IMethodOverride>();
        foreach (var record in overrideRecords)
        {
            overridesBag.Add(new MethodOverride
            {
                CallGraphId = callGraphId,
                OverridingMethodId = record["overridingMethodId"].As<string>(),
                BaseMethodId = record["baseMethodId"].As<string>(),
            });
        }

        return new CallGraph(callGraphId, methods, callsBag, implsBag, overridesBag);
    }

    public async Task Save<TMethodMetadata, TCallMetadata>(ICallGraphWithMetadata<TMethodMetadata, TCallMetadata> callGraphWithMetadata, CancellationToken cancellationToken)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
        where TCallMetadata : IGraphMetadata<TCallMetadata>
    {
        await using var session = _driver.AsyncSession();

        var methodsList = callGraphWithMetadata.Methods.ToArray();
        var callsList = callGraphWithMetadata.Calls.ToArray();
        var totalMethods = methodsList.Length;
        var totalCalls = callsList.Length;

        // Process methods in batches of 1000
        const int batchSize = 1000;

        for (int i = 0; i < totalMethods; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(i + batchSize, totalMethods);
            var batch = new object[batchEnd - i];

            for (int j = 0; j < batch.Length; j++)
            {
                var (key, method) = methodsList[i + j];

                var methodDict = new Dictionary<string, object>
                {
                    ["id"] = method.Id,
                    ["name"] = method.Name,
                    ["containingType"] = method.ContainingType,
                    ["containingNamespace"] = method.ContainingNamespace,
                    ["returnType"] = method.ReturnType,
                    ["parameters"] = method.Parameters.Select(p => p.ToString()).ToList(),
                    ["filePath"] = method.FilePath,
                    ["startLine"] = method.StartLine,
                    ["endLine"] = method.EndLine,
                    ["callGraphId"] = callGraphWithMetadata.Id
                };

                // Add metadata if available
                if (callGraphWithMetadata.TryGetMethodMetadata(method.Id, out var metadata))
                {
                    foreach (var kvp in metadata.ToDictionary())
                    {
                        methodDict[kvp.Key] = kvp.Value;
                    }
                }

                batch[j] = methodDict;
            }

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $methods AS method
                      CREATE (m:Method)
                      SET m = method",
                    new { methods = batch });
            });

            _logger.LogInformation("Writing methods ({methodsProcessed}/{totalMethods})...", batchEnd, totalMethods);
        }

        for (int i = 0; i < totalCalls; i += batchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(i + batchSize, totalCalls);
            var batch = new object[batchEnd - i];

            for (int j = 0; j < batch.Length; j++)
            {
                var call = callsList[i + j];

                var callDict = new Dictionary<string, object>
                {
                    ["Caller"] = call.CallerId,
                    ["Callee"] = call.CalleeId,
                    ["LineNumber"] = call.LineNumber,
                    ["FilePath"] = call.FilePath,
                    ["CallGraphId"] = callGraphWithMetadata.Id.ToString()
                };

                // Add metadata if available
                var callKey = $"{call.CallerId}|{call.CalleeId}|{call.LineNumber}";
                if (callGraphWithMetadata.TryGetCallMetadata(callKey, out var metadata))
                {
                    foreach (var kvp in metadata.ToDictionary())
                    {
                        callDict[kvp.Key] = kvp.Value;
                    }
                }

                batch[j] = callDict;
            }

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $calls AS call
                      MATCH (caller:Method {id: call.Caller, callGraphId: call.CallGraphId})
                      MATCH (callee:Method {id: call.Callee, callGraphId: call.CallGraphId})
                      CREATE (caller)-[r:CALLS]->(callee)
                      SET r = call",
                    new { calls = batch });
            });

            _logger.LogInformation("Writing calls ({callsProcessed}/{totalCalls})...", batchEnd, totalCalls);
        }
    }

    public async Task<ICallGraphWithMetadata<TMethodMetadata, TCallMetadata>> Load<TMethodMetadata, TCallMetadata>(Guid id, CancellationToken cancellationToken)
        where TMethodMetadata : IGraphMetadata<TMethodMetadata>
        where TCallMetadata : IGraphMetadata<TCallMetadata>
    {
        await using var session = _driver.AsyncSession();

        _logger.LogInformation("Loading call graph with metadata from Neo4j...");

        // Load all methods with all their properties
        var methodsDict = new ConcurrentDictionary<string, IMethodNode>();
        var methodMetadataDict = new Dictionary<string, Dictionary<string, string>>();

        var methodsCursor = await session.RunAsync(
            @"MATCH (m:Method)
              WHERE m.callGraphId = $callGraphId
              RETURN m",
            new { callGraphId = id.ToString() });

        var methodCount = 0;
        await foreach (var record in methodsCursor)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var node = record["m"].As<INode>();
            var properties = node.Properties;

            var methodId = properties["id"].As<string>();
            var parameters = properties["parameters"].As<List<string>>()
                .ToList();

            var method = new MethodNode
            {
                CallGraphId = id.ToString(),
                Id = methodId,
                Name = properties["name"].As<string>(),
                ContainingType = properties["containingType"].As<string>(),
                ContainingNamespace = properties["containingNamespace"].As<string>(),
                ReturnType = properties["returnType"].As<string>(),
                Parameters = parameters,
                FilePath = properties["filePath"].As<string>(),
                StartLine = properties.ContainsKey("startLine") ? properties["startLine"].As<int>() : 0,
                EndLine = properties.ContainsKey("endLine") ? properties["endLine"].As<int>() : 0
            };

            methodsDict[methodId] = method;

            // Extract metadata (properties starting with "meta_")
            var metadata = new Dictionary<string, string>();
            foreach (var kvp in properties)
            {
                if (kvp.Key.StartsWith("meta_"))
                {
                    // Remove the "meta_" prefix when storing
                    var metadataKey = kvp.Key.Substring(5);
                    metadata[metadataKey] = kvp.Value?.ToString() ?? string.Empty;
                }
            }

            if (metadata.Count > 0)
            {
                methodMetadataDict[methodId] = metadata;
            }

            methodCount++;
            if (methodCount % 1000 == 0)
            {
                _logger.LogInformation("Loaded {methodCount} methods with metadata...", methodCount);
            }
        }

        _logger.LogInformation("Loaded {totalMethods} methods with metadata", methodCount);

        // Load all calls with all their properties
        var calls = new ConcurrentBag<IMethodCall>();
        var callMetadataDict = new Dictionary<string, Dictionary<string, string>>();

        var callsCursor = await session.RunAsync(
            @"MATCH (caller:Method)-[r:CALLS]->(callee:Method)
              WHERE r.CallGraphId = $callGraphId
              RETURN caller.id AS callerId, callee.id AS calleeId, r",
            new { callGraphId = id.ToString() });

        var callCount = 0;
        await foreach (var record in callsCursor)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relationship = record["r"].As<IRelationship>();
            var properties = relationship.Properties;
            var callerId = record["callerId"].As<string>();
            var calleeId = record["calleeId"].As<string>();

            var call = new MethodCall
            {
                Id = properties["id"].As<string>(),
                CallGraphId = id.ToString(),
                CallerId = callerId,
                CalleeId = calleeId,
                LineNumber = properties.ContainsKey("LineNumber") ? properties["LineNumber"].As<int>() : 0,
                FilePath = properties.ContainsKey("FilePath") ? properties["FilePath"].As<string?>() : null
            };

            calls.Add(call);

            // Extract metadata (properties starting with "meta_")
            var metadata = new Dictionary<string, string>();
            foreach (var kvp in properties)
            {
                if (kvp.Key.StartsWith("meta_"))
                {
                    // Remove the "meta_" prefix when storing
                    var metadataKey = kvp.Key.Substring(5);
                    metadata[metadataKey] = kvp.Value?.ToString() ?? string.Empty;
                }
            }

            if (metadata.Count > 0)
            {
                var callKey = $"{callerId}|{calleeId}|{call.LineNumber}";
                callMetadataDict[callKey] = metadata;
            }

            callCount++;
            if (callCount % 5000 == 0)
            {
                _logger.LogInformation("Loaded {callCount} calls with metadata...", callCount);
            }
        }

        _logger.LogInformation("Loaded {totalCalls} calls with metadata", callCount);
        _logger.LogInformation("Call graph with metadata loaded successfully");

        // Create the base call graph
        var callGraph = new CallGraph(id.ToString(), methodsDict, calls);

        // Cast metadata dictionaries to the generic types
        var typedMethodMetadata = methodMetadataDict.ToDictionary(
            kvp => kvp.Key,
            kvp => TMethodMetadata.FromDictionary(kvp.Value)
        );

        var typedCallMetadata = callMetadataDict.ToDictionary(
            kvp => kvp.Key,
            kvp => TCallMetadata.FromDictionary(kvp.Value)
        );

        return new CallGraphWithMetadata<TMethodMetadata, TCallMetadata>(
            id,
            callGraph,
            typedMethodMetadata,
            typedCallMetadata
        );
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


    private static MethodNode MapToMethodNode(INode node)
    {
        return new MethodNode
        {
            CallGraphId = node["callGraphId"].As<string>(),
            Id = node["id"].As<string>(),
            Name = node["name"].As<string>(),
            ContainingType = node["containingType"].As<string>(),
            ContainingNamespace = node["containingNamespace"].As<string>(),
            ReturnType = node["returnType"].As<string>(),
            Parameters = node["parameters"].As<List<string>>(),
            FilePath = node["filePath"].As<string>(),
            StartLine = node["startLine"].As<int>(),
            EndLine = node["endLine"].As<int>(),
        };
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
