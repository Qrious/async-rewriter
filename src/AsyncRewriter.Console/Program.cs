using System.CommandLine;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Neo4j;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;

namespace AsyncRewriter.Console;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        await using var services = serviceCollection.BuildServiceProvider(true);
        
        // Register MSBuild
        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine($"Warning: Could not register MSBuild: {ex.Message}");
            System.Console.ResetColor();
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

        // Build command - builds call graph and stores in Neo4j
        var buildCallGraphCommand = new Command("build", "Build a call graph from a Solution");
        buildCallGraphCommand.AddArgument(solutionPathArgument);
        buildCallGraphCommand.AddOption(neo4jUriOption);
        buildCallGraphCommand.AddOption(neo4jUserOption);
        buildCallGraphCommand.AddOption(neo4jPasswordOption);

        buildCallGraphCommand.SetHandler(async (solutionPath, neo4jUri, neo4jUser, neo4jPassword) =>
        {
            await BuildCallGraphAsync(services.GetRequiredService<ICallGraphBuilder>(), solutionPath, neo4jUri, neo4jUser, neo4jPassword);
        }, solutionPathArgument, neo4jUriOption, neo4jUserOption, neo4jPasswordOption);

        rootCommand.AddCommand(buildCallGraphCommand);

        // Analyze command - builds call graph, stores, then offers find-sources
        var analyzeCommand = new Command("analyze", "Build a call graph and interactively analyze it");
        var analyzeSolutionPathArgument = new Argument<string>("solution-path", "The path to the solution to analyze");
        var analyzeNeo4jUriOption = new Option<string>(
            aliases: new[] { "--uri", "-u" },
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var analyzeNeo4jUserOption = new Option<string>(
            aliases: new[] { "--neo4j-user" },
            description: "Neo4j username",
            getDefaultValue: () => "neo4j");
        var analyzeNeo4jPasswordOption = new Option<string>(
            aliases: new[] { "--neo4j-password" },
            description: "Neo4j password",
            getDefaultValue: () => "asyncrewriter");

        analyzeCommand.AddArgument(analyzeSolutionPathArgument);
        analyzeCommand.AddOption(analyzeNeo4jUriOption);
        analyzeCommand.AddOption(analyzeNeo4jUserOption);
        analyzeCommand.AddOption(analyzeNeo4jPasswordOption);

        analyzeCommand.SetHandler(async (solutionPath, neo4jUri, neo4jUser, neo4jPassword) =>
        {
            await AnalyzeSolutionAsync(
                services.GetRequiredService<ICallGraphBuilder>(),
                services.GetRequiredService<ITaskWrapperExtractor>(),
                services.GetRequiredService<IAsyncFloodingAnalyzer>(),
                solutionPath, neo4jUri, neo4jUser, neo4jPassword);
        }, analyzeSolutionPathArgument, analyzeNeo4jUriOption, analyzeNeo4jUserOption, analyzeNeo4jPasswordOption);

        rootCommand.AddCommand(analyzeCommand);

        // Find sources command
        var findSourcesCommand = new Command("find-sources", "Find task wrapper methods (sync-over-async patterns) in the call graph");
        var projectNameArgument = new Argument<string>("project-name", "The project name to load the call graph for");
        var findSourcesNeo4jUriOption = new Option<string>(
            aliases: new[] { "--uri", "-u" },
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var findSourcesNeo4jUserOption = new Option<string>(
            aliases: new[] { "--neo4j-user" },
            description: "Neo4j username",
            getDefaultValue: () => "neo4j");
        var findSourcesNeo4jPasswordOption = new Option<string>(
            aliases: new[] { "--neo4j-password" },
            description: "Neo4j password",
            getDefaultValue: () => "asyncrewriter");

        findSourcesCommand.AddArgument(projectNameArgument);
        findSourcesCommand.AddOption(findSourcesNeo4jUriOption);
        findSourcesCommand.AddOption(findSourcesNeo4jUserOption);
        findSourcesCommand.AddOption(findSourcesNeo4jPasswordOption);

        findSourcesCommand.SetHandler(async (projectName, neo4jUri, neo4jUser, neo4jPassword) =>
        {
            await FindSourcesAsync(services.GetRequiredService<ITaskWrapperExtractor>(), projectName, neo4jUri, neo4jUser, neo4jPassword);
        }, projectNameArgument, findSourcesNeo4jUriOption, findSourcesNeo4jUserOption, findSourcesNeo4jPasswordOption);

        rootCommand.AddCommand(findSourcesCommand);

        // Flood command - run async flooding analysis from task wrappers
        var floodCommand = new Command("flood", "Run async flooding analysis from task wrapper methods");
        var floodProjectNameArgument = new Argument<string>("project-name", "The project name to load the call graph for");
        var floodNeo4jUriOption = new Option<string>(
            aliases: new[] { "--uri", "-u" },
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var floodNeo4jUserOption = new Option<string>(
            aliases: new[] { "--neo4j-user" },
            description: "Neo4j username",
            getDefaultValue: () => "neo4j");
        var floodNeo4jPasswordOption = new Option<string>(
            aliases: new[] { "--neo4j-password" },
            description: "Neo4j password",
            getDefaultValue: () => "asyncrewriter");

        floodCommand.AddArgument(floodProjectNameArgument);
        floodCommand.AddOption(floodNeo4jUriOption);
        floodCommand.AddOption(floodNeo4jUserOption);
        floodCommand.AddOption(floodNeo4jPasswordOption);

        floodCommand.SetHandler(async (projectName, neo4jUri, neo4jUser, neo4jPassword) =>
        {
            await FloodAsync(
                services.GetRequiredService<ITaskWrapperExtractor>(),
                services.GetRequiredService<IAsyncFloodingAnalyzer>(),
                projectName, neo4jUri, neo4jUser, neo4jPassword);
        }, floodProjectNameArgument, floodNeo4jUriOption, floodNeo4jUserOption, floodNeo4jPasswordOption);

        rootCommand.AddCommand(floodCommand);

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
        serviceCollection.AddSingleton<ICallGraphRepository, Neo4jCallGraphRepository>();
        serviceCollection.AddLogging();
    }

    static async Task BuildCallGraphAsync(ICallGraphBuilder callGraphBuilder, string solutionPath, string neo4jUri, string neo4jUser, string neo4jPassword)
    {
        try
        {
            System.Console.WriteLine($"Analyzing solution: {solutionPath}");
            System.Console.WriteLine();

            var callGraph = await callGraphBuilder.Build(solutionPath);

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine("✓ Analysis completed successfully!");
            System.Console.ResetColor();
            System.Console.WriteLine();
            System.Console.WriteLine($"Methods found: {callGraph.Methods.Count}");
            System.Console.WriteLine($"Method calls: {callGraph.Calls.Count}");

            System.Console.WriteLine($"Connecting to Neo4j at {neo4jUri}...");

            await using var repository = new Neo4jCallGraphRepository(neo4jUri, neo4jUser, neo4jPassword);

            System.Console.WriteLine("Ensuring indexes...");
            await repository.EnsureIndexesAsync();

            System.Console.WriteLine($"Storing call graph ({callGraph.Methods.Count} methods, {callGraph.Calls.Count} calls)...");
            System.Console.WriteLine();

            await repository.StoreCallGraphAsync(callGraph, (phase, current, total) =>
            {
                System.Console.WriteLine($"  {phase}: {current}/{total}");
            });

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Call graph stored in Neo4j successfully!");
            System.Console.WriteLine();
            System.Console.ResetColor();
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            System.Console.ResetColor();
        }
    }

    static async Task AnalyzeSolutionAsync(ICallGraphBuilder callGraphBuilder, ITaskWrapperExtractor taskWrapperExtractor, IAsyncFloodingAnalyzer floodingAnalyzer, string solutionPath, string neo4jUri, string neo4jUser, string neo4jPassword)
    {
        try
        {
            System.Console.WriteLine($"Analyzing solution: {solutionPath}");
            System.Console.WriteLine();


            var callGraph = await callGraphBuilder.Build(solutionPath);

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine("✓ Analysis completed successfully!");
            System.Console.ResetColor();
            System.Console.WriteLine();
            System.Console.WriteLine($"Methods found: {callGraph.Methods.Count}");
            System.Console.WriteLine($"Method calls: {callGraph.Calls.Count}");

            System.Console.WriteLine($"Connecting to Neo4j at {neo4jUri}...");

            await using var repository = new Neo4jCallGraphRepository(neo4jUri, neo4jUser, neo4jPassword);

            System.Console.WriteLine("Ensuring indexes...");
            await repository.EnsureIndexesAsync();

            System.Console.WriteLine($"Storing call graph ({callGraph.Methods.Count} methods, {callGraph.Calls.Count} calls)...");
            System.Console.WriteLine();

            await repository.StoreCallGraphAsync(callGraph, (phase, current, total) =>
            {
                System.Console.WriteLine($"  {phase}: {current}/{total}");
            });

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Call graph stored in Neo4j successfully!");
            System.Console.WriteLine();
            System.Console.ResetColor();

            System.Console.Write("Would you like to detect task wrapper methods (find-sources)? [Y/n] ");
            var response = System.Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(response) || response.Equals("y", StringComparison.OrdinalIgnoreCase) || response.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                PrintTaskWrappers(taskWrapperExtractor, callGraph);

                System.Console.Write("Would you like to check for problematic external interfaces? [Y/n] ");
                var floodResponse = System.Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(floodResponse) || floodResponse.Equals("y", StringComparison.OrdinalIgnoreCase) || floodResponse.Equals("yes", StringComparison.OrdinalIgnoreCase))
                {
                    var wrappers = taskWrapperExtractor.Extract(callGraph);
                    if (wrappers.Count == 0)
                    {
                        System.Console.ForegroundColor = ConsoleColor.Yellow;
                        System.Console.WriteLine("No task wrapper methods found. Cannot run flooding analysis.");
                        System.Console.ResetColor();
                    }
                    else
                    {
                        var rootMethodIds = new HashSet<string>(wrappers.Select(w => w.MethodId));
                        System.Console.WriteLine("Running async flooding analysis...");
                        var asyncGraph = await floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethodIds);
                        PrintProblematicInterfaces(callGraph, asyncGraph);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            System.Console.ResetColor();
        }
    }

    static async Task FindSourcesAsync(ITaskWrapperExtractor extractor, string projectName, string neo4jUri, string neo4jUser, string neo4jPassword)
    {
        try
        {
            System.Console.WriteLine($"Loading call graph for project: {projectName}");

            await using var repository = new Neo4jCallGraphRepository(neo4jUri, neo4jUser, neo4jPassword);
            var callGraph = await repository.GetCallGraphByProjectAsync(projectName);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"No call graph found for project '{projectName}'.");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Call graph loaded: {callGraph.Methods.Count} methods");
            System.Console.WriteLine();

            PrintTaskWrappers(extractor, callGraph);
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            System.Console.ResetColor();
        }
    }

    static async Task FloodAsync(ITaskWrapperExtractor extractor, IAsyncFloodingAnalyzer floodingAnalyzer, string projectName, string neo4jUri, string neo4jUser, string neo4jPassword)
    {
        try
        {
            System.Console.WriteLine($"Loading call graph for project: {projectName}");

            await using var repository = new Neo4jCallGraphRepository(neo4jUri, neo4jUser, neo4jPassword);
            var callGraph = await repository.GetCallGraphByProjectAsync(projectName);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"No call graph found for project '{projectName}'.");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Call graph loaded: {callGraph.Methods.Count} methods, {callGraph.Calls.Count} calls");
            System.Console.WriteLine();

            // Find task wrappers as root methods
            var wrappers = extractor.Extract(callGraph);
            if (wrappers.Count == 0)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("No task wrapper methods found. Nothing to flood.");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Found {wrappers.Count} task wrapper(s) as flooding roots:");
            foreach (var w in wrappers)
            {
                System.Console.WriteLine($"  - {w.Signature}");
            }
            System.Console.WriteLine();

            var rootMethodIds = new HashSet<string>(wrappers.Select(w => w.MethodId));

            // Run flooding analysis
            System.Console.WriteLine("Running async flooding analysis...");
            var asyncGraph = await floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethodIds, (method, current, total) =>
            {
                System.Console.WriteLine($"  Flooding: {method} ({current}/{total})");
            });

            // Count flooded methods (those whose return type changed)
            var floodedCount = 0;
            foreach (var (id, method) in asyncGraph.Methods)
            {
                if (callGraph.Methods.TryGetValue(id, out var original) && original.ReturnType != method.ReturnType)
                    floodedCount++;
            }

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Flooding complete: {floodedCount} methods need async transformation");
            System.Console.ResetColor();
            System.Console.WriteLine();

            // Print flooded methods
            foreach (var (id, method) in asyncGraph.Methods.OrderBy(m => m.Value.ContainingType).ThenBy(m => m.Value.Name))
            {
                if (callGraph.Methods.TryGetValue(id, out var original) && original.ReturnType != method.ReturnType)
                {
                    System.Console.ForegroundColor = ConsoleColor.Cyan;
                    System.Console.Write($"  {method.ContainingType}.{method.Name}");
                    System.Console.ResetColor();
                    System.Console.WriteLine($": {original.ReturnType} → {method.ReturnType}");
                }
            }
            System.Console.WriteLine();

            PrintProblematicInterfaces(callGraph, asyncGraph);

            // Store the async call graph in Neo4j
            System.Console.WriteLine($"Storing async call graph '{asyncGraph.ProjectName}' in Neo4j...");
            await repository.EnsureIndexesAsync();
            await repository.StoreCallGraphAsync(asyncGraph, (phase, current, total) =>
            {
                System.Console.WriteLine($"  {phase}: {current}/{total}");
            });

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Async call graph stored as '{asyncGraph.ProjectName}'");
            System.Console.ResetColor();
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            System.Console.ResetColor();
        }
    }

    static void PrintProblematicInterfaces(AsyncRewriter.Core.Models.CallGraph callGraph, AsyncRewriter.Core.Models.CallGraph asyncGraph)
    {
        var problematicInterfaces = new Dictionary<string, (string InterfaceMethodId, string InterfaceType, string InterfaceMethodName, List<string> ImplementingTypes)>();

        foreach (var impl in callGraph.InterfaceImplementations)
        {
            // Check if the implementing method was flooded (return type changed)
            if (!callGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var originalImpl))
                continue;
            if (!asyncGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var asyncImpl))
                continue;
            if (originalImpl.ReturnType == asyncImpl.ReturnType)
                continue;

            // Check if the interface method is external
            var isExternal = !callGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var interfaceMethod)
                || interfaceMethod.FilePath == "external";

            if (!isExternal)
                continue;

            if (!problematicInterfaces.TryGetValue(impl.InterfaceMethodId, out var entry))
            {
                var ifaceName = interfaceMethod?.ContainingType ?? impl.InterfaceMethodId.Split('.').LastOrDefault() ?? impl.InterfaceMethodId;
                var ifaceMethodName = interfaceMethod?.Name ?? impl.InterfaceMethodId;
                entry = (impl.InterfaceMethodId, ifaceName, ifaceMethodName, new List<string>());
                problematicInterfaces[impl.InterfaceMethodId] = entry;
            }

            entry.ImplementingTypes.Add($"{originalImpl.ContainingType}.{originalImpl.Name}");
        }

        if (problematicInterfaces.Count > 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine($"⚠ {problematicInterfaces.Count} problematic external interface(s) detected:");
            System.Console.ResetColor();
            System.Console.WriteLine("  These interface methods are defined in external code and cannot be modified,");
            System.Console.WriteLine("  but their implementations were flooded to async:");
            System.Console.WriteLine();

            foreach (var (_, entry) in problematicInterfaces.OrderBy(p => p.Value.InterfaceType))
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.Write($"  {entry.InterfaceType}.{entry.InterfaceMethodName}");
                System.Console.ResetColor();
                System.Console.WriteLine();
                foreach (var implType in entry.ImplementingTypes)
                {
                    System.Console.WriteLine($"    implemented by: {implType}");
                }
            }
            System.Console.WriteLine();
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine("✓ No problematic external interfaces detected.");
            System.Console.ResetColor();
            System.Console.WriteLine();
        }
    }

    static void PrintTaskWrappers(ITaskWrapperExtractor extractor, AsyncRewriter.Core.Models.CallGraph callGraph)
    {
        var wrappers = extractor.Extract(callGraph);

        if (wrappers.Count == 0)
        {
            System.Console.WriteLine("No task wrapper methods found.");
            return;
        }

        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($"Found {wrappers.Count} task wrapper method(s):");
        System.Console.ResetColor();
        System.Console.WriteLine();

        foreach (var wrapper in wrappers)
        {
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine($"  {wrapper.Signature}");
            System.Console.ResetColor();
            System.Console.WriteLine($"    Pattern: {wrapper.PatternDescription}");
            System.Console.WriteLine($"    Location: {wrapper.FilePath}:{wrapper.StartLine}");
            System.Console.WriteLine();
        }
    }
}
