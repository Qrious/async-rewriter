using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;
using Neo4j.Driver;

namespace AsyncRewriter.Neo4j;

/// <summary>
/// Stores flooding debug data as a separate graph in Neo4j (FloodedMethod nodes + FLOODED_BY relationships).
/// </summary>
public class Neo4jFloodingDebugRepository : IAsyncDisposable, IDisposable
{
    private readonly IDriver _driver;
    private const int BatchSize = 500;

    public Neo4jFloodingDebugRepository(string uri, string user, string password)
    {
        _driver = GraphDatabase.Driver(uri, AuthTokens.Basic(user, password));
    }

    public Neo4jFloodingDebugRepository(IDriver driver)
    {
        _driver = driver;
    }

    public async Task EnsureIndexesAsync(CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();
        await session.RunAsync("CREATE INDEX flooded_method_id IF NOT EXISTS FOR (m:FloodedMethod) ON (m.floodGraphId, m.methodId)");
        await session.RunAsync("CREATE INDEX flooded_method_graph IF NOT EXISTS FOR (m:FloodedMethod) ON (m.floodGraphId)");
    }

    public async Task StoreFloodingResultAsync(
        FloodingResult result,
        CallGraph originalGraph,
        CallGraph floodedGraph,
        Action<string, int, int>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        await using var session = _driver.AsyncSession();

        // Clear existing nodes for this flood graph
        await session.ExecuteWriteAsync(async tx =>
        {
            await tx.RunAsync(
                "MATCH (m:FloodedMethod {floodGraphId: $id}) DETACH DELETE m",
                new { id = result.Id });
        });

        // Build node data
        var nodeData = new List<Dictionary<string, object?>>();
        foreach (var (methodId, info) in result.FloodedMethods)
        {
            originalGraph.Methods.TryGetValue(methodId, out var origMethod);
            floodedGraph.Methods.TryGetValue(methodId, out var floodedMethod);

            var originalReturnType = origMethod?.ReturnType ?? "unknown";
            var newReturnType = floodedMethod?.ReturnType ?? "unknown";

            nodeData.Add(new Dictionary<string, object?>
            {
                ["floodGraphId"] = result.Id,
                ["methodId"] = methodId,
                ["name"] = origMethod?.Name ?? methodId,
                ["containingType"] = origMethod?.ContainingType ?? "",
                ["containingNamespace"] = origMethod?.ContainingNamespace ?? "",
                ["depth"] = info.Depth,
                ["isRoot"] = info.Reason == FloodReason.Root,
                ["originalReturnType"] = originalReturnType,
                ["newReturnType"] = newReturnType
            });
        }

        // Create nodes in batches
        var totalBatches = (int)Math.Ceiling(nodeData.Count / (double)BatchSize);
        var batchNum = 0;
        foreach (var batch in nodeData.Chunk(BatchSize))
        {
            batchNum++;
            progressCallback?.Invoke("Creating FloodedMethod nodes", batchNum, totalBatches);

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $batch AS n
                      CREATE (m:FloodedMethod {
                          floodGraphId: n.floodGraphId,
                          methodId: n.methodId,
                          name: n.name,
                          containingType: n.containingType,
                          containingNamespace: n.containingNamespace,
                          depth: n.depth,
                          isRoot: n.isRoot,
                          originalReturnType: n.originalReturnType,
                          newReturnType: n.newReturnType
                      })",
                    new { batch = batch.ToList() });
            });
        }

        // Build relationship data
        var relData = new List<Dictionary<string, object>>();
        foreach (var (methodId, info) in result.FloodedMethods)
        {
            if (info.FloodedById == null)
            {
                continue;
            }

            relData.Add(new Dictionary<string, object>
            {
                ["floodGraphId"] = result.Id,
                ["methodId"] = methodId,
                ["floodedById"] = info.FloodedById,
                ["reason"] = info.Reason.ToString()
            });
        }

        // Create relationships in batches
        totalBatches = (int)Math.Ceiling(relData.Count / (double)BatchSize);
        batchNum = 0;
        foreach (var batch in relData.Chunk(BatchSize))
        {
            batchNum++;
            progressCallback?.Invoke("Creating FLOODED_BY relationships", batchNum, totalBatches);

            await session.ExecuteWriteAsync(async tx =>
            {
                await tx.RunAsync(
                    @"UNWIND $batch AS r
                      MATCH (m:FloodedMethod {floodGraphId: r.floodGraphId, methodId: r.methodId})
                      MATCH (cause:FloodedMethod {floodGraphId: r.floodGraphId, methodId: r.floodedById})
                      CREATE (m)-[:FLOODED_BY {reason: r.reason}]->(cause)",
                    new { batch = batch.ToList() });
            });
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
