using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Transformation;
using AsyncRewriter.Neo4j;
using Microsoft.Build.Locator;

namespace AsyncRewriter.Console;

class Program
{
    private static readonly ICallGraphAnalyzer _analyzer = new CallGraphAnalyzer();
    private static readonly IAsyncFloodingAnalyzer _floodingAnalyzer = new AsyncFloodingAnalyzer();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    static async Task<int> Main(string[] args)
    {
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

        // Analyze command
        var analyzeCommand = new Command("analyze", "Analyze a C# project and generate a call graph");
        var projectPathArgument = new Argument<string>("project-path", "The path to the C# project to analyze");
        var outputOption = new Option<string>(
            aliases: new[] { "--output", "-o" },
            description: "Output file path for the call graph (default: callgraph.json)",
            getDefaultValue: () => "callgraph.json");
        var externalSyncWrapperOption = new Option<string[]>(
            aliases: new[] { "--external-sync-wrapper", "-esw" },
            description: "Fully qualified method IDs to treat as sync wrappers",
            getDefaultValue: Array.Empty<string>);

        analyzeCommand.AddArgument(projectPathArgument);
        analyzeCommand.AddOption(outputOption);
        analyzeCommand.AddOption(externalSyncWrapperOption);

        analyzeCommand.SetHandler(async (projectPath, output, externalSyncWrappers) =>
        {
            await AnalyzeProjectAsync(projectPath, output, externalSyncWrappers);
        }, projectPathArgument, outputOption, externalSyncWrapperOption);

        // Find sync wrappers command
        var findSyncWrappersCommand = new Command("find-sync-wrappers", "Find sync-over-async wrapper methods in a project");
        var syncWrapperProjectPath = new Argument<string>("project-path", "The path to the C# project to analyze");
        var analyzeFromWrappersOption = new Option<bool>(
            aliases: new[] { "--analyze", "-a" },
            description: "Automatically run async flooding analysis from the found sync wrappers",
            getDefaultValue: () => false);
        var applyChangesOption = new Option<bool>(
            aliases: new[] { "--apply", "-y" },
            description: "Automatically apply transformation changes",
            getDefaultValue: () => false);
        var syncWrapperOutputOption = new Option<string>(
            aliases: new[] { "--output", "-o" },
            description: "Output file path for the call graph (default: callgraph.json)",
            getDefaultValue: () => "callgraph.json");
        var interfaceMappingOption = new Option<string[]>(
            aliases: new[] { "--interface-mapping", "-im" },
            description: "Interface mappings in format 'SyncInterface=AsyncInterface'",
            getDefaultValue: Array.Empty<string>);

        findSyncWrappersCommand.AddArgument(syncWrapperProjectPath);
        findSyncWrappersCommand.AddOption(analyzeFromWrappersOption);
        findSyncWrappersCommand.AddOption(applyChangesOption);
        findSyncWrappersCommand.AddOption(syncWrapperOutputOption);
        findSyncWrappersCommand.AddOption(externalSyncWrapperOption);
        findSyncWrappersCommand.AddOption(interfaceMappingOption);

        findSyncWrappersCommand.SetHandler(async (projectPath, analyze, apply, output, externalSyncWrappers, interfaceMappings) =>
        {
            await FindSyncWrappersAsync(projectPath, analyze, apply, output, externalSyncWrappers, interfaceMappings);
        }, syncWrapperProjectPath, analyzeFromWrappersOption, applyChangesOption, syncWrapperOutputOption, externalSyncWrapperOption, interfaceMappingOption);

        // Transform command
        var transformCommand = new Command("transform", "Transform a C# project from sync to async based on a call graph");
        var transformProjectPath = new Argument<string>("project-path", "The path to the C# project to transform");
        var transformCallGraphPath = new Argument<string>("call-graph-path", "The path to the call graph JSON file");
        var transformApplyOption = new Option<bool>(
            aliases: new[] { "--apply", "-y" },
            description: "Apply the changes to the files (default is preview only)",
            getDefaultValue: () => false);

        transformCommand.AddArgument(transformProjectPath);
        transformCommand.AddArgument(transformCallGraphPath);
        transformCommand.AddOption(transformApplyOption);
        transformCommand.AddOption(externalSyncWrapperOption);
        transformCommand.AddOption(interfaceMappingOption);

        transformCommand.SetHandler(async (projectPath, callGraphPath, applyChanges, externalSyncWrappers, interfaceMappings) =>
        {
            await TransformProjectAsync(projectPath, callGraphPath, applyChanges, externalSyncWrappers, interfaceMappings);
        }, transformProjectPath, transformCallGraphPath, transformApplyOption, externalSyncWrapperOption, interfaceMappingOption);

        // Search command
        var searchCommand = new Command("search", "Search for methods in a call graph");
        var searchCallGraphPathArgument = new Argument<string>("call-graph-path", "The path to the call graph JSON file");
        var searchQueryArgument = new Argument<string>("query", "The search query (matches method name, type, or ID)");
        var floodedOnlyOption = new Option<bool>(
            aliases: new[] { "--flooded-only", "-f" },
            description: "Only show methods that require async transformation",
            getDefaultValue: () => false);

        searchCommand.AddArgument(searchCallGraphPathArgument);
        searchCommand.AddArgument(searchQueryArgument);
        searchCommand.AddOption(floodedOnlyOption);

        searchCommand.SetHandler(async (callGraphPath, query, floodedOnly) =>
        {
            await SearchMethodsAsync(callGraphPath, query, floodedOnly);
        }, searchCallGraphPathArgument, searchQueryArgument, floodedOnlyOption);

        // Explain command
        var explainCommand = new Command("explain", "Explain why a method requires async transformation");
        var explainCallGraphPathArgument = new Argument<string>("call-graph-path", "The path to the call graph JSON file");
        var explainMethodIdArgument = new Argument<string>("method-id", "The ID of the method to explain");

        explainCommand.AddArgument(explainCallGraphPathArgument);
        explainCommand.AddArgument(explainMethodIdArgument);

        explainCommand.SetHandler(async (callGraphPath, methodId) =>
        {
            await ExplainMethodAsync(callGraphPath, methodId);
        }, explainCallGraphPathArgument, explainMethodIdArgument);

        // Store-neo4j command
        var storeNeo4jCommand = new Command("store-neo4j", "Store a call graph JSON file into Neo4j for visual inspection");
        var neo4jCallGraphPathArgument = new Argument<string>("call-graph-path", "The path to the call graph JSON file");
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

        storeNeo4jCommand.AddArgument(neo4jCallGraphPathArgument);
        storeNeo4jCommand.AddOption(neo4jUriOption);
        storeNeo4jCommand.AddOption(neo4jUserOption);
        storeNeo4jCommand.AddOption(neo4jPasswordOption);

        storeNeo4jCommand.SetHandler(async (callGraphPath, uri, user, password) =>
        {
            await StoreInNeo4jAsync(callGraphPath, uri, user, password);
        }, neo4jCallGraphPathArgument, neo4jUriOption, neo4jUserOption, neo4jPasswordOption);

        rootCommand.AddCommand(analyzeCommand);
        rootCommand.AddCommand(findSyncWrappersCommand);
        rootCommand.AddCommand(transformCommand);
        rootCommand.AddCommand(searchCommand);
        rootCommand.AddCommand(explainCommand);
        rootCommand.AddCommand(storeNeo4jCommand);

        return await rootCommand.InvokeAsync(args);
    }

