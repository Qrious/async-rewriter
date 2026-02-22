using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using AsyncRewriter.Transformation;
using Microsoft.CodeAnalysis.MSBuild;
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
        var solutionOption = new Option<string>(
            aliases: ["--solution", "-s"],
            description: "Path to the .sln file. When provided the transformer uses a full Roslyn " +
                         "semantic model for accurate symbol-based method matching.");
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
        AddOption(solutionOption);
        AddOption(neo4jUriOption);
        AddOption(neo4jUserOption);
        AddOption(neo4jPasswordOption);
        AddOption(dryRunOption);

        this.SetHandler(ExecuteAsync,
            callGraphId, solutionOption,
            neo4jUriOption, neo4jUserOption, neo4jPasswordOption, dryRunOption);
    }

    private async Task ExecuteAsync(
        string callGraphId,
        string? solutionPath,
        string neo4jUri,
        string neo4jUser,
        string neo4jPassword,
        bool dryRun)
    {
        var neo4jCredentials = new Neo4JCredentials(new Uri(neo4jUri), neo4jUser, neo4jPassword);
        _logger.LogInformation("Connecting to Neo4j at {Neo4JUri}...", neo4jCredentials.Url);

        await using var repository = new Neo4jCallGraphRepository(neo4jCredentials, _logger);

        _logger.LogInformation("Loading flooded call graph: {CallGraphId}", callGraphId);
        var callGraph = await repository.Load<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            callGraphId, default);

        _logger.LogInformation(
            "Call graph loaded: {MethodCount} methods ({FloodedCount} flooded), {CallCount} calls",
            callGraph.Methods.Count,
            callGraph.MethodMetadata.Count,
            callGraph.Calls.Count);

        var documentProvider = await BuildDocumentProviderAsync(solutionPath);

        _logger.LogInformation("Running transformation{DryRun}...", dryRun ? " (dry run)" : "");
        var transformations = await _transformer.TransformAsync(callGraph, documentProvider);

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

    private async Task<IDocumentSemanticModelProvider> BuildDocumentProviderAsync(string? solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath))
        {
            _logger.LogWarning(
                "No --solution path provided. Transformation will use a minimal fallback compilation " +
                "instead of a full semantic model. Provide --solution for best results.");
            return NullDocumentSemanticModelProvider.Instance;
        }

        if (!File.Exists(solutionPath))
        {
            _logger.LogWarning(
                "Solution file not found at {SolutionPath}. Falling back to minimal compilation.",
                solutionPath);
            return NullDocumentSemanticModelProvider.Instance;
        }

        _logger.LogInformation("Loading solution {SolutionPath} for semantic model...", solutionPath);
        var workspace = MSBuildWorkspace.Create();
        var solution = await workspace.OpenSolutionAsync(solutionPath);
        _logger.LogInformation("Solution loaded ({ProjectCount} projects).", solution.Projects.Count());
        return SolutionDocumentSemanticModelProvider.Create(solution);
    }
}
