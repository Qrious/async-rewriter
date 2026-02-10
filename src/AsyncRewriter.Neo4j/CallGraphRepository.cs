using System;
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
public class CallGraphRepository : ICallGraphRepository, IAsyncDisposable, IDisposable
{
    private readonly IDriver _driver;
    private const int BatchSize = 500;

    public CallGraphRepository(string uri, string user, string password)
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    public CallGraphRepository(IDriver driver)
    {
        _driver = driver;
    }

    /// <summary>
    /// Creates indexes for Method and CallGraph nodes and CALLS relationships.
    /// Should be called once at startup before storing data.
    /// </summary>
    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        // Node indexes
        await session.RunAsync("CREATE INDEX method_id IF NOT EXISTS FOR (m:Method) ON (m.id)");
        await session.RunAsync("CREATE INDEX method_name IF NOT EXISTS FOR (m:Method) ON (m.name)");
        await session.RunAsync("CREATE INDEX method_type IF NOT EXISTS FOR (m:Method) ON (m.containingType)");
        await session.RunAsync("CREATE INDEX callgraph_id IF NOT EXISTS FOR (cg:CallGraph) ON (cg.id)");
        await session.RunAsync("CREATE INDEX callgraph_project IF NOT EXISTS FOR (cg:CallGraph) ON (cg.projectName)");

        // Composite index for lookups by namespace + type
        await session.RunAsync("CREATE INDEX method_ns_type IF NOT EXISTS FOR (m:Method) ON (m.containingNamespace, m.containingType)");
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