    static async Task AnalyzeProjectAsync(string projectPath, string outputPath, string[] externalSyncWrappers)
    {
        try
        {
            System.Console.WriteLine($"Analyzing project: {projectPath}");
            System.Console.WriteLine();

            var progress = new Progress<string>(step =>
            {
                System.Console.WriteLine($"  {step}");
            });

            var callGraph = await _analyzer.AnalyzeProjectAsync(
                projectPath,
                externalSyncWrappers.ToList(),
                progress);

            System.Console.WriteLine();
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine("✓ Analysis completed successfully!");
            System.Console.ResetColor();
            System.Console.WriteLine();
            System.Console.WriteLine($"Methods found: {callGraph.Methods.Count}");
            System.Console.WriteLine($"Method calls: {callGraph.Calls.Count}");

            // Save to JSON file
            var json = JsonSerializer.Serialize(callGraph, _jsonOptions);
            await File.WriteAllTextAsync(outputPath, json);

            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Call graph saved to: {outputPath}");
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

    static async Task FindSyncWrappersAsync(
        string projectPath,
        bool analyzeFromWrappers,
        bool applyChanges,
        string outputPath,
        string[] externalSyncWrappers,
        string[] interfaceMappings)
    {
        try
        {
            System.Console.WriteLine($"Finding sync wrapper methods in project: {projectPath}");
            System.Console.WriteLine();

            var interfaceMappingsDict = ParseInterfaceMappings(interfaceMappings);

            var syncWrappers = await _analyzer.FindSyncWrapperMethodsAsync(projectPath, externalSyncWrappers.ToList());

            if (syncWrappers.Count == 0)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("No sync wrapper methods found in the project.");
                System.Console.ResetColor();
                return;
            }

            PrintSyncWrappers(syncWrappers);

            if (analyzeFromWrappers)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("Running async flooding analysis from sync wrappers...");
                System.Console.WriteLine();

                var progress = new Progress<string>(step =>
                {
                    System.Console.WriteLine($"  {step}");
                });

                var callGraph = await _analyzer.AnalyzeProjectAsync(
                    projectPath,
                    externalSyncWrappers.ToList(),
                    progress);

                // Set sync wrapper methods as root methods
                var rootMethodIds = syncWrappers.Select(sw => sw.MethodId).ToList();
                callGraph.RootAsyncMethods = rootMethodIds;
                callGraph.SyncWrapperMethods = rootMethodIds;

                // Apply interface mappings
                foreach (var mapping in interfaceMappingsDict)
                {
                    callGraph.InterfaceMappings[mapping.Key] = mapping.Value;
                }

                System.Console.WriteLine();
                System.Console.WriteLine("Running flooding analysis...");
                await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethodIds, progress);

                System.Console.WriteLine();
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"✓ Found {callGraph.FloodedMethods.Count} method(s) that require async transformation");
                System.Console.ResetColor();

                // Save call graph
                var json = JsonSerializer.Serialize(callGraph, _jsonOptions);
                await File.WriteAllTextAsync(outputPath, json);

                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"✓ Call graph saved to: {outputPath}");
                System.Console.ResetColor();

