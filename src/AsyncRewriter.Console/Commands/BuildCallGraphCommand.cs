using System.CommandLine;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Neo4j;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

public class BuildCallGraphCommand : Command
{
    private readonly ICallGraphBuilder _callGraphBuilder;
    private readonly ILogger<BuildCallGraphCommand> _logger;

    public BuildCallGraphCommand(ICallGraphBuilder callGraphBuilder, ILogger<BuildCallGraphCommand> logger) : base("build", "Build a call graph from a Solution")
    {
        _callGraphBuilder = callGraphBuilder;
        _logger = logger;
        var solutionPathArgument = new Argument<string>("solution-path", "The path to the solution to build a call graph from");
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
        var graphIdOption = new Option<string?>(
            aliases: new[]
            {
                "--graph-id"
            },
            description: "The id of the call graph to store in Neo4j. If not provided, a random id will be generated.",
            getDefaultValue: () => Guid.NewGuid().ToString());

        AddArgument(solutionPathArgument);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(graphIdOption);

        this.SetHandler(ExecuteAsync, solutionPathArgument, neo4jUriOption, neo4jUserOption, neo4jPasswordOption, graphIdOption);
    }

    private async Task ExecuteAsync(string solutionPath, string neo4jUri, string neo4jUser, string neo4jPassword, string? graphId)
    {
        _logger.LogInformation("Analyzing solution: {SolutionPath}", solutionPath);

        var callGraph = await _callGraphBuilder.Build(solutionPath, graphId ?? Guid.NewGuid().ToString());

        _logger.LogInformation("Analysis completed successfully!");
        _logger.LogInformation("Methods found: {MethodsCount}", callGraph.Methods.Count);
        _logger.LogInformation("Method calls: {CallsCount}", callGraph.Calls.Count);

        var neo4JCredentials = new Neo4JCredentials(new Uri(neo4jUri), neo4jUser, neo4jPassword);
        _logger.LogInformation("Connecting to Neo4j at {Neo4JUri}...", neo4JCredentials.Url);

        await using var repository = new Neo4jCallGraphRepository(neo4JCredentials, _logger);

        _logger.LogInformation("Ensuring indexes...");
        await repository.EnsureIndexesAsync();

        _logger.LogInformation("Storing call graph ({MethodsCount} methods, {CallsCount} calls)...", callGraph.Methods.Count, callGraph.Calls.Count);

        await repository.Save(callGraph);

        _logger.LogInformation("Call graph successfully stored in Neo4j!");
        System.Console.ResetColor();
    }
}