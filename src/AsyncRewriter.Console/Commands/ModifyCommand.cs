using System.CommandLine;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using AsyncRewriter.Transformation;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console.Commands;

public class ModifyCommand : Command
{
    private readonly ILogger<ModifyCommand> _logger;
    private readonly FloodedCallGraphTransformer _transformer;

    public ModifyCommand(ILogger<ModifyCommand> logger, FloodedCallGraphTransformer transformer)
        : base("modify", "Apply async transformations to source files based on a flooded call graph")
    {
        _logger = logger;
        _transformer = transformer;

        var callGraphId = new Argument<string>("callgraph", "The id of the flooded call graph to transform");
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
        var dryRunOption = new Option<bool>(
            aliases: ["--dry-run", "-n"],
            description: "Print which files would be modified without writing changes",
            getDefaultValue: () => false);

        AddArgument(callGraphId);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync, callGraphId, neo4jUriOption, neo4jUserOption, neo4jPasswordOption, dryRunOption);
    }

    private async Task ExecuteAsync(string callGraphId, string neo4jUri, string neo4jUser, string neo4jPassword, bool dryRun)
    {
        var neo4jCredentials = new Neo4JCredentials(new Uri(neo4jUri), neo4jUser, neo4jPassword);
        _logger.LogInformation("Connecting to Neo4j at {Neo4JUri}...", neo4jCredentials.Url);

        await using var repository = new Neo4jCallGraphRepository(neo4jCredentials, _logger);

        _logger.LogInformation("Loading flooded call graph: {CallGraphId}", callGraphId);
        var callGraph = await repository.Load<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            callGraphId, CancellationToken.None);

        _logger.LogInformation(
            "Call graph loaded: {MethodCount} methods ({FloodedCount} flooded), {CallCount} calls",
            callGraph.Methods.Count,
            callGraph.MethodMetadata.Count,
            callGraph.Calls.Count);

        _logger.LogInformation("Running transformation{DryRun}...", dryRun ? " (dry run)" : "");
        var transformations = await _transformer.TransformAsync(callGraph);

        if (transformations.Count == 0)
        {
            _logger.LogInformation("No files needed modification.");
            return;
        }

        foreach (var file in transformations)
        {
            if (dryRun)
            {
                _logger.LogInformation("[dry-run] Would modify: {FilePath}", file.FilePath);
            }
            else
            {
                await File.WriteAllTextAsync(file.FilePath, file.TransformedContent);
                _logger.LogInformation("Modified: {FilePath}", file.FilePath);
            }
        }

        _logger.LogInformation(
            "{Action} {FileCount} file(s).",
            dryRun ? "Would modify" : "Modified",
            transformations.Count);
    }
}