                if (applyChanges && callGraph.FloodedMethods.Count > 0)
                {
                    System.Console.WriteLine();
                    System.Console.ForegroundColor = ConsoleColor.Yellow;
                    System.Console.WriteLine($"Applying transformations to {callGraph.FloodedMethods.Count} method(s)...");
                    System.Console.ResetColor();
                    System.Console.WriteLine();

                    await TransformProjectWithCallGraphAsync(projectPath, callGraph, true, externalSyncWrappers, interfaceMappingsDict);
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

    static async Task TransformProjectAsync(
        string projectPath,
        string callGraphPath,
        bool applyChanges,
        string[] externalSyncWrappers,
        string[] interfaceMappings)
    {
        try
        {
            System.Console.WriteLine($"Loading call graph from: {callGraphPath}");

            if (!File.Exists(callGraphPath))
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"Error: Call graph file not found: {callGraphPath}");
                System.Console.ResetColor();
                return;
            }

            var json = await File.ReadAllTextAsync(callGraphPath);
            var callGraph = JsonSerializer.Deserialize<CallGraph>(json);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine("Error: Failed to deserialize call graph");
                System.Console.ResetColor();
                return;
            }

            var interfaceMappingsDict = ParseInterfaceMappings(interfaceMappings);

            await TransformProjectWithCallGraphAsync(projectPath, callGraph, applyChanges, externalSyncWrappers, interfaceMappingsDict);
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            System.Console.ResetColor();
        }
    }

    static async Task TransformProjectWithCallGraphAsync(
        string projectPath,
        CallGraph callGraph,
        bool applyChanges,
        string[] externalSyncWrappers,
        Dictionary<string, string> interfaceMappings)
    {
        try
        {
            System.Console.WriteLine($"Transforming project: {projectPath}");
            System.Console.WriteLine($"Apply Changes: {applyChanges}");
            System.Console.WriteLine();

            // Apply interface mappings
            foreach (var mapping in interfaceMappings)
            {
                callGraph.InterfaceMappings[mapping.Key] = mapping.Value;
            }

            var transformer = new AsyncTransformer(externalSyncWrappers.ToList());

            var progress = new Progress<string>(step =>
            {
                System.Console.WriteLine($"  {step}");
            });

            var result = await transformer.TransformProjectAsync(projectPath, callGraph, applyChanges, progress);

            System.Console.WriteLine();
            PrintTransformationResult(result, applyChanges);
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error during transformation: {ex.Message}");
            System.Console.WriteLine(ex.StackTrace);
            System.Console.ResetColor();
        }
    }

    static async Task SearchMethodsAsync(string callGraphPath, string query, bool floodedOnly)
    {
        try
        {
            if (!File.Exists(callGraphPath))
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"Error: Call graph file not found: {callGraphPath}");
                System.Console.ResetColor();
                return;
            }

            var json = await File.ReadAllTextAsync(callGraphPath);
            var callGraph = JsonSerializer.Deserialize<CallGraph>(json);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine("Error: Failed to deserialize call graph");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Searching for '{query}' in call graph...");
            System.Console.WriteLine();

            var results = callGraph.Methods.Values
                .Where(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                           m.ContainingType.Contains(query, StringComparison.OrdinalIgnoreCase))
                .Where(m => !floodedOnly || m.RequiresAsyncTransformation)
                .ToList();

            if (results.Count == 0)
            {
                System.Console.WriteLine("No methods found matching the query.");
                return;
            }

            System.Console.WriteLine($"Found {results.Count} method(s):");
            System.Console.WriteLine();

            foreach (var method in results)
            {
                var asyncMarker = method.RequiresAsyncTransformation ? "[NEEDS ASYNC]" :
                                  method.IsAsync ? "[ASYNC]" :
                                  method.IsSyncWrapper ? "[SYNC WRAPPER]" : "";

                if (!string.IsNullOrEmpty(asyncMarker))
                {
                    System.Console.ForegroundColor = method.RequiresAsyncTransformation ? ConsoleColor.Yellow :
                                              method.IsSyncWrapper ? ConsoleColor.Magenta : ConsoleColor.Green;
                    System.Console.Write($"  {asyncMarker} ");
                    System.Console.ResetColor();
                }
                else
                {
                    System.Console.Write("  ");
                }

                System.Console.WriteLine($"{method.ContainingType}.{method.Name}");
                System.Console.ForegroundColor = ConsoleColor.DarkGray;
                System.Console.WriteLine($"    ID: {method.Id}");
                System.Console.WriteLine($"    {method.FilePath}:{method.StartLine}");
                System.Console.ResetColor();
                System.Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error searching methods: {ex.Message}");
            System.Console.ResetColor();
        }
    }

    static async Task ExplainMethodAsync(string callGraphPath, string methodId)
    {
        try
        {
            if (!File.Exists(callGraphPath))
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"Error: Call graph file not found: {callGraphPath}");
                System.Console.ResetColor();
                return;
            }

            var json = await File.ReadAllTextAsync(callGraphPath);
            var callGraph = JsonSerializer.Deserialize<CallGraph>(json);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine("Error: Failed to deserialize call graph");
                System.Console.ResetColor();
                return;
            }

            if (!callGraph.Methods.TryGetValue(methodId, out var method))
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"Error: Method not found: {methodId}");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine("Method Information:");
            System.Console.WriteLine($"  Name: {method.ContainingType}.{method.Name}");
            System.Console.WriteLine($"  ID: {method.Id}");
            System.Console.WriteLine($"  Location: {method.FilePath}:{method.StartLine}");
            System.Console.WriteLine();

            if (method.RequiresAsyncTransformation)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("Status: REQUIRES ASYNC TRANSFORMATION");
                System.Console.ResetColor();
                System.Console.WriteLine();

                // Find call chain to root
                var callChain = FindCallChainToRoot(callGraph, methodId);
                if (callChain.Count > 0)
                {
                    System.Console.WriteLine("Call Chain:");
                    for (int i = 0; i < callChain.Count; i++)
                    {
                        var step = callChain[i];
                        var indent = new string(' ', i * 2);

                        if (i == 0)
                        {
                            System.Console.ForegroundColor = ConsoleColor.Cyan;
                            System.Console.Write($"{indent}[THIS METHOD] ");
                            System.Console.ResetColor();
                        }
                        else
                        {
                            System.Console.Write($"{indent}");
                        }

                        System.Console.WriteLine($"{step.ContainingType}.{step.Name}");
                        System.Console.ForegroundColor = ConsoleColor.DarkGray;
                        System.Console.WriteLine($"{indent}  {step.FilePath}:{step.StartLine}");
                        System.Console.ResetColor();

                        if (i < callChain.Count - 1)
                        {
                            System.Console.ForegroundColor = ConsoleColor.DarkYellow;
                            System.Console.WriteLine($"{indent}  └── calls ──▶");
                            System.Console.ResetColor();
                        }
                    }
                }
            }
            else
            {
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine("Status: Does not require async transformation");
                System.Console.ResetColor();
            }
        }
        catch (Exception ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine($"Error explaining method: {ex.Message}");
            System.Console.ResetColor();
        }
    }

    static async Task StoreInNeo4jAsync(string callGraphPath, string uri, string user, string password)
    {
        try
        {
            if (!File.Exists(callGraphPath))
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"Error: Call graph file not found: {callGraphPath}");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Loading call graph from: {callGraphPath}");

            var json = await File.ReadAllTextAsync(callGraphPath);
            var callGraph = JsonSerializer.Deserialize<CallGraph>(json);

            if (callGraph == null)
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine("Error: Failed to deserialize call graph");
                System.Console.ResetColor();
                return;
            }

            System.Console.WriteLine($"Connecting to Neo4j at {uri}...");

            await using var repository = new CallGraphRepository(uri, user, password);

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
            System.Console.WriteLine("Open the Neo4j Browser to visualize:");
            System.Console.WriteLine("  http://localhost:7474");
            System.Console.WriteLine();
            System.Console.WriteLine("Example Cypher queries:");
            System.Console.WriteLine("  // Show all methods and calls");
            System.Console.WriteLine("  MATCH (m:Method)-[r:CALLS]->(n:Method) RETURN m, r, n LIMIT 100");
            System.Console.WriteLine();
            System.Console.WriteLine("  // Methods requiring async transformation");
            System.Console.WriteLine("  MATCH (m:Method {requiresAsyncTransformation: true}) RETURN m");
            System.Console.WriteLine();
            System.Console.WriteLine("  // Call chain from a specific method");
            System.Console.WriteLine("  MATCH path = (m:Method)-[:CALLS*]->(n:Method)");
            System.Console.WriteLine("  WHERE m.name = 'YourMethod' RETURN path LIMIT 25");
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

    static List<MethodNode> FindCallChainToRoot(CallGraph callGraph, string methodId)
    {
        var chain = new List<MethodNode>();
        var visited = new HashSet<string>();
        var current = methodId;

        while (current != null && visited.Add(current))
        {
            if (callGraph.Methods.TryGetValue(current, out var method))
            {
                chain.Add(method);

                if (callGraph.RootAsyncMethods.Contains(current) || callGraph.SyncWrapperMethods.Contains(current))
                {
                    break;
                }

                // Find a call where current is the caller and the callee requires async
                var nextCall = callGraph.Calls.FirstOrDefault(c =>
                    c.CallerId == current &&
                    callGraph.Methods.TryGetValue(c.CalleeId, out var callee) &&
                    (callee.RequiresAsyncTransformation || callee.IsAsync));

                current = nextCall?.CalleeId;
            }
            else
            {
                break;
            }
        }

        return chain;
    }

    static Dictionary<string, string> ParseInterfaceMappings(string[] mappings)
    {
        var result = new Dictionary<string, string>();
        foreach (var mapping in mappings)
        {
            var parts = mapping.Split('=', 2);
            if (parts.Length == 2)
            {
                result[parts[0].Trim()] = parts[1].Trim();
            }
            else
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine($"Warning: Invalid interface mapping format '{mapping}'. Expected 'SyncInterface=AsyncInterface'");
                System.Console.ResetColor();
            }
        }
        return result;
    }

    static void PrintSyncWrappers(List<SyncWrapperMethod> syncWrappers)
    {
        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($"Found {syncWrappers.Count} sync wrapper method(s):");
        System.Console.ResetColor();
        System.Console.WriteLine();

        foreach (var wrapper in syncWrappers)
        {
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine($"  {wrapper.ContainingType}.{wrapper.Signature}");
            System.Console.ResetColor();
            System.Console.WriteLine($"    File: {wrapper.FilePath}:{wrapper.StartLine}");
            System.Console.WriteLine($"    Return Type: {wrapper.ReturnType}");
            System.Console.WriteLine($"    Pattern: {wrapper.PatternDescription}");
            System.Console.WriteLine();
        }
    }

    static void PrintTransformationResult(TransformationResult result, bool applied)
    {
        if (result.Success)
        {
            System.Console.ForegroundColor = ConsoleColor.Green;
            System.Console.WriteLine($"✓ Transformation {(applied ? "applied" : "preview generated")} successfully!");
            System.Console.ResetColor();
            System.Console.WriteLine();
            System.Console.WriteLine($"Files modified: {result.ModifiedFiles.Count}");
            System.Console.WriteLine();

            foreach (var file in result.ModifiedFiles)
            {
                System.Console.ForegroundColor = ConsoleColor.Cyan;
                System.Console.WriteLine($"  {file.FilePath}");
                System.Console.ResetColor();
                System.Console.WriteLine($"    Methods transformed: {file.TransformedMethods.Count}");
                System.Console.WriteLine($"    Await keywords added: {file.AwaitLocations.Count}");

                if (file.TransformedMethods.Count > 0)
                {
                    System.Console.WriteLine("    Transformed methods:");
                    foreach (var method in file.TransformedMethods)
                    {
                        System.Console.WriteLine($"      - {method}");
                    }
                }
                System.Console.WriteLine();
            }

            if (!applied)
            {
                System.Console.ForegroundColor = ConsoleColor.Yellow;
                System.Console.WriteLine("Note: Changes have not been applied to the files.");
                System.Console.WriteLine("To apply the changes, use the --apply flag.");
                System.Console.ResetColor();
            }
            else
            {
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"✓ Changes have been written to {result.ModifiedFiles.Count} file(s).");
                System.Console.ResetColor();
            }
        }
        else
        {
            System.Console.ForegroundColor = ConsoleColor.Red;
            System.Console.WriteLine("✗ Transformation failed");
            System.Console.ResetColor();

            if (result.Errors.Count > 0)
            {
                System.Console.WriteLine();
                System.Console.WriteLine("Errors:");
                foreach (var error in result.Errors)
                {
                    System.Console.WriteLine($"  - {error}");
                }
            }
        }
    }
}
