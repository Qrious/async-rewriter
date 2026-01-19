using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j.Configuration;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AsyncRewriter.Neo4j;

/// <summary>
/// Neo4j repository for storing and querying call graphs
/// </summary>
public class CallGraphRepository : ICallGraphRepository, IAsyncDisposable
{
    private readonly IDriver _driver;
    private readonly Neo4jOptions _options;

    public CallGraphRepository(IOptions<Neo4jOptions> options)
    {
        _options = options.Value;
        _driver = GraphDatabase.Driver(_options.Uri, AuthTokens.Basic(_options.Username, _options.Password));
    }

    public Task StoreCallGraphAsync(CallGraph callGraph, CancellationToken cancellationToken = default)
    {
        return StoreCallGraphAsync(callGraph, null, cancellationToken);
    }

    public async Task StoreCallGraphAsync(CallGraph callGraph, Action<string, int, int>? progressCallback, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_options.Database));

        var methodsList = callGraph.Methods.Values.ToList();
        var callsList = callGraph.Calls.ToList();
        var totalMethods = methodsList.Count;
        var totalCalls = callsList.Count;

        await session.ExecuteWriteAsync(async tx =>
        {
            // Create CallGraph node
            progressCallback?.Invoke("Creating call graph node...", 0, totalMethods);
            await tx.RunAsync(
                @"MERGE (cg:CallGraph {id: $id})
                  SET cg.projectName = $projectName,
                      cg.createdAt = $createdAt,
                      cg.syncWrapperMethods = $syncWrapperMethods",
                new
                {
                    id = callGraph.Id,
                    projectName = callGraph.ProjectName,
                    createdAt = callGraph.CreatedAt,
                    syncWrapperMethods = callGraph.SyncWrapperMethods.ToList()
                });

            // Create Method nodes
            var methodsProcessed = 0;
            foreach (var method in methodsList)
            {
                await tx.RunAsync(
                    @"MERGE (m:Method {id: $id})
                      SET m.name = $name,
                          m.containingType = $containingType,
                          m.containingNamespace = $containingNamespace,
                          m.returnType = $returnType,
                          m.parameters = $parameters,
                          m.filePath = $filePath,
                          m.startLine = $startLine,
                          m.endLine = $endLine,
                          m.isAsync = $isAsync,
                          m.requiresAsyncTransformation = $requiresAsyncTransformation,
                          m.asyncReturnType = $asyncReturnType,
                          m.signature = $signature,
                          m.sourceCode = $sourceCode,
                          m.isInterfaceMethod = $isInterfaceMethod,
                          m.asyncPropagationReasons = $asyncPropagationReasons
                       MERGE (cg:CallGraph {id: $callGraphId})
                       MERGE (cg)-[:CONTAINS]->(m)",
                    new
                    {
                        id = method.Id,
                        name = method.Name,
                        containingType = method.ContainingType,
                        containingNamespace = method.ContainingNamespace,
                        returnType = method.ReturnType,
                        parameters = method.Parameters,
                        filePath = method.FilePath,
                        startLine = method.StartLine,
                        endLine = method.EndLine,
                        isAsync = method.IsAsync,
                        requiresAsyncTransformation = method.RequiresAsyncTransformation,
                        asyncReturnType = method.AsyncReturnType ?? "",
                        signature = method.Signature,
                        sourceCode = method.SourceCode ?? "",
                        isInterfaceMethod = method.IsInterfaceMethod,
                        asyncPropagationReasons = method.AsyncPropagationReasons,
                        callGraphId = callGraph.Id
                    });


                methodsProcessed++;
                if (methodsProcessed % 100 == 0 || methodsProcessed == totalMethods)
                {
                    progressCallback?.Invoke($"Writing methods ({methodsProcessed}/{totalMethods})...", methodsProcessed, totalMethods);
                }
            }

            // Create CALLS relationships
            var callsProcessed = 0;
            progressCallback?.Invoke($"Writing call relationships (0/{totalCalls})...", 0, totalCalls);
            foreach (var call in callsList)
            {
                await tx.RunAsync(
                    @"MATCH (caller:Method {id: $callerId})
                      MATCH (callee:Method {id: $calleeId})
                      MERGE (caller)-[c:CALLS {id: $id}]->(callee)
                      SET c.lineNumber = $lineNumber,
                          c.filePath = $filePath,
                          c.requiresAwait = $requiresAwait",
                    new
                    {
                        id = call.Id,
                        callerId = call.CallerId,
                        calleeId = call.CalleeId,
                        lineNumber = call.LineNumber,
                        filePath = call.FilePath,
                        requiresAwait = call.RequiresAwait
                    });

                callsProcessed++;
                if (callsProcessed % 100 == 0 || callsProcessed == totalCalls)
                {
                    progressCallback?.Invoke($"Writing call relationships ({callsProcessed}/{totalCalls})...", callsProcessed, totalCalls);
                }
            }

            // Mark root async methods
            progressCallback?.Invoke("Marking root async methods...", 0, 0);
            foreach (var rootMethodId in callGraph.RootAsyncMethods)
            {
                await tx.RunAsync(
                    @"MATCH (m:Method {id: $methodId})
                      MATCH (cg:CallGraph {id: $callGraphId})
                      MERGE (cg)-[:ROOT_ASYNC]->(m)",
                    new { methodId = rootMethodId, callGraphId = callGraph.Id });
            }

            // Mark flooded methods
            progressCallback?.Invoke("Marking flooded methods...", 0, 0);
            foreach (var floodedMethodId in callGraph.FloodedMethods)
            {
                await tx.RunAsync(
                    @"MATCH (m:Method {id: $methodId})
                      SET m.flooded = true",
                    new { methodId = floodedMethodId });
            }
        });
    }

    public async Task<CallGraph?> GetCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_options.Database));

        return await session.ExecuteReadAsync(async tx =>
        {
            // Get CallGraph
            var cgResult = await tx.RunAsync(
                "MATCH (cg:CallGraph {id: $id}) RETURN cg",
                new { id });

            if (!await cgResult.FetchAsync()) return null;
            var cgRecord = cgResult.Current;

            var cgNode = cgRecord["cg"].As<INode>();
            var callGraph = new CallGraph
            {
                Id = cgNode.Properties["id"].As<string>(),
                ProjectName = cgNode.Properties["projectName"].As<string>(),
                CreatedAt = cgNode.Properties["createdAt"].As<ZonedDateTime>().ToDateTimeOffset().UtcDateTime,
                SyncWrapperMethods = cgNode.Properties.ContainsKey("syncWrapperMethods")
                    ? new HashSet<string>(cgNode.Properties["syncWrapperMethods"].As<List<string>>())
                    : new HashSet<string>()
            };

            // Get all methods
            var methodsResult = await tx.RunAsync(
                @"MATCH (cg:CallGraph {id: $id})-[:CONTAINS]->(m:Method)
                  RETURN m",
                new { id });

            await foreach (var record in methodsResult)
            {
                var methodNode = record["m"].As<INode>();
                var method = MapToMethodNode(methodNode);
                callGraph.AddMethod(method);
            }

            // Get all calls
            var callsResult = await tx.RunAsync(
                @"MATCH (cg:CallGraph {id: $id})-[:CONTAINS]->(:Method)-[c:CALLS]->(:Method)
                  RETURN c, startNode(c).id as callerId, endNode(c).id as calleeId",
                new { id });

            await foreach (var record in callsResult)
            {
                var callRel = record["c"].As<IRelationship>();
                var methodCall = new MethodCall
                {
                    Id = callRel.Properties["id"].As<string>(),
                    CallerId = record["callerId"].As<string>(),
                    CalleeId = record["calleeId"].As<string>(),
                    LineNumber = callRel.Properties["lineNumber"].As<int>(),
                    FilePath = callRel.Properties["filePath"].As<string>(),
                    RequiresAwait = callRel.Properties["requiresAwait"].As<bool>()
                };
                callGraph.AddCall(methodCall);
            }

            // Get root async methods
            var rootsResult = await tx.RunAsync(
                @"MATCH (cg:CallGraph {id: $id})-[:ROOT_ASYNC]->(m:Method)
                  RETURN m.id as methodId",
                new { id });

            await foreach (var record in rootsResult)
            {
                callGraph.RootAsyncMethods.Add(record["methodId"].As<string>());
            }

            // Get flooded methods
            var floodedResult = await tx.RunAsync(
                @"MATCH (cg:CallGraph {id: $id})-[:CONTAINS]->(m:Method)
                  WHERE m.flooded = true
                  RETURN m.id as methodId",
                new { id });

            await foreach (var record in floodedResult)
            {
                callGraph.FloodedMethods.Add(record["methodId"].As<string>());
            }

            return callGraph;
        });
    }

    public async Task<CallGraph?> GetCallGraphByProjectAsync(string projectName, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_options.Database));

        var result = await session.ExecuteReadAsync(async tx =>
        {
            var cgResult = await tx.RunAsync(
                "MATCH (cg:CallGraph {projectName: $projectName}) RETURN cg.id as id ORDER BY cg.createdAt DESC LIMIT 1",
                new { projectName });

            if (!await cgResult.FetchAsync()) return null;
            return cgResult.Current["id"].As<string>();
        });

        if (result == null) return null;

        return await GetCallGraphAsync(result, cancellationToken);
    }

    public async Task<List<MethodNode>> FindCallersAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_options.Database));

        return await session.ExecuteReadAsync(async tx =>
        {
            var depthClause = depth > 0 ? $"*1..{depth}" : "*";
            var query = $@"
                MATCH (callers:Method)-[c:CALLS{depthClause}]->(m:Method {{id: $methodId}})
                RETURN DISTINCT callers";

            var result = await tx.RunAsync(query, new { methodId });
            var methods = new List<MethodNode>();

            await foreach (var record in result)
            {
                var methodNode = record["callers"].As<INode>();
                methods.Add(MapToMethodNode(methodNode));
            }

            return methods;
        });
    }

    public async Task<List<MethodNode>> FindCalleesAsync(string methodId, int depth = -1, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_options.Database));

        return await session.ExecuteReadAsync(async tx =>
        {
            var depthClause = depth > 0 ? $"*1..{depth}" : "*";
            var query = $@"
                MATCH (m:Method {{id: $methodId}})-[c:CALLS{depthClause}]->(callees:Method)
                RETURN DISTINCT callees";

            var result = await tx.RunAsync(query, new { methodId });
            var methods = new List<MethodNode>();

            await foreach (var record in result)
            {
                var methodNode = record["callees"].As<INode>();
                methods.Add(MapToMethodNode(methodNode));
            }

            return methods;
        });
    }

    public async Task DeleteCallGraphAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession(o => o.WithDatabase(_options.Database));

        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                @"MATCH (cg:CallGraph {id: $id})
                  OPTIONAL MATCH (cg)-[:CONTAINS]->(m:Method)
                  DETACH DELETE cg, m",
                new { id });
        });
    }

    private MethodNode MapToMethodNode(INode node)
    {
        return new MethodNode
        {
            Id = node.Properties["id"].As<string>(),
            Name = node.Properties["name"].As<string>(),
            ContainingType = node.Properties["containingType"].As<string>(),
            ContainingNamespace = node.Properties["containingNamespace"].As<string>(),
            ReturnType = node.Properties["returnType"].As<string>(),
            Parameters = node.Properties["parameters"].As<List<string>>(),
            FilePath = node.Properties["filePath"].As<string>(),
            StartLine = node.Properties["startLine"].As<int>(),
            EndLine = node.Properties["endLine"].As<int>(),
            IsAsync = node.Properties["isAsync"].As<bool>(),
            RequiresAsyncTransformation = node.Properties["requiresAsyncTransformation"].As<bool>(),
            AsyncReturnType = node.Properties.ContainsKey("asyncReturnType")
                ? node.Properties["asyncReturnType"].As<string>()
                : null,
            Signature = node.Properties["signature"].As<string>(),
            SourceCode = node.Properties.ContainsKey("sourceCode")
                ? node.Properties["sourceCode"].As<string>()
                : null,
            IsInterfaceMethod = node.Properties.ContainsKey("isInterfaceMethod")
                && node.Properties["isInterfaceMethod"].As<bool>(),
            AsyncPropagationReasons = node.Properties.ContainsKey("asyncPropagationReasons")
                ? node.Properties["asyncPropagationReasons"].As<List<string>>()
                : new List<string>()
        };
    }

    public async ValueTask DisposeAsync()
    {
        await _driver.DisposeAsync();
    }
}
