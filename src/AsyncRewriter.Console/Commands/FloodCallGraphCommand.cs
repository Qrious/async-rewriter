using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

public class FloodCallGraphCommand : Command
{
    private readonly ILogger<FloodCallGraphCommand> _logger;
    private readonly IAsyncCallGraphFlooder _flooder;
    private readonly IDirtyTaskMethodsExtractor _dirtyTaskMethodsExtractor;
    private readonly IEntityFrameworkSyncCallExtractor _efSyncCallExtractor;
    private readonly IOutParameterAnalyzer _outParameterAnalyzer;

    public FloodCallGraphCommand(
        ILogger<FloodCallGraphCommand> logger,
        IAsyncCallGraphFlooder flooder,
        IDirtyTaskMethodsExtractor dirtyTaskMethodsExtractor,
        IEntityFrameworkSyncCallExtractor efSyncCallExtractor,
        IOutParameterAnalyzer outParameterAnalyzer)
        : base("flood", "Flood a existing callgraph")
    {
        _logger = logger;
        _flooder = flooder;
        _dirtyTaskMethodsExtractor = dirtyTaskMethodsExtractor;
        _efSyncCallExtractor = efSyncCallExtractor;
        _outParameterAnalyzer = outParameterAnalyzer;

        var callGraphId = new Argument<string>("callgraph", "The id of the call graph to flood");
        var neo4jUriOption = new Option<string>(
            aliases: ["--uri", "-u"],
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var neo4jUserOption = new Option<string>(
            aliases: ["--neo4j-user"],
            description: "Neo4j username",
            getDefaultValue: () => "");
        var neo4jPasswordOption = new Option<string>(
            aliases: ["--neo4j-password"],
            description: "Neo4j password",
            getDefaultValue: () => "");
        var newGraphIdOption = new Option<string?>(
            aliases: ["--new-graph-id"],
            description: "The id of the new call graph to create. If not specified, the existing call graph will be overwritten.");

        AddArgument(callGraphId);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(newGraphIdOption);

        this.SetHandler(ExecuteAsync, callGraphId, neo4jUriOption, neo4jUserOption, neo4jPasswordOption, newGraphIdOption);
    }

    private async Task ExecuteAsync(string callGraphId, string neo4jUri, string neo4jUser, string neo4jPassword, string? newGraphId)
    {
        var neo4JCredentials = new Neo4JCredentials(new Uri(neo4jUri), neo4jUser, neo4jPassword);
        _logger.LogInformation("Connecting to Neo4j at {Neo4JUri}...", neo4JCredentials.Url);

        await using var repository = new Neo4jCallGraphRepository(neo4JCredentials, _logger);

        _logger.LogInformation("Loading call graph: {CallGraphId}", callGraphId);
        var callGraph = await repository.Load(callGraphId);

        _logger.LogInformation(
            "Call graph loaded with {MethodCount} methods, {CallCount} calls, {ImplementationsCount} implementations and {OverridesCount} overrides!",
            callGraph.Methods.Count, callGraph.Calls.Count,
            callGraph.InterfaceImplementations.Count, callGraph.MethodOverrides.Count);

        _logger.LogInformation("Analyzing sync wrapper methods in call graph...");
        var syncWrapperGraph = _dirtyTaskMethodsExtractor.Extract(callGraph);
        _logger.LogInformation("Found {Count} sync wrapper methods!", syncWrapperGraph.MethodMetadata.Count);

        foreach (var (id, meta) in syncWrapperGraph.MethodMetadata)
        {
            _logger.LogTrace("Sync wrapper: {MethodId} ({MethodName}) - {Reason}", id, callGraph.Methods[id].Name, meta.Reason);
        }

        _logger.LogInformation("Analyzing Entity Framework sync calls in call graph...");
        var efGraph = _efSyncCallExtractor.Extract(callGraph);
        _logger.LogInformation("Found {Count} methods calling Entity Framework sync methods!", efGraph.MethodMetadata.Count);

        foreach (var (id, meta) in efGraph.MethodMetadata)
        {
            _logger.LogTrace("EF Sync Caller: {MethodId} ({MethodName}) - {Reason}", id, callGraph.Methods[id].Name, meta.Reason);
        }

        var rootMethodIds = new HashSet<string>(syncWrapperGraph.MethodMetadata.Keys);
        rootMethodIds.UnionWith(efGraph.MethodMetadata.Keys);

        var floodedGraph = await _flooder.Flood(callGraph, rootMethodIds, newCallGraphId: newGraphId);

        _logger.LogInformation("Analyzing out parameter methods in flooded call graph...");
        var outParameterCallGraph = _outParameterAnalyzer.DetectOutParameterMethods(callGraph, floodedGraph);
        _logger.LogInformation("Found {Count} out-parameter methods!", outParameterCallGraph.MethodMetadata.Count);



        // Combine flooding metadata with sync wrapper and EF metadata into a composite graph
        var compositeMetadata = new Dictionary<string, CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>>();
        foreach (var (id, floodingMeta) in floodedGraph.MethodMetadata)
        {
            syncWrapperGraph.TryGetMethodMetadata(id, out var syncMeta);
            efGraph.TryGetMethodMetadata(id, out var efMeta);
            outParameterCallGraph.TryGetMethodMetadata(id, out var outParameterMeta);
            compositeMetadata[id] = new CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>
            {
                First = floodingMeta,
                Second = syncMeta ?? SyncWrapperMethodMetadata.None,
                Third = efMeta ?? EntityFrameworkMethodMetadata.None,
                Fourth = outParameterMeta ?? OutParameterMetadata.None
            };
        }

        var combinedGraph = new CallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            floodedGraph.Id,
            floodedGraph.BaseGraph,
            compositeMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());

        _logger.LogInformation("Storing call graph ({MethodsCount} methods, {CallsCount} calls)...",
            combinedGraph.Methods.Count, combinedGraph.Calls.Count);

        await repository.Save(combinedGraph, CancellationToken.None);

        _logger.LogInformation("Call graph successfully stored in Neo4j!");
    }
}
