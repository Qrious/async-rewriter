using System.CommandLine;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Neo4j;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

public class FloodCallGraphCommand : Command
{
    private readonly ILogger<FloodCallGraphCommand> _logger;
    private readonly IAsyncCallGraphFlooder _flooder;
    private readonly IDirtyTaskMethodsExtractor _dirtyTaskMethodsExtractor;

    public FloodCallGraphCommand(ILogger<FloodCallGraphCommand> logger, IAsyncCallGraphFlooder flooder, IDirtyTaskMethodsExtractor dirtyTaskMethodsExtractor) : base(
        "flood", "Flood a existing callgraph")
    {
        _logger = logger;
        _flooder = flooder;
        _dirtyTaskMethodsExtractor = dirtyTaskMethodsExtractor;
        var callGraphId = new Argument<string>("callgraph", "The id of the call graph to flood");
        var neo4jUriOption = new Option<string>(
            aliases: new[]
            {
                "--uri", "-u"
            },
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var neo4jUserOption = new Option<string>(
            aliases: new[]
            {
                "--neo4j-user"
            },
            description: "Neo4j username",
            getDefaultValue: () => "");
        var neo4jPasswordOption = new Option<string>(
            aliases: new[]
            {
                "--neo4j-password"
            },
            description: "Neo4j password",
            getDefaultValue: () => "");
        var newGraphIdOption = new Option<string?>(
            aliases: new[]
            {
                "--new-graph-id"
            },
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

        _logger.LogInformation("Loading call Graph: {CallGraphId}", callGraphId);
        var callGraph = await repository.Load(callGraphId);

        _logger.LogInformation("Call graph loaded with {MethodCount} methods, {CallCount} calls, {ImplementationsCount} implementations and {OverridesCount} overrides!",
            callGraph.Methods.Count, callGraph.Calls.Count, callGraph.InterfaceImplementations.Count, callGraph.MethodOverrides.Count);

        _logger.LogInformation("Analyzing dirty task methods in call graph...");
        var dirtyTaskMethodInfos = _dirtyTaskMethodsExtractor.Extract(callGraph);
        _logger.LogInformation("Found {DirtyTaskMethodCount} dirty task methods in call graph!", dirtyTaskMethodInfos.Count);

        foreach (var dirtyTaskMethodInfo in dirtyTaskMethodInfos)
        {
            _logger.LogInformation("Dirty Task Method: {MethodId} ({MethodName})", dirtyTaskMethodInfo.MethodId, callGraph.Methods[dirtyTaskMethodInfo.MethodId].Name);
        }

        var floodedGraph = await _flooder.Flood(callGraph, new HashSet<string>(dirtyTaskMethodInfos.Select(m => m.MethodId)), newGraphId);

        _logger.LogInformation("Storing call graph ({MethodsCount} methods, {CallsCount} calls)...", callGraph.Methods.Count, callGraph.Calls.Count);

        await repository.Save(floodedGraph);

        _logger.LogInformation("Call graph successfully stored in Neo4j!");
    }
}