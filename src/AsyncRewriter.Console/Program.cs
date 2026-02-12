using System.CommandLine;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Neo4j;
using AsyncRewriter.Transformation;
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
                services.GetRequiredService<IAsyncTransformer>(),
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

        // Transform command - apply async transformations to source files
        var transformCommand = new Command("transform", "Apply async transformations to source files based on flooded call graph");
        var transformProjectNameArgument = new Argument<string>("project-name", "The project name of the flooded (async) call graph");
        var transformNeo4jUriOption = new Option<string>(
            aliases: new[] { "--uri", "-u" },
            description: "Neo4j Bolt URI",
            getDefaultValue: () => "bolt://localhost:7687");
        var transformNeo4jUserOption = new Option<string>(
            aliases: new[] { "--neo4j-user" },
            description: "Neo4j username",
            getDefaultValue: () => "neo4j");
        var transformNeo4jPasswordOption = new Option<string>(
            aliases: new[] { "--neo4j-password" },
            description: "Neo4j password",
            getDefaultValue: () => "asyncrewriter");
        var dryRunOption = new Option<bool>(
            aliases: new[] { "--dry-run", "-n" },
            description: "Preview changes without writing to disk",
            getDefaultValue: () => false);

        transformCommand.AddArgument(transformProjectNameArgument);
        transformCommand.AddOption(transformNeo4jUriOption);
        transformCommand.AddOption(transformNeo4jUserOption);
        transformCommand.AddOption(transformNeo4jPasswordOption);
        transformCommand.AddOption(dryRunOption);

        transformCommand.SetHandler(async (projectName, neo4jUri, neo4jUser, neo4jPassword, dryRun) =>
        {
            await TransformAsync(
                services.GetRequiredService<IAsyncTransformer>(),
                projectName, neo4jUri, neo4jUser, neo4jPassword, dryRun);
        }, transformProjectNameArgument, transformNeo4jUriOption, transformNeo4jUserOption, transformNeo4jPasswordOption, dryRunOption);

        rootCommand.AddCommand(transformCommand);

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

    static async Task AnalyzeSolutionAsync(ICallGraphBuilder callGraphBuilder, ITaskWrapperExtractor taskWrapperExtractor, IAsyncFloodingAnalyzer floodingAnalyzer, IAsyncTransformer transformer, string solutionPath, string neo4jUri, string neo4jUser, string neo4jPassword)
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
                        PrintFloodingStatistics(callGraph, asyncGraph);
                        var interfaceMappings = await ResolveProblematicInterfacesAsync(callGraph, asyncGraph);

                        System.Console.Write("Would you like to transform the source files? [Y/n] ");
                        var transformResponse = System.Console.ReadLine()?.Trim();
                        if (string.IsNullOrEmpty(transformResponse) || transformResponse.Equals("y", StringComparison.OrdinalIgnoreCase) || transformResponse.Equals("yes", StringComparison.OrdinalIgnoreCase))
                        {
                            System.Console.Write("Dry run (preview only, no files written)? [Y/n] ");
                            var dryRunResponse = System.Console.ReadLine()?.Trim();
                            var dryRun = string.IsNullOrEmpty(dryRunResponse) || dryRunResponse.Equals("y", StringComparison.OrdinalIgnoreCase) || dryRunResponse.Equals("yes", StringComparison.OrdinalIgnoreCase);

                            if (dryRun)
                            {
                                System.Console.ForegroundColor = ConsoleColor.Yellow;
                                System.Console.WriteLine("DRY RUN - no files will be modified");
                                System.Console.ResetColor();
                                System.Console.WriteLine();
                            }

                            System.Console.WriteLine("Transforming source files...");
                            var transformResult = await transformer.TransformProjectAsync(".", asyncGraph, (file, current, total) =>
                            {
                                System.Console.WriteLine($"  [{current}/{total}] {file}");
                            });

                            if (!transformResult.Success)
                            {
                                System.Console.ForegroundColor = ConsoleColor.Red;
                                System.Console.WriteLine("Transformation failed:");
                                foreach (var error in transformResult.Errors)
                                    System.Console.WriteLine($"  {error}");
                                System.Console.ResetColor();
                            }
                            else
                            {
                                foreach (var warning in transformResult.Warnings)
                                {
                                    System.Console.ForegroundColor = ConsoleColor.Yellow;
                                    System.Console.WriteLine($"  Warning: {warning}");
                                    System.Console.ResetColor();
                                }

                                System.Console.WriteLine();
                                System.Console.ForegroundColor = ConsoleColor.Green;
                                System.Console.WriteLine($"✓ Transformation complete:");
                                System.Console.ResetColor();
                                System.Console.WriteLine($"  Files modified:      {transformResult.ModifiedFiles.Count}");
                                System.Console.WriteLine($"  Methods transformed: {transformResult.TotalMethodsTransformed}");
                                System.Console.WriteLine($"  Call sites awaited:  {transformResult.TotalCallSitesTransformed}");
                                System.Console.WriteLine();

                                if (!dryRun)
                                {
                                    foreach (var file in transformResult.ModifiedFiles)
                                    {
                                        await File.WriteAllTextAsync(file.FilePath, file.TransformedContent);
                                        System.Console.WriteLine($"  Written: {file.FilePath}");
                                    }
                                }
                                else
                                {
                                    foreach (var file in transformResult.ModifiedFiles)
                                    {
                                        System.Console.ForegroundColor = ConsoleColor.Cyan;
                                        System.Console.WriteLine($"  Would modify: {file.FilePath}");
                                        System.Console.ResetColor();
                                        foreach (var method in file.MethodTransformations)
                                        {
                                            System.Console.WriteLine($"    {method.MethodSignature}: {method.OriginalReturnType} → {method.NewReturnType}");
                                            if (method.AwaitAddedAtLines.Count > 0)
                                                System.Console.WriteLine($"      await added at lines: {string.Join(", ", method.AwaitAddedAtLines)}");
                                        }
                                    }
                                }
                            }
                        }
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

            System.Console.WriteLine();
            PrintFloodingStatistics(callGraph, asyncGraph);

            var interfaceMappings = await ResolveProblematicInterfacesAsync(callGraph, asyncGraph);

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

    static async Task TransformAsync(IAsyncTransformer transformer, string projectName, string neo4jUri, string neo4jUser, string neo4jPassword, bool dryRun)
    {
        try
        {
            System.Console.WriteLine($"Loading flooded call graph for project: {projectName}");

            await using var repository = new Neo4jCallGraphRepository(neo4jUri, neo4jUser, neo4jPassword);
            var callGraph = await repository.GetCallGraphByProjectAsync(projectName);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"No call graph found for project '{projectName}'.");
                System.Console.WriteLine("Run 'flood' first to create a flooded call graph.");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Call graph loaded: {callGraph.Methods.Count} methods, {callGraph.Calls.Count} calls");
            System.Console.WriteLine();

            if (dryRun)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("DRY RUN - no files will be modified");
                System.Console.ResetColor();
                System.Console.WriteLine();
            }

            System.Console.WriteLine("Transforming source files...");
            var result = await transformer.TransformProjectAsync(".", callGraph, (file, current, total) =>
            {
                System.Console.WriteLine($"  [{current}/{total}] {file}");
            });

            if (!result.Success)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine("Transformation failed:");
                foreach (var error in result.Errors)
                    System.Console.WriteLine($"  {error}");
                System.Console.ResetColor();
                return;
            }

            foreach (var warning in result.Warnings)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine($"  Warning: {warning}");
                System.Console.ResetColor();
            }

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Transformation complete:");
            System.Console.ResetColor();
            System.Console.WriteLine($"  Files modified:      {result.ModifiedFiles.Count}");
            System.Console.WriteLine($"  Methods transformed: {result.TotalMethodsTransformed}");
            System.Console.WriteLine($"  Call sites awaited:  {result.TotalCallSitesTransformed}");
            System.Console.WriteLine();

            if (!dryRun)
            {
                foreach (var file in result.ModifiedFiles)
                {
                    await File.WriteAllTextAsync(file.FilePath, file.TransformedContent);
                    System.Console.WriteLine($"  Written: {file.FilePath}");
                }
            }
            else
            {
                foreach (var file in result.ModifiedFiles)
                {
                    System.Console.ForegroundColor = ConsoleColor.Cyan;
                    System.Console.WriteLine($"  Would modify: {file.FilePath}");
                    System.Console.ResetColor();
                    foreach (var method in file.MethodTransformations)
                    {
                        System.Console.WriteLine($"    {method.MethodSignature}: {method.OriginalReturnType} → {method.NewReturnType}");
                        if (method.AwaitAddedAtLines.Count > 0)
                            System.Console.WriteLine($"      await added at lines: {string.Join(", ", method.AwaitAddedAtLines)}");
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

    static void PrintFloodingStatistics(AsyncRewriter.Core.Models.CallGraph callGraph, AsyncRewriter.Core.Models.CallGraph asyncGraph)
    {
        var floodedMethods = new List<(string Id, AsyncRewriter.Core.Models.MethodNode Original, AsyncRewriter.Core.Models.MethodNode Flooded)>();
        foreach (var (id, method) in asyncGraph.Methods)
        {
            if (callGraph.Methods.TryGetValue(id, out var original) && original.ReturnType != method.ReturnType)
                floodedMethods.Add((id, original, method));
        }

        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($"✓ Flooding complete: {floodedMethods.Count} methods need async transformation");
        System.Console.ResetColor();
        System.Console.WriteLine();

        var affectedTypes = floodedMethods.Select(m => m.Original.ContainingType).Distinct().Count();
        var affectedFiles = floodedMethods.Where(m => m.Original.FilePath != "external").Select(m => m.Original.FilePath).Distinct().Count();
        var externalFlooded = floodedMethods.Count(m => m.Original.FilePath == "external");
        var internalFlooded = floodedMethods.Count - externalFlooded;

        var byReturnTypeChange = floodedMethods
            .GroupBy(m => $"{m.Original.ReturnType} → {m.Flooded.ReturnType}")
            .OrderByDescending(g => g.Count())
            .ToList();

        System.Console.WriteLine("Statistics:");
        System.Console.WriteLine($"  Methods flooded:  {floodedMethods.Count} ({internalFlooded} internal, {externalFlooded} external)");
        System.Console.WriteLine($"  Types affected:   {affectedTypes}");
        System.Console.WriteLine($"  Files affected:   {affectedFiles}");
        System.Console.WriteLine();
        System.Console.WriteLine("  Return type changes:");
        foreach (var group in byReturnTypeChange)
        {
            System.Console.WriteLine($"    {group.Key}: {group.Count()}");
        }
        System.Console.WriteLine();
    }

    static async Task<List<InterfaceMapping>> ResolveProblematicInterfacesAsync(CallGraph callGraph, CallGraph asyncGraph)
    {
        var mappings = new List<InterfaceMapping>();

        // Collect problematic methods grouped by interface type
        var byInterfaceType = new Dictionary<string, List<(string InterfaceMethodId, MethodNode? InterfaceMethod, MethodNode OriginalImpl, MethodNode AsyncImpl)>>();

        foreach (var impl in callGraph.InterfaceImplementations)
        {
            if (!callGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var originalImpl))
                continue;
            if (!asyncGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var asyncImpl))
                continue;
            if (originalImpl.ReturnType == asyncImpl.ReturnType)
                continue;

            var isExternal = !callGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var interfaceMethod)
                || interfaceMethod.FilePath == "external";
            if (!isExternal)
                continue;

            // Skip interfaces where the return type is a generic type parameter (e.g., IMapper<TSource, TDestination>)
            // since the type parameter can be instantiated with Task<T>
            if (interfaceMethod?.IsReturnTypeParameter == true)
                continue;

            var interfaceType = interfaceMethod?.ContainingType
                ?? impl.InterfaceMethodId.Split('.').LastOrDefault()
                ?? impl.InterfaceMethodId;

            if (!byInterfaceType.TryGetValue(interfaceType, out var list))
            {
                list = new();
                byInterfaceType[interfaceType] = list;
            }

            // Avoid duplicates for the same interface method
            if (!list.Any(e => e.InterfaceMethodId == impl.InterfaceMethodId))
                list.Add((impl.InterfaceMethodId, interfaceMethod, originalImpl, asyncImpl));
        }

        if (byInterfaceType.Count == 0)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine("✓ No problematic external interfaces detected.");
            System.Console.ResetColor();
            System.Console.WriteLine();
            return mappings;
        }

        System.Console.ForegroundColor = ConsoleColor.Yellow;
        System.Console.WriteLine($"⚠ {byInterfaceType.Count} problematic external interface(s) detected:");
        System.Console.ResetColor();
        System.Console.WriteLine();

        foreach (var (interfaceType, methods) in byInterfaceType.OrderBy(kv => kv.Key))
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine($"⚠ Problematic interface: {interfaceType} ({methods.Count} method(s) flooded)");
            System.Console.ResetColor();

            foreach (var m in methods)
            {
                var origRet = m.InterfaceMethod?.ReturnType ?? m.OriginalImpl.ReturnType;
                var asyncRet = m.AsyncImpl.ReturnType;
                var name = m.InterfaceMethod?.Name ?? m.OriginalImpl.Name;
                System.Console.WriteLine($"    {origRet} {name}() → {asyncRet}");
            }
            System.Console.WriteLine();

            // Check for existing async interface
            var existingAsync = FindExistingAsyncInterface(callGraph, interfaceType, methods);

            var optionNum = 1;
            if (existingAsync != null)
            {
                System.Console.WriteLine($"  [{optionNum}] Use existing: {existingAsync} (found in codebase)");
                optionNum++;
            }
            System.Console.WriteLine($"  [{optionNum}] Create new async interface");
            var createOption = optionNum;
            optionNum++;
            System.Console.WriteLine($"  [{optionNum}] Ignore");
            var ignoreOption = optionNum;

            System.Console.Write("  Choice: ");
            var choice = System.Console.ReadLine()?.Trim();

            if (!int.TryParse(choice, out var choiceNum))
                choiceNum = ignoreOption;

            if (existingAsync != null && choiceNum == 1)
            {
                // Use existing async interface
                var ns = GetNamespaceFromCallGraph(callGraph, existingAsync);
                mappings.Add(new InterfaceMapping
                {
                    SyncInterfaceName = interfaceType,
                    AsyncInterfaceName = existingAsync,
                    RequiredNamespaces = ns != null ? new List<string> { ns } : new List<string>()
                });
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"  ✓ Will replace {interfaceType} → {existingAsync}");
                System.Console.ResetColor();
            }
            else if (choiceNum == createOption)
            {
                // Create new async interface
                var asyncName = interfaceType + "Async";
                var defaultPath = $"src/{asyncName}.cs";

                System.Console.Write($"  File path [{defaultPath}]: ");
                var filePath = System.Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(filePath))
                    filePath = defaultPath;

                System.Console.Write("  Namespace [AsyncInterfaces]: ");
                var ns = System.Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(ns))
                    ns = "AsyncInterfaces";

                var source = GenerateAsyncInterface(asyncName, ns, methods);
                await File.WriteAllTextAsync(filePath, source);

                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"  ✓ Created {filePath}");
                System.Console.ResetColor();

                mappings.Add(new InterfaceMapping
                {
                    SyncInterfaceName = interfaceType,
                    AsyncInterfaceName = asyncName,
                    RequiredNamespaces = new List<string> { ns }
                });
            }
            else
            {
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.WriteLine("  Ignored.");
                System.Console.ResetColor();
            }

            System.Console.WriteLine();
        }

        // Apply interface replacements to flooded files
        if (mappings.Count > 0)
        {
            await ApplyInterfaceReplacements(callGraph, asyncGraph, mappings);
        }

        return mappings;
    }

    static string? FindExistingAsyncInterface(CallGraph callGraph, string syncInterfaceType, List<(string InterfaceMethodId, MethodNode? InterfaceMethod, MethodNode OriginalImpl, MethodNode AsyncImpl)> methods)
    {
        // Build candidate names from both simple and qualified forms
        // syncInterfaceType could be "IFoo" or "Some.Namespace.IFoo"
        // Strip generic type parameters for name matching, re-append after generating candidates
        // e.g., "IMapInto<TDestination, TSource>" → base="IMapInto", genericSuffix="<TDestination, TSource>"
        var genericSuffix = "";
        var baseType = syncInterfaceType;
        var angleBracketIndex = syncInterfaceType.IndexOf('<');
        if (angleBracketIndex >= 0)
        {
            genericSuffix = syncInterfaceType.Substring(angleBracketIndex);
            baseType = syncInterfaceType.Substring(0, angleBracketIndex);
        }

        var simpleName = baseType.Contains('.')
            ? baseType.Substring(baseType.LastIndexOf('.') + 1)
            : baseType;
        var prefix = baseType.Contains('.')
            ? baseType.Substring(0, baseType.LastIndexOf('.') + 1)
            : "";

        var candidateSimpleNames = new HashSet<string>(StringComparer.Ordinal);
        if (simpleName.StartsWith("I"))
            candidateSimpleNames.Add("IAsync" + simpleName.Substring(1));
        candidateSimpleNames.Add(simpleName + "Async");

        // Also generate qualified candidates
        var candidateNames = new HashSet<string>(candidateSimpleNames, StringComparer.Ordinal);
        if (prefix.Length > 0)
        {
            foreach (var c in candidateSimpleNames)
                candidateNames.Add(prefix + c);
        }

        var methodsByType = callGraph.Methods.Values
            .GroupBy(m => m.ContainingType)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Search by exact match first, then by simple name match
        foreach (var (typeName, candidateMethods) in methodsByType)
        {
            // Strip generic parameters from the candidate type for comparison
            var typeNameBase = typeName;
            var typeAngle = typeName.IndexOf('<');
            if (typeAngle >= 0)
                typeNameBase = typeName.Substring(0, typeAngle);

            var typeSimpleName = typeNameBase.Contains('.')
                ? typeNameBase.Substring(typeNameBase.LastIndexOf('.') + 1)
                : typeNameBase;

            if (!candidateNames.Contains(typeNameBase) && !candidateSimpleNames.Contains(typeSimpleName))
                continue;

            // Check that all flooded methods have matching async signatures
            var allMatch = true;
            foreach (var m in methods)
            {
                var name = m.InterfaceMethod?.Name ?? m.OriginalImpl.Name;
                var expectedReturnType = m.AsyncImpl.ReturnType;

                // Also try matching with Async suffix on method name
                // Compare return types flexibly (Task<T> vs System.Threading.Tasks.Task<T>)
                var match = candidateMethods.Any(cm =>
                    (cm.Name == name || cm.Name == name + "Async")
                    && NormalizeReturnType(cm.ReturnType) == NormalizeReturnType(expectedReturnType));

                if (!match)
                {
                    allMatch = false;
                    break;
                }
            }

            if (allMatch)
                return typeName;
        }

        return null;
    }

    static string NormalizeReturnType(string returnType)
    {
        return returnType
            .Replace("System.Threading.Tasks.Task", "Task")
            .Replace("System.Threading.Tasks.ValueTask", "ValueTask");
    }

    static string GenerateAsyncInterface(string asyncInterfaceName, string ns, List<(string InterfaceMethodId, MethodNode? InterfaceMethod, MethodNode OriginalImpl, MethodNode AsyncImpl)> methods)
    {
        var lines = new List<string>
        {
            "using System.Threading.Tasks;",
            "",
            $"namespace {ns};",
            "",
            $"public interface {asyncInterfaceName}",
            "{"
        };

        foreach (var m in methods)
        {
            var name = m.InterfaceMethod?.Name ?? m.OriginalImpl.Name;
            var returnType = m.AsyncImpl.ReturnType;
            var parameters = m.InterfaceMethod?.Parameters ?? m.OriginalImpl.Parameters;
            var paramStr = string.Join(", ", parameters);
            lines.Add($"    {returnType} {name}({paramStr});");
        }

        lines.Add("}");
        lines.Add("");

        return string.Join(Environment.NewLine, lines);
    }

    static string? GetNamespaceFromCallGraph(CallGraph callGraph, string typeName)
    {
        var method = callGraph.Methods.Values.FirstOrDefault(m => m.ContainingType == typeName);
        return method?.ContainingNamespace;
    }

    static async Task ApplyInterfaceReplacements(CallGraph callGraph, CallGraph asyncGraph, List<InterfaceMapping> mappings)
    {
        // Find files that contain implementations of the sync interfaces
        var syncTypeNames = new HashSet<string>(mappings.Select(m => m.SyncInterfaceName));
        var filesToProcess = new HashSet<string>();

        foreach (var impl in callGraph.InterfaceImplementations)
        {
            if (!callGraph.Methods.TryGetValue(impl.ImplementingMethodId, out var implMethod))
                continue;
            if (!callGraph.Methods.TryGetValue(impl.InterfaceMethodId, out var ifaceMethod))
                continue;
            if (!syncTypeNames.Contains(ifaceMethod.ContainingType))
                continue;
            if (!string.IsNullOrEmpty(implMethod.FilePath) && implMethod.FilePath != "external")
                filesToProcess.Add(implMethod.FilePath);
        }

        if (filesToProcess.Count == 0)
            return;

        System.Console.WriteLine($"Replacing interface references in {filesToProcess.Count} file(s)...");

        foreach (var filePath in filesToProcess.OrderBy(f => f))
        {
            if (!File.Exists(filePath))
                continue;

            var source = await File.ReadAllTextAsync(filePath);
            var transformed = InterfaceReplacer.Transform(source, mappings);

            if (transformed != null)
            {
                await File.WriteAllTextAsync(filePath, transformed);
                System.Console.WriteLine($"  Updated: {filePath}");
            }
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
