using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Neo4j.Driver;

namespace AsyncRewriter.Neo4j;

/// <summary>
/// Neo4j implementation of ICallGraphRepository.
/// Uses CREATE statements with indexes for fast insertion (no deduplication).
/// Call <see cref="EnsureIndexesAsync"/> once before first use.
/// </summary>
public class Neo4jCallGraphRepository : ICallGraphRepository, IAsyncDisposable, IDisposable
{
    private readonly IDriver _driver;
    private const int BatchSize = 500;

    public Neo4jCallGraphRepository(string uri, string user, string password)
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    public Neo4jCallGraphRepository(IDriver driver)
    {
        _driver = driver;
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

    public Task StoreCallGraphAsync(CallGraph callGraph, CancellationToken cancellationToken = default)
    {
        return StoreCallGraphAsync(callGraph, null, cancellationToken);
    }

    public async Task StoreCallGraphAsync(
        CallGraph callGraph,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        // 1. Clean any existing data for this call graph
        progressCallback?.Invoke("Clearing existing data", 0, 3);
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
            progressCallback?.Invoke("Creating Method nodes", processed, totalMethods);

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
            progressCallback?.Invoke("Creating CALLS relationships", processed, totalCalls);

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

        progressCallback?.Invoke("Done", totalMethods + totalCalls, totalMethods + totalCalls);
    }

    public async Task<CallGraph?> GetCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            // Check if any methods exist for this call graph
            var checkResult = await tx.RunAsync(
                "MATCH (m:Method {callGraphId: $id}) RETURN m LIMIT 1",
                new { id });
            var checkRecord = await checkResult.SingleAsync();
            if (checkRecord == null) return null;

            return await ReadCallGraphFromTransaction(tx, id);
        });
    }

    public async Task<CallGraph?> GetCallGraphByProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            // Find a callGraphId for methods that belong to this project's namespace
            // Since we no longer have a CallGraph node, we look for methods whose namespace matches the project name
            var result = await tx.RunAsync(
                "MATCH (m:Method) WHERE m.containingNamespace STARTS WITH $projectName RETURN m.callGraphId AS callGraphId LIMIT 1",
                new { projectName });
            var record = await result.SingleAsync();
            if (record == null) return null;

            var callGraphId = record["callGraphId"].As<string>();
            return await ReadCallGraphFromTransaction(tx, callGraphId);
        });
    }

    public async Task<List<MethodNode>> FindCallersAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        var depthClause = depth < 0 ? "*" : $"*1..{depth}";

        return await session.ExecuteReadAsync(async tx =>
        {
            var result = await tx.RunAsync(
                $"MATCH (caller:Method)-[:CALLS{depthClause}]->(target:Method {{id: $methodId}}) " +
                "WHERE caller.id <> $methodId " +
                "RETURN DISTINCT caller",
                new { methodId });

            var records = await result.ToListAsync();
            return records.Select(r => MapToMethodNode(r["caller"].As<INode>())).ToList();
        });
    }

    public async Task<List<MethodNode>> FindCalleesAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        var depthClause = depth < 0 ? "*" : $"*1..{depth}";

        return await session.ExecuteReadAsync(async tx =>
        {
            var result = await tx.RunAsync(
                $"MATCH (source:Method {{id: $methodId}})-[:CALLS{depthClause}]->(callee:Method) " +
                "WHERE callee.id <> $methodId " +
                "RETURN DISTINCT callee",
                new { methodId });

            var records = await result.ToListAsync();
            return records.Select(r => MapToMethodNode(r["callee"].As<INode>())).ToList();
        });
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

    private async Task<CallGraph> ReadCallGraphFromTransaction(IAsyncQueryRunner tx, string callGraphId)
    {
        // Fetch all methods
        var methodsResult = await tx.RunAsync(
            "MATCH (m:Method {callGraphId: $callGraphId}) RETURN m",
            new { callGraphId });
        var methodRecords = await methodsResult.ToListAsync();

        var methods = new ConcurrentDictionary<string, MethodNode>();
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

        var callsBag = new ConcurrentBag<MethodCall>();
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

        return new CallGraph(callsBag)
        {
            Id = callGraphId,
            Methods = methods,
        };
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
