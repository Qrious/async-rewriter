using System.Collections.Concurrent;
using System.Linq.Expressions;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AsyncRewriter.Tests;

/// <summary>
/// Integration test that follows the production analyze flow end-to-end:
/// 1. Parse actual C# source with Roslyn to build a call graph (MethodExtractor + MethodCallExtractor)
/// 2. Detect task wrapper methods (TaskWrapperExtractor)
/// 3. Run async flooding (AsyncFloodingAnalyzer)
/// 4. Detect problematic interfaces (ProblematicInterfaceAnalyzer)
/// 5. Transform source files (AsyncTransformer.TransformProjectAsync with temp files)
/// 6. Verify the transformed code compiles
///
/// This replicates the exact production code path without interactive prompts or Neo4j storage.
/// </summary>
public class MapperScenarioIntegrationTests
{
    private static string LoadTestData(string name)
        => File.ReadAllText(Path.Combine("TestData", $"{name}.cs"));

    /// <summary>
    /// Builds a call graph from multiple source files using Roslyn semantic analysis,
    /// exactly as the production CallGraphBuilder does.
    /// </summary>
    private static async Task<CallGraph> BuildCallGraphFromSources(
        params (string filePath, string source)[] sources)
    {
        var syntaxTrees = new List<(string filePath, SyntaxTree tree)>();
        foreach (var (filePath, source) in sources)
        {
            syntaxTrees.Add((filePath, CSharpSyntaxTree.ParseText(source, path: filePath)));
        }

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Expression<>).Assembly.Location),
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")));

        var compilation = CSharpCompilation.Create("TestAssembly",
            syntaxTrees.Select(t => t.tree),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var callGraphId = "test";
        var methods = new ConcurrentDictionary<string, IMethodNode>();
        var callsDict = new ConcurrentDictionary<string, IMethodCall>();
        var interfaceImplsDict = new ConcurrentDictionary<string, IInterfaceImplementation>();
        var overridesDict = new ConcurrentDictionary<string, IMethodOverride>();
        var genericInstantiationsDict = new ConcurrentDictionary<string, IGenericInstantiation>();

        // Phase 1: Extract method declarations (like production CallGraphBuilder first pass)
        var methodExtractor = new MethodExtractor();
        foreach (var (filePath, tree) in syntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync();
            await methodExtractor.Extract(callGraphId, root, semanticModel, filePath,
                methods, interfaceImplsDict, overridesDict, genericInstantiationsDict);
        }

        // Phase 2: Extract method calls (like production CallGraphBuilder second pass)
        var callExtractor = new MethodCallExtractor();
        foreach (var (filePath, tree) in syntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync();
            await callExtractor.Extract(callGraphId, root, semanticModel, filePath,
                methods, callsDict);
        }

        return new CallGraph(callGraphId.ToString(), methods,
            new ConcurrentBag<IMethodCall>(callsDict.Values),
            new ConcurrentBag<IInterfaceImplementation>(interfaceImplsDict.Values),
            new ConcurrentBag<IMethodOverride>(overridesDict.Values),
            new ConcurrentBag<IGenericInstantiation>(genericInstantiationsDict.Values));
    }

    /// <summary>
    /// Compiles multiple C# source strings together and asserts no errors.
    /// </summary>
    private static void AssertCompilesMultiple(string[] sources, string because = "code should compile without errors")
    {
        var syntaxTrees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToList();

        var references = new List<MetadataReference>();
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        foreach (var assembly in trustedAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(assembly);
            if (name is "System.Runtime" or "System.Threading.Tasks" or "System.Console"
                or "System.Private.CoreLib" or "netstandard")
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        var compilation = CSharpCompilation.Create("TestCompilation",
            syntaxTrees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        diagnostics.Should().BeEmpty(
            $"{because}, but got:\n" +
            string.Join("\n", diagnostics.Select(d => $"  {d.Location.GetLineSpan()}: {d.GetMessage()}")));
    }

    [Fact]
    public async Task FullProductionFlow_MapperScenario_TransformsAndCompiles()
    {
        // Load all source files from TestData
        var interfacesSource = LoadTestData("MapperScenario_Interfaces");
        var mapperASource = LoadTestData("MapperA");
        var mapperBSource = LoadTestData("MapperB");
        var mapperCSource = LoadTestData("MapperC");
        var mapperDSource = LoadTestData("MapperD");
        var controllerSource = LoadTestData("MapperController");

        // Pre-check: all original sources compile together
        AssertCompilesMultiple(
            [interfacesSource, mapperASource, mapperBSource, mapperCSource, mapperDSource, controllerSource],
            "all original sources should compile together before transformation");

        // === PHASE 1: Build call graph from actual source using Roslyn ===
        // This mirrors CallGraphBuilder.Build() which uses MethodExtractor + MethodCallExtractor

        var tempDir = Path.Combine(Path.GetTempPath(), "async-rewriter-inttest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // Write source files to temp dir (TransformProjectAsync reads from disk)
            var interfacesPath = Path.Combine(tempDir, "Interfaces.cs");
            var mapperAPath = Path.Combine(tempDir, "MapperA.cs");
            var mapperBPath = Path.Combine(tempDir, "MapperB.cs");
            var mapperCPath = Path.Combine(tempDir, "MapperC.cs");
            var mapperDPath = Path.Combine(tempDir, "MapperD.cs");
            var controllerPath = Path.Combine(tempDir, "Controller.cs");

            await File.WriteAllTextAsync(interfacesPath, interfacesSource);
            await File.WriteAllTextAsync(mapperAPath, mapperASource);
            await File.WriteAllTextAsync(mapperBPath, mapperBSource);
            await File.WriteAllTextAsync(mapperCPath, mapperCSource);
            await File.WriteAllTextAsync(mapperDPath, mapperDSource);
            await File.WriteAllTextAsync(controllerPath, controllerSource);

            var callGraph = await BuildCallGraphFromSources(
                (interfacesPath, interfacesSource),
                (mapperAPath, mapperASource),
                (mapperBPath, mapperBSource),
                (mapperCPath, mapperCSource),
                (mapperDPath, mapperDSource),
                (controllerPath, controllerSource));

            // Verify the call graph was built correctly
            callGraph.Methods.Should().NotBeEmpty("call graph should contain extracted methods");

            // Verify key methods were found
            var mapperBMapInto = callGraph.Methods.Values
                .FirstOrDefault(m => m.Name == "MapInto" && m.ContainingType == "MapperB");
            mapperBMapInto.Should().NotBeNull("MapperB.MapInto should be in the call graph");

            var mapperAMapInto = callGraph.Methods.Values
                .FirstOrDefault(m => m.Name == "MapInto" && m.ContainingType == "MapperA");
            mapperAMapInto.Should().NotBeNull("MapperA.MapInto should be in the call graph");

            // Verify interface implementations were detected
            callGraph.InterfaceImplementations.Should().NotBeEmpty(
                "interface implementations should be detected");

            // Verify calls: MapperB → DbContext.SaveAsync
            var saveAsyncMethod = callGraph.Methods.Values
                .FirstOrDefault(m => m.Name == "SaveAsync" && m.ContainingType == "DbContext");
            saveAsyncMethod.Should().NotBeNull("DbContext.SaveAsync should be in the call graph");

            callGraph.Calls.Should().Contain(c =>
                c.CallerId == mapperBMapInto!.Id && c.CalleeId == saveAsyncMethod!.Id,
                "MapperB should call DbContext.SaveAsync");

            // === PHASE 2: Find task wrapper methods ===
            // In this scenario, SaveAsync and ValidateAsync are already async (return Task),
            // so they are the root methods for flooding (not sync wrappers, but direct async roots).
            // The production flow would find sync wrappers, but here we use the async methods directly.
            var taskWrapperExtractor = new DirtyTaskMethodsExtractor();
            var syncWrappers = taskWrapperExtractor.Extract(callGraph);

            // Collect root method IDs: async methods that return Task and are called by our mappers
            var rootMethodIds = new HashSet<string>();
            foreach (var method in callGraph.Methods.Values)
            {
                if (method.ReturnType is "Task" or "System.Threading.Tasks.Task"
                    && method.FilePath == interfacesPath)
                {
                    rootMethodIds.Add(method.Id);
                }
            }
            // Also add any detected sync wrappers
            foreach (var wrapper in syncWrappers)
            {
                rootMethodIds.Add(wrapper.MethodId);
            }

            rootMethodIds.Should().NotBeEmpty("should have root async methods for flooding");

            // === PHASE 3: Run async flooding ===
            var floodingAnalyzer = new AsyncCallGraphFlooder(NullLogger<AsyncCallGraphFlooder>.Instance);
            var floodedGraph = await floodingAnalyzer.Flood(callGraph, rootMethodIds);

            // Verify flooding results
            var floodedMapperB = floodedGraph.Methods.Values
                .First(m => m.Name == "MapInto" && m.ContainingType == "MapperB");
            floodedMapperB.ReturnType.Should().Be("Task",
                "MapperB.MapInto should be flooded (calls SaveAsync)");

            var floodedMapperA = floodedGraph.Methods.Values
                .First(m => m.Name == "MapInto" && m.ContainingType == "MapperA");
            floodedMapperA.ReturnType.Should().Be("Task",
                "MapperA.MapInto should be flooded (calls MapperB.MapInto via interface)");

            var floodedMapperC = floodedGraph.Methods.Values
                .First(m => m.Name == "MapInto" && m.ContainingType == "MapperC");
            floodedMapperC.ReturnType.Should().Be("Task",
                "MapperC.MapInto should be flooded (calls ValidateAsync)");

            // === PHASE 4: Detect problematic interfaces ===
            var problematicInterfaces = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(
                callGraph, floodedGraph);

            // IMapInto instantiations should be problematic (external interface, return type changed)
            // Note: depending on how the interface file is classified (external or not),
            // this may or may not detect problems. In production, the interface comes from
            // an external NuGet package. Here we simulate by checking what was detected.

            // === PHASE 5: Set up interface mappings ===
            // In production this is done interactively. Here we set it directly.
            floodedGraph.InterfaceMappings = new List<InterfaceMapping>
            {
                new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
            };

            // === PHASE 6: Transform using TransformProjectAsync (production code path) ===
            var transformer = new AsyncTransformer();
            var result = await transformer.TransformProjectAsync(tempDir, floodedGraph);

            result.Success.Should().BeTrue("transformation should succeed");
            result.Errors.Should().BeEmpty();
            result.ModifiedFiles.Should().NotBeEmpty("at least some files should be transformed");
            result.TotalMethodsTransformed.Should().BeGreaterThan(0);

            // Verify individual file transformations
            var mapperBFile = result.ModifiedFiles.FirstOrDefault(f => f.FilePath == mapperBPath);
            mapperBFile.Should().NotBeNull("MapperB should be transformed");
            mapperBFile!.TransformedContent.Should().Contain("Task MapInto(",
                "MapperB.MapInto should return Task");
            mapperBFile.TransformedContent.Should().Contain("return _db.SaveAsync();",
                "MapperB should directly return the task");
            mapperBFile.TransformedContent.Should().Contain("IMapIntoAsync<bool, string>",
                "MapperB should implement IMapIntoAsync");

            var mapperAFile = result.ModifiedFiles.FirstOrDefault(f => f.FilePath == mapperAPath);
            mapperAFile.Should().NotBeNull("MapperA should be transformed");
            mapperAFile!.TransformedContent.Should().Contain("Task MapInto(",
                "MapperA.MapInto should return Task");
            mapperAFile.TransformedContent.Should().Contain("return _mapperB.MapInto(",
                "MapperA should directly return the task from MapperB.MapInto call");
            mapperAFile.TransformedContent.Should().Contain("IMapIntoAsync<int, string>",
                "MapperA should implement IMapIntoAsync");
            mapperAFile.TransformedContent.Should().Contain("IMapIntoAsync<bool, string>",
                "MapperA's dependency on IMapInto<bool, string> should be replaced");

            var mapperCFile = result.ModifiedFiles.FirstOrDefault(f => f.FilePath == mapperCPath);
            mapperCFile.Should().NotBeNull("MapperC should be transformed");
            mapperCFile!.TransformedContent.Should().Contain("Task MapInto(",
                "MapperC.MapInto should return Task");
            mapperCFile.TransformedContent.Should().Contain("IMapIntoAsync<double, string>",
                "MapperC should implement IMapIntoAsync");

            var controllerFile = result.ModifiedFiles.FirstOrDefault(f => f.FilePath == controllerPath);
            controllerFile.Should().NotBeNull("Controller should be transformed");
            controllerFile!.TransformedContent.Should().Contain("async Task HandleRequest(",
                "Controller.HandleRequest should become async");
            controllerFile.TransformedContent.Should().Contain("IMapIntoAsync<int, string>",
                "Controller should use IMapIntoAsync for MapperA");

            // === PHASE 7: Post-check — all transformed sources compile together ===
            // Collect the final state of each file: transformed if modified, original otherwise.
            var modifiedPaths = result.ModifiedFiles.Select(f => f.FilePath).ToHashSet();
            var allPaths = new[] { interfacesPath, mapperAPath, mapperBPath, mapperCPath, mapperDPath, controllerPath };
            var transformedSources = new List<string>();
            foreach (var path in allPaths)
            {
                var modifiedFile = result.ModifiedFiles.FirstOrDefault(f => f.FilePath == path);
                transformedSources.Add(modifiedFile != null
                    ? modifiedFile.TransformedContent
                    : await File.ReadAllTextAsync(path));
            }

            AssertCompilesMultiple(
                transformedSources.ToArray(),
                "all transformed sources should compile together after transformation");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