        // 1. Clean any existing data for this project to keep the graph fresh
        progressCallback?.Invoke("Clearing existing data", 0, 3);
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (cg:CallGraph {projectName: $projectName}) " +
                "OPTIONAL MATCH (cg)-[:CONTAINS]->(m:Method) " +
                "OPTIONAL MATCH (m)-[r:CALLS]->() " +
                "DELETE r, m, cg",
                new { projectName = callGraph.ProjectName });
        });

        // 2. Create CallGraph node
        progressCallback?.Invoke("Creating CallGraph node", 1, 3);
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                @"CREATE (cg:CallGraph {
                    id: $id,
                    projectName: $projectName,
                    createdAt: $createdAt,
                    rootAsyncMethods: $rootAsyncMethods,
                    syncWrapperMethods: $syncWrapperMethods,
                    floodedMethods: $floodedMethods
                })",
                new
                {
                    id = callGraph.Id,
                    projectName = callGraph.ProjectName,
                    createdAt = callGraph.CreatedAt.ToString("O"),
                    rootAsyncMethods = callGraph.RootAsyncMethods.ToList(),
                    syncWrapperMethods = callGraph.SyncWrapperMethods.ToList(),
                    floodedMethods = callGraph.FloodedMethods.ToList()
                });
        });

        // 3. Create Method nodes in batches
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
                        id: m.id,
                        name: m.name,
                        containingType: m.containingType,
                        containingNamespace: m.containingNamespace,
                        returnType: m.returnType,
                        parameters: m.parameters,
                        filePath: m.filePath,
                        startLine: m.startLine,
                        endLine: m.endLine,
                        isAsync: m.isAsync,
                        requiresAsyncTransformation: m.requiresAsyncTransformation,
                        isSyncWrapper: m.isSyncWrapper,
                        signature: m.signature,
                        isInterfaceMethod: m.isInterfaceMethod,
                        isReturnTypeParameter: m.isReturnTypeParameter,
                        implementsInterfaceMethods: m.implementsInterfaceMethods
                    })
                    WITH method, m
                    MATCH (cg:CallGraph {id: $callGraphId})
                    CREATE (cg)-[:CONTAINS]->(method)",
                    new
                    {
                        callGraphId = callGraph.Id,
                        methods = batch.Select(m => new Dictionary<string, object?>
                        {
                            ["id"] = m.Id,
                            ["name"] = m.Name,
                            ["containingType"] = m.ContainingType,
                            ["containingNamespace"] = m.ContainingNamespace,
                            ["returnType"] = m.ReturnType,
                            ["parameters"] = m.Parameters,
                            ["filePath"] = m.FilePath,
                            ["startLine"] = m.StartLine,
                            ["endLine"] = m.EndLine,
                            ["isAsync"] = m.IsAsync,
                            ["requiresAsyncTransformation"] = m.RequiresAsyncTransformation,
                            ["isSyncWrapper"] = m.IsSyncWrapper,
                            ["signature"] = m.Signature,
                            ["isInterfaceMethod"] = m.IsInterfaceMethod,
                            ["isReturnTypeParameter"] = m.IsReturnTypeParameter,
                            ["implementsInterfaceMethods"] = m.ImplementsInterfaceMethods
                        }).ToList()
                    });
            });
        }

        // 4. Create CALLS relationships in batches
        var calls = callGraph.Calls.ToList();
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
                    MATCH (caller:Method {id: c.callerId})
                    MATCH (callee:Method {id: c.calleeId})
                    CREATE (caller)-[:CALLS {
                        id: c.id,
                        callerSignature: c.callerSignature,
                        calleeSignature: c.calleeSignature,
                        lineNumber: c.lineNumber,
                        filePath: c.filePath,
                        requiresAwait: c.requiresAwait
                    }]->(callee)",
                    new
                    {
                        calls = batch.Select(c => new Dictionary<string, object>
                        {
                            ["id"] = c.Id,
                            ["callerId"] = c.CallerId,
                            ["calleeId"] = c.CalleeId,
                            ["callerSignature"] = c.CallerSignature,
                            ["calleeSignature"] = c.CalleeSignature,
                            ["lineNumber"] = c.LineNumber,
                            ["filePath"] = c.FilePath,
                            ["requiresAwait"] = c.RequiresAwait
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
            // Fetch the CallGraph node
            var cgResult = await tx.RunAsync(
                "MATCH (cg:CallGraph {id: $id}) RETURN cg",
                new { id });
            var cgRecord = await cgResult.SingleAsync();
            if (cgRecord == null) return null;

            return await ReadCallGraphFromTransaction(tx, cgRecord);
        });
    }

    public async Task<CallGraph?> GetCallGraphByProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        return await session.ExecuteReadAsync(async tx =>
        {
            var cgResult = await tx.RunAsync(
                "MATCH (cg:CallGraph {projectName: $projectName}) RETURN cg ORDER BY cg.createdAt DESC LIMIT 1",
                new { projectName });
            var cgRecord = await cgResult.SingleAsync();
            if (cgRecord == null) return null;

            return await ReadCallGraphFromTransaction(tx, cgRecord);
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
                "MATCH (cg:CallGraph {id: $id}) " +
                "OPTIONAL MATCH (cg)-[:CONTAINS]->(m:Method) " +
                "OPTIONAL MATCH (m)-[r:CALLS]->() " +
                "DELETE r, m, cg",
                new { id });
        });
    }

    private async Task<CallGraph> ReadCallGraphFromTransaction(IAsyncQueryRunner tx, IRecord cgRecord)
    {
        var cgNode = cgRecord["cg"].As<INode>();
        var callGraph = new CallGraph
        {
            Id = cgNode["id"].As<string>(),
            ProjectName = cgNode["projectName"].As<string>(),
            CreatedAt = DateTime.Parse(cgNode["createdAt"].As<string>()),
            RootAsyncMethods = cgNode["rootAsyncMethods"].As<List<string>>().ToHashSet(),
            SyncWrapperMethods = cgNode["syncWrapperMethods"].As<List<string>>().ToHashSet(),
            FloodedMethods = cgNode["floodedMethods"].As<List<string>>().ToHashSet()
        };

        // Fetch all methods
        var methodsResult = await tx.RunAsync(
            "MATCH (cg:CallGraph {id: $id})-[:CONTAINS]->(m:Method) RETURN m",
            new { id = callGraph.Id });
        var methodRecords = await methodsResult.ToListAsync();

        foreach (var record in methodRecords)
        {
            var method = MapToMethodNode(record["m"].As<INode>());
            callGraph.AddMethod(method);
        }

        // Fetch all call relationships
        var callsResult = await tx.RunAsync(
            "MATCH (cg:CallGraph {id: $id})-[:CONTAINS]->(caller:Method)-[r:CALLS]->(callee:Method) " +
            "RETURN r, caller.id AS callerId, callee.id AS calleeId",
            new { id = callGraph.Id });
        var callRecords = await callsResult.ToListAsync();

        foreach (var record in callRecords)
        {
            var rel = record["r"].As<IRelationship>();
            var call = new MethodCall
            {
                Id = rel["id"].As<string>(),
                CallerId = record["callerId"].As<string>(),
                CalleeId = record["calleeId"].As<string>(),
                CallerSignature = rel["callerSignature"].As<string>(),
                CalleeSignature = rel["calleeSignature"].As<string>(),
                LineNumber = rel["lineNumber"].As<int>(),
                FilePath = rel["filePath"].As<string>(),
                RequiresAwait = rel["requiresAwait"].As<bool>()
            };
            callGraph.AddCall(call);
        }

        return callGraph;
    }

    private static MethodNode MapToMethodNode(INode node)
    {
        return new MethodNode
        {
            Id = node["id"].As<string>(),
            Name = node["name"].As<string>(),
            ContainingType = node["containingType"].As<string>(),
            ContainingNamespace = node["containingNamespace"].As<string>(),
            ReturnType = node["returnType"].As<string>(),
            Parameters = node["parameters"].As<List<string>>(),
            FilePath = node["filePath"].As<string>(),
            StartLine = node["startLine"].As<int>(),
            EndLine = node["endLine"].As<int>(),
            IsAsync = node["isAsync"].As<bool>(),
            RequiresAsyncTransformation = node["requiresAsyncTransformation"].As<bool>(),
            IsSyncWrapper = node["isSyncWrapper"].As<bool>(),
            Signature = node["signature"].As<string>(),
            IsInterfaceMethod = node["isInterfaceMethod"].As<bool>(),
            IsReturnTypeParameter = node["isReturnTypeParameter"].As<bool>(),
            ImplementsInterfaceMethods = node["implementsInterfaceMethods"].As<List<string>>()
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
