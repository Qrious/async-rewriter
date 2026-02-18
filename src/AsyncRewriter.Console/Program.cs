using System.CommandLine;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        await using var services = serviceCollection.BuildServiceProvider(true);
        var logger = services.GetRequiredService<ILogger<Program>>();

        // Register MSBuild
        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Warning: Could not register MSBuild: {Message}", ex.Message);
        }

        var rootCommand = new RootCommand("Async Rewriter - Analyze and transform C# codebases from sync to async");

        // Shared options
        var solutionPathArgument = new Argument<string>("solution-path", "The path to the solution to build a call graph from");
        var neo4jUriOption = new Option<string>(
            aliases: new[] { "--uri", "-u" },
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var neo4jUserOption = new Option<string>(
            aliases: new[] { "--neo4j-user" },
            description: "Neo4j username",
            getDefaultValue: () => "neo4j");
        var neo4jPasswordOption = new Option<string>(
            aliases: new[] { "--neo4j-password" },
            description: "Neo4j password",
            getDefaultValue: () => "asyncrewriter");
        var deleteGraphOption = new Option<bool>(
            aliases: new[] { "--delete-graph" },
            description: "Delete existing call graph data in Neo4j before storing the new graph",
            getDefaultValue: () => false);

        // Build command - builds call graph and stores in Neo4j
        var buildCallGraphCommand = new Command("build", "Build a call graph from a Solution");
        buildCallGraphCommand.AddArgument(solutionPathArgument);
        buildCallGraphCommand.AddOption(neo4jUriOption);
        buildCallGraphCommand.AddOption(neo4jUserOption);
        buildCallGraphCommand.AddOption(neo4jPasswordOption);
        buildCallGraphCommand.AddOption(deleteGraphOption);

        buildCallGraphCommand.SetHandler(async (solutionPath, neo4jUri, neo4jUser, neo4jPassword, deleteGraph) =>
        {
            var neo4JCredentials = new Neo4JCredentials(new Uri(neo4jUri), neo4jUser, neo4jPassword);
            await BuildCallGraphAsync(services.GetRequiredService<ICallGraphBuilder>(), services.GetRequiredService<ILogger<Program>>(), solutionPath, neo4JCredentials, deleteGraph);
        }, solutionPathArgument, neo4jUriOption, neo4jUserOption, neo4jPasswordOption, deleteGraphOption);

        rootCommand.AddCommand(buildCallGraphCommand);

        return await rootCommand.InvokeAsync(args);
    }

    private static void ConfigureServices(ServiceCollection serviceCollection)
    {
        serviceCollection.AddTransient<ICallGraphBuilder, CallGraphBuilder>();
        serviceCollection.AddTransient<IMethodCallExtractor, MethodCallExtractor>();
        serviceCollection.AddTransient<IMethodExtractor, MethodExtractor>();
        serviceCollection.AddSingleton<IMethodExtractorFactory, MethodExtractorFactory>();
        serviceCollection.AddSingleton<IMethodCallExtractorFactory, MethodCallExtractorFactory>();

        serviceCollection.AddTransient<ITaskWrapperExtractor, TaskWrapperExtractor>();
        serviceCollection.AddTransient<IAsyncFloodingAnalyzer, AsyncFloodingAnalyzer>();
        serviceCollection.AddTransient<IAsyncTransformer, AsyncTransformer>();
        serviceCollection.AddSingleton<ICallGraphRepository, Neo4jCallGraphRepository>();
        serviceCollection.AddLogging(c => c.AddSimpleConsole());
    }

    static async Task BuildCallGraphAsync(ICallGraphBuilder callGraphBuilder, ILogger logger, string solutionPath, Neo4JCredentials neo4JCredentials, bool deleteGraph)
    {
        try
        {
            logger.LogInformation("Analyzing solution: {SolutionPath}", solutionPath);

            var callGraph = await callGraphBuilder.Build(solutionPath);

            logger.LogInformation("Analysis completed successfully!");
            logger.LogInformation("Methods found: {MethodsCount}", callGraph.Methods.Count);
            logger.LogInformation("Method calls: {CallsCount}", callGraph.Calls.Count);

            logger.LogInformation("Connecting to Neo4j at {Neo4JUri}...", neo4JCredentials.Url);

            await using var repository = new Neo4jCallGraphRepository(neo4JCredentials, logger);

            if (deleteGraph)
            {
                logger.LogInformation("Deleting existing call graph...");
                await repository.DeleteAllCallGraphsAsync();
            }

            logger.LogInformation("Ensuring indexes...");
            await repository.EnsureIndexesAsync();

            logger.LogInformation("Storing call graph ({MethodsCount} methods, {CallsCount} calls)...", callGraph.Methods.Count, callGraph.Calls.Count);

            await repository.StoreCallGraphAsync(callGraph);

            logger.LogInformation("Call graph successfully stored in Neo4j!");
            System.Console.ResetColor();
        }
        catch (Exception ex)
        {
            logger.LogInformation("Error: {ExMessage}", ex.Message);
            logger.LogInformation(ex.StackTrace);
            System.Console.ResetColor();
        }
    }
}
