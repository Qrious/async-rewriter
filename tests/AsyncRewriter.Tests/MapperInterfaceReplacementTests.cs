using System.Collections.Concurrent;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AsyncRewriter.Tests;

/// <summary>
/// End-to-end tests for generic interface replacement with IMapInto scenarios.
/// Tests flooding analysis and interface replacement across 4 mappers (A, B, C, D)
/// with a Controller consuming all of them via their derived interfaces.
/// </summary>
public class MapperInterfaceReplacementTests
{
    private readonly AsyncFloodingAnalyzer _analyzer = new();
    private readonly AsyncTransformer _transformer = new();

    /// <summary>
    /// Stub types providing IMapInto, IMapIntoAsync, DbContext, Validator so test sources compile.
    /// </summary>
    private const string StubTypes = @"
using System.Threading.Tasks;

public interface IMapInto<TSource, TTarget>
{
    void MapInto(TTarget destination, TSource source);
}

public interface IMapIntoAsync<TSource, TTarget>
{
    Task MapInto(TTarget destination, TSource source);
}

public class DbContext
{
    public Task SaveAsync() => Task.CompletedTask;
}

public class Validator
{
    public Task ValidateAsync() => Task.CompletedTask;
}
";

    /// <summary>
    /// Verifies that the given C# source compiles without errors.
    /// </summary>
    private static void AssertCompiles(string source, string because = "code should compile without errors")
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var stubTree = CSharpSyntaxTree.ParseText(StubTypes);

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
            new[] { syntaxTree, stubTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        diagnostics.Should().BeEmpty(
            $"{because}, but got:\n" +
            string.Join("\n", diagnostics.Select(d => $"  {d.Location.GetLineSpan()}: {d.GetMessage()}")));
    }

    private static CallGraph CreateCallGraph(
        Dictionary<string, MethodNode> methods,
        List<MethodCall>? calls = null,
        List<InterfaceImplementation>? interfaceImpls = null,
        List<GenericInstantiation>? genericInstantiations = null)
    {
        var graphId = Guid.NewGuid().ToString();
        var methodDict = new ConcurrentDictionary<string, MethodNode>(methods);
        var callBag = new ConcurrentBag<MethodCall>(calls ?? []);
        var implBag = new ConcurrentBag<InterfaceImplementation>(interfaceImpls ?? []);
        var overrideBag = new ConcurrentBag<MethodOverride>();
        var giBag = new ConcurrentBag<GenericInstantiation>(genericInstantiations ?? []);

        return new CallGraph(callBag, implBag, overrideBag, giBag)
        {
            Id = graphId,
            ProjectName = "TestProject",
            Methods = methodDict
        };
    }

    private static MethodNode MakeMethod(string id, string name, string returnType,
        string containingType = "TestClass", string filePath = "test.cs",
        string containingNamespace = "TestNamespace", bool isReturnTypeParameter = false)
        => new()
        {
            CallGraphId = "g",
            Id = id,
            Name = name,
            ContainingType = containingType,
            ContainingNamespace = containingNamespace,
            ReturnType = returnType,
            Parameters = [],
            FilePath = filePath,
            StartLine = 1,
            EndLine = 10,
            IsReturnTypeParameter = isReturnTypeParameter
        };

    private static MethodCall MakeCall(string callerId, string calleeId)
        => new()
        {
            CallGraphId = "g",
            Id = Guid.NewGuid().ToString(),
            CallerId = callerId,
            CalleeId = calleeId,
            LineNumber = 1,
            FilePath = "test.cs"
        };

    #region Flooding Analysis

    [Fact]
    public async Task Flooding_MapperACallsMapperB_BothGetFlooded()
    {
        // Scenario:
        // - IMapInto<TSource, TTarget> is a generic interface with MapInto(TTarget dest, TSource source) -> void
        //   (return type is void, NOT a type parameter, so flooding CAN propagate through generic)
        // - MapperA : IMapInto<int, string> — calls MapperB internally
        // - MapperB : IMapInto<bool, string> — calls an async root method
        // - MapperC : IMapInto<double, string> — calls another async method directly
        // - MapperD : IMapInto<long, string> — pure sync, no async calls
        // - Controller uses all 4 mappers via their interfaces

        var methods = new Dictionary<string, MethodNode>
        {
            // Generic interface definition
            ["imap_generic"] = MakeMethod("imap_generic", "MapInto", "void",
                "IMapInto<TSource, TTarget>", "external"),

            // Instantiated interface methods (one per mapper type combo)
            ["imap_int_string"] = MakeMethod("imap_int_string", "MapInto", "void",
                "IMapInto<int, string>", "external"),
            ["imap_bool_string"] = MakeMethod("imap_bool_string", "MapInto", "void",
                "IMapInto<bool, string>", "external"),
            ["imap_double_string"] = MakeMethod("imap_double_string", "MapInto", "void",
                "IMapInto<double, string>", "external"),
            ["imap_long_string"] = MakeMethod("imap_long_string", "MapInto", "void",
                "IMapInto<long, string>", "external"),

            // Mapper implementations
            ["mapperA_mapinto"] = MakeMethod("mapperA_mapinto", "MapInto", "void", "MapperA"),
            ["mapperB_mapinto"] = MakeMethod("mapperB_mapinto", "MapInto", "void", "MapperB"),
            ["mapperC_mapinto"] = MakeMethod("mapperC_mapinto", "MapInto", "void", "MapperC"),
            ["mapperD_mapinto"] = MakeMethod("mapperD_mapinto", "MapInto", "void", "MapperD"),

            // Async root: the method that starts the flooding
            ["async_root"] = MakeMethod("async_root", "SaveAsync", "void", "DbContext"),

            // Another async method that MapperC calls
            ["another_async"] = MakeMethod("another_async", "ValidateAsync", "void", "Validator"),

            // Controller
            ["controller_action"] = MakeMethod("controller_action", "HandleRequest", "void", "Controller"),
        };

        var calls = new List<MethodCall>
        {
            // MapperA calls MapperB internally
            MakeCall("mapperA_mapinto", "mapperB_mapinto"),
            // MapperB calls the async root
            MakeCall("mapperB_mapinto", "async_root"),
            // MapperC calls another async method
            MakeCall("mapperC_mapinto", "another_async"),
            // Controller calls all 4 mappers via their instantiated interfaces
            MakeCall("controller_action", "imap_int_string"),
            MakeCall("controller_action", "imap_bool_string"),
            MakeCall("controller_action", "imap_double_string"),
            MakeCall("controller_action", "imap_long_string"),
        };

        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "mapperA_mapinto", InterfaceMethodId = "imap_int_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperB_mapinto", InterfaceMethodId = "imap_bool_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperC_mapinto", InterfaceMethodId = "imap_double_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperD_mapinto", InterfaceMethodId = "imap_long_string" },
        };

        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_int_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_bool_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_double_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_long_string", GenericMethodId = "imap_generic" },
        };

        var graph = CreateCallGraph(methods, calls, impls, genericInstantiations);

        // Flood from the two async roots
        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["async_root", "another_async"]);

        // MapperB: flooded because it calls async_root
        result.Methods["mapperB_mapinto"].ReturnType.Should().Be("Task",
            "MapperB calls async_root and must become async");

        // MapperA: flooded because it calls MapperB
        result.Methods["mapperA_mapinto"].ReturnType.Should().Be("Task",
            "MapperA calls MapperB which is async, so MapperA must become async too");

        // MapperC: flooded because it calls another_async
        result.Methods["mapperC_mapinto"].ReturnType.Should().Be("Task",
            "MapperC calls another_async and must become async");

        // Instantiated interfaces for A, B, C: flooded via InterfaceImplementation
        result.Methods["imap_int_string"].ReturnType.Should().Be("Task",
            "IMapInto<int, string> is flooded because MapperA (its impl) is flooded");
        result.Methods["imap_bool_string"].ReturnType.Should().Be("Task",
            "IMapInto<bool, string> is flooded because MapperB (its impl) is flooded");
        result.Methods["imap_double_string"].ReturnType.Should().Be("Task",
            "IMapInto<double, string> is flooded because MapperC (its impl) is flooded");

        // Generic interface: flooded because return type is void (not a type parameter)
        result.Methods["imap_generic"].ReturnType.Should().Be("Task",
            "generic IMapInto<TSource, TTarget>.MapInto has void return type, so it gets flooded");

        // Instantiated interface for D: flooded because generic interface is flooded
        // (void return type allows generic → instantiation propagation)
        result.Methods["imap_long_string"].ReturnType.Should().Be("Task",
            "IMapInto<long, string> is flooded via generic interface propagation (void return type)");

        // MapperD: flooded via generic → instantiation → implementation chain
        // Even though MapperD has no direct async calls, the void return type on the generic
        // interface allows flooding to propagate to ALL instantiations and their implementations
        result.Methods["mapperD_mapinto"].ReturnType.Should().Be("Task",
            "MapperD is flooded via generic interface propagation: " +
            "instantiated iface → generic iface → all instantiations → all implementations");

        // Controller: flooded because it calls the instantiated interfaces
        result.Methods["controller_action"].ReturnType.Should().Be("Task",
            "Controller calls flooded interface methods and must become async");
    }

    [Fact]
    public async Task Flooding_WithBlockedGeneric_OnlyDirectlyConnectedMappersFlooded()
    {
        // Same scenario but with the generic method blocked — prevents cross-contamination.
        // Only mappers A, B, C (which have direct async call chains) should be flooded.
        // MapperD should stay sync because the generic interface can't propagate.

        var methods = new Dictionary<string, MethodNode>
        {
            ["imap_generic"] = MakeMethod("imap_generic", "MapInto", "void",
                "IMapInto<TSource, TTarget>", "external"),
            ["imap_int_string"] = MakeMethod("imap_int_string", "MapInto", "void",
                "IMapInto<int, string>", "external"),
            ["imap_bool_string"] = MakeMethod("imap_bool_string", "MapInto", "void",
                "IMapInto<bool, string>", "external"),
            ["imap_double_string"] = MakeMethod("imap_double_string", "MapInto", "void",
                "IMapInto<double, string>", "external"),
            ["imap_long_string"] = MakeMethod("imap_long_string", "MapInto", "void",
                "IMapInto<long, string>", "external"),
            ["mapperA_mapinto"] = MakeMethod("mapperA_mapinto", "MapInto", "void", "MapperA"),
            ["mapperB_mapinto"] = MakeMethod("mapperB_mapinto", "MapInto", "void", "MapperB"),
            ["mapperC_mapinto"] = MakeMethod("mapperC_mapinto", "MapInto", "void", "MapperC"),
            ["mapperD_mapinto"] = MakeMethod("mapperD_mapinto", "MapInto", "void", "MapperD"),
            ["async_root"] = MakeMethod("async_root", "SaveAsync", "void", "DbContext"),
            ["another_async"] = MakeMethod("another_async", "ValidateAsync", "void", "Validator"),
            ["controller_action"] = MakeMethod("controller_action", "HandleRequest", "void", "Controller"),
        };

        var calls = new List<MethodCall>
        {
            MakeCall("mapperA_mapinto", "mapperB_mapinto"),
            MakeCall("mapperB_mapinto", "async_root"),
            MakeCall("mapperC_mapinto", "another_async"),
            MakeCall("controller_action", "imap_int_string"),
            MakeCall("controller_action", "imap_bool_string"),
            MakeCall("controller_action", "imap_double_string"),
            MakeCall("controller_action", "imap_long_string"),
        };

        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "mapperA_mapinto", InterfaceMethodId = "imap_int_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperB_mapinto", InterfaceMethodId = "imap_bool_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperC_mapinto", InterfaceMethodId = "imap_double_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperD_mapinto", InterfaceMethodId = "imap_long_string" },
        };

        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_int_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_bool_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_double_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_long_string", GenericMethodId = "imap_generic" },
        };

        var graph = CreateCallGraph(methods, calls, impls, genericInstantiations);

        // Block the generic method to prevent cross-contamination between instantiations
        var blocked = new HashSet<string> { "imap_generic" };
        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["async_root", "another_async"], blocked);

        // Mappers A, B, C: flooded via direct call chains
        result.Methods["mapperA_mapinto"].ReturnType.Should().Be("Task");
        result.Methods["mapperB_mapinto"].ReturnType.Should().Be("Task");
        result.Methods["mapperC_mapinto"].ReturnType.Should().Be("Task");

        // MapperD: NOT flooded — blocked generic prevents propagation
        result.Methods["mapperD_mapinto"].ReturnType.Should().Be("void",
            "MapperD should stay sync when generic propagation is blocked");

        // Instantiated interfaces for A, B, C: flooded via implementation
        result.Methods["imap_int_string"].ReturnType.Should().Be("Task");
        result.Methods["imap_bool_string"].ReturnType.Should().Be("Task");
        result.Methods["imap_double_string"].ReturnType.Should().Be("Task");

        // Instantiated interface for D: NOT flooded
        result.Methods["imap_long_string"].ReturnType.Should().Be("void",
            "IMapInto<long, string> should not be flooded when generic is blocked");

        // Generic interface: NOT flooded (blocked)
        result.Methods["imap_generic"].ReturnType.Should().Be("void",
            "generic interface should not be flooded when blocked");

        // Controller: still flooded because it calls flooded instantiated interfaces
        result.Methods["controller_action"].ReturnType.Should().Be("Task",
            "Controller calls flooded interface methods (A, B, C) and must become async");
    }

    #endregion

    #region Problematic Interface Detection

    [Fact]
    public void DetectProblematicInterfaces_FindsFloodedMapperInterfaces()
    {
        // After flooding with blocked generic, mappers A/B/C have changed return types.
        // Their interface methods (external) become problematic because
        // the impl return type changed but the interface return type did not.
        var syncMethods = new Dictionary<string, MethodNode>
        {
            ["imap_int_string"] = MakeMethod("imap_int_string", "MapInto", "void",
                "IMapInto<int, string>", "external"),
            ["imap_bool_string"] = MakeMethod("imap_bool_string", "MapInto", "void",
                "IMapInto<bool, string>", "external"),
            ["imap_double_string"] = MakeMethod("imap_double_string", "MapInto", "void",
                "IMapInto<double, string>", "external"),
            ["imap_long_string"] = MakeMethod("imap_long_string", "MapInto", "void",
                "IMapInto<long, string>", "external"),
            ["mapperA_mapinto"] = MakeMethod("mapperA_mapinto", "MapInto", "void", "MapperA"),
            ["mapperB_mapinto"] = MakeMethod("mapperB_mapinto", "MapInto", "void", "MapperB"),
            ["mapperC_mapinto"] = MakeMethod("mapperC_mapinto", "MapInto", "void", "MapperC"),
            ["mapperD_mapinto"] = MakeMethod("mapperD_mapinto", "MapInto", "void", "MapperD"),
        };

        var syncGraph = CreateCallGraph(syncMethods,
            interfaceImpls: new List<InterfaceImplementation>
            {
                new() { CallGraphId = "g", ImplementingMethodId = "mapperA_mapinto", InterfaceMethodId = "imap_int_string" },
                new() { CallGraphId = "g", ImplementingMethodId = "mapperB_mapinto", InterfaceMethodId = "imap_bool_string" },
                new() { CallGraphId = "g", ImplementingMethodId = "mapperC_mapinto", InterfaceMethodId = "imap_double_string" },
                new() { CallGraphId = "g", ImplementingMethodId = "mapperD_mapinto", InterfaceMethodId = "imap_long_string" },
            });

        // After flooding: A, B, C have Task return types; D unchanged
        var asyncMethods = new Dictionary<string, MethodNode>
        {
            ["imap_int_string"] = MakeMethod("imap_int_string", "MapInto", "void",
                "IMapInto<int, string>", "external"),
            ["imap_bool_string"] = MakeMethod("imap_bool_string", "MapInto", "void",
                "IMapInto<bool, string>", "external"),
            ["imap_double_string"] = MakeMethod("imap_double_string", "MapInto", "void",
                "IMapInto<double, string>", "external"),
            ["imap_long_string"] = MakeMethod("imap_long_string", "MapInto", "void",
                "IMapInto<long, string>", "external"),
            ["mapperA_mapinto"] = MakeMethod("mapperA_mapinto", "MapInto", "Task", "MapperA"),
            ["mapperB_mapinto"] = MakeMethod("mapperB_mapinto", "MapInto", "Task", "MapperB"),
            ["mapperC_mapinto"] = MakeMethod("mapperC_mapinto", "MapInto", "Task", "MapperC"),
            ["mapperD_mapinto"] = MakeMethod("mapperD_mapinto", "MapInto", "void", "MapperD"),
        };

        var asyncGraph = CreateCallGraph(asyncMethods);

        var result = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(syncGraph, asyncGraph);

        // Three instantiated interfaces should be detected as problematic
        result.Should().ContainKey("IMapInto<int, string>");
        result.Should().ContainKey("IMapInto<bool, string>");
        result.Should().ContainKey("IMapInto<double, string>");

        // MapperD's interface should NOT be problematic (return type unchanged)
        result.Should().NotContainKey("IMapInto<long, string>");
    }

    #endregion

    #region Interface Replacement

    [Fact]
    public void InterfaceReplacer_ReplacesAllMapperBaseListInterfaces()
    {
        var source = @"
public class MapperA : IMapInto<int, string>
{
    public void MapInto(string dest, int source) { }
}

public class MapperB : IMapInto<bool, string>
{
    public void MapInto(string dest, bool source) { }
}

public class MapperC : IMapInto<double, string>
{
    public void MapInto(string dest, double source) { }
}

public class MapperD : IMapInto<long, string>
{
    public void MapInto(string dest, long source) { }
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("IMapIntoAsync<int, string>");
        result.Should().Contain("IMapIntoAsync<bool, string>");
        result.Should().Contain("IMapIntoAsync<double, string>");
        result.Should().Contain("IMapIntoAsync<long, string>");
        result.Should().NotContain(": IMapInto<");
    }

    [Fact]
    public void InterfaceReplacer_ReplacesControllerFieldsAndParameters()
    {
        var source = @"using System.Threading.Tasks;

public class Controller
{
    private readonly IMapInto<int, string> _mapperA;
    private readonly IMapInto<bool, string> _mapperB;
    private readonly IMapInto<double, string> _mapperC;
    private readonly IMapInto<long, string> _mapperD;

    public Controller(
        IMapInto<int, string> mapperA,
        IMapInto<bool, string> mapperB,
        IMapInto<double, string> mapperC,
        IMapInto<long, string> mapperD)
    {
        _mapperA = mapperA;
        _mapperB = mapperB;
        _mapperC = mapperC;
        _mapperD = mapperD;
    }

    public void HandleRequest()
    {
        _mapperA.MapInto(""hello"", 42);
        _mapperB.MapInto(""world"", true);
        _mapperC.MapInto(""foo"", 3.14);
        _mapperD.MapInto(""bar"", 100L);
    }
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();

        // Fields should be replaced
        result.Should().Contain("private readonly IMapIntoAsync<int, string> _mapperA;");
        result.Should().Contain("private readonly IMapIntoAsync<bool, string> _mapperB;");
        result.Should().Contain("private readonly IMapIntoAsync<double, string> _mapperC;");
        result.Should().Contain("private readonly IMapIntoAsync<long, string> _mapperD;");

        // Constructor parameters should be replaced
        result.Should().Contain("IMapIntoAsync<int, string> mapperA");
        result.Should().Contain("IMapIntoAsync<bool, string> mapperB");
        result.Should().Contain("IMapIntoAsync<double, string> mapperC");
        result.Should().Contain("IMapIntoAsync<long, string> mapperD");

        // No remaining sync references
        result.Should().NotContain("IMapInto<");
    }

    [Fact]
    public void InterfaceReplacer_AddsRequiredNamespace_ForAsyncInterface()
    {
        var source = @"using System;

public class Controller
{
    private readonly IMapInto<int, string> _mapper;

    public Controller(IMapInto<int, string> mapper)
    {
        _mapper = mapper;
    }
}";
        var mappings = new List<InterfaceMapping>
        {
            new()
            {
                SyncInterfaceName = "IMapInto",
                AsyncInterfaceName = "IMapIntoAsync",
                RequiredNamespaces = new List<string> { "MyApp.Async.Interfaces" }
            }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("using MyApp.Async.Interfaces;");
        result.Should().Contain("IMapIntoAsync<int, string>");
    }

    [Fact]
    public void InterfaceReplacer_SelectiveReplacement_OnlyReplacesSpecifiedInstantiations()
    {
        // When using scoped replacement, only specific instantiations get replaced.
        // This tests that a class implementing a non-replaced instantiation keeps the sync interface.
        var mapperSource = @"
public class MapperA : IMapInto<int, string>
{
    public void MapInto(string dest, int source) { }
}";
        var controllerSource = @"
public class Controller
{
    private readonly IMapInto<int, string> _mapperA;
    private readonly IMapInto<long, string> _mapperD;

    public Controller(IMapInto<int, string> mapperA, IMapInto<long, string> mapperD)
    {
        _mapperA = mapperA;
        _mapperD = mapperD;
    }
}";

        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };

        // Mapper file: all IMapInto references get replaced (it only has one)
        var mapperResult = InterfaceReplacer.Transform(mapperSource, mappings);
        mapperResult.Should().NotBeNull();
        mapperResult.Should().Contain("IMapIntoAsync<int, string>");

        // Controller file: ALL IMapInto references get replaced (the replacer is name-based)
        var controllerResult = InterfaceReplacer.Transform(controllerSource, mappings);
        controllerResult.Should().NotBeNull();
        controllerResult.Should().Contain("IMapIntoAsync<int, string>");
        controllerResult.Should().Contain("IMapIntoAsync<long, string>");
    }

    #endregion

    #region Full Scenario: Flooding + Interface Replacement

    [Fact]
    public async Task FullScenario_FloodingAndReplacement_AllReferencesTransformedCorrectly()
    {
        // Full end-to-end scenario:
        // - IMapInto<TSource, TTarget> { void MapInto(TTarget dest, TSource source); }
        //   is a "problematic" external interface
        // - IMapIntoAsync<TSource, TTarget> { Task MapIntoAsync(TTarget dest, TSource source); }
        //   is the async replacement
        // - MapperA : IMapInto<int, string> — calls MapperB internally
        // - MapperB : IMapInto<bool, string> — calls async root
        // - MapperC : IMapInto<double, string> — calls another async method
        // - MapperD : IMapInto<long, string> — pure sync
        // - Controller injects all 4 via interfaces

        // Step 1: Build call graph and flood with blocked generic
        var methods = new Dictionary<string, MethodNode>
        {
            ["imap_generic"] = MakeMethod("imap_generic", "MapInto", "void",
                "IMapInto<TSource, TTarget>", "external"),
            ["imap_int_string"] = MakeMethod("imap_int_string", "MapInto", "void",
                "IMapInto<int, string>", "external"),
            ["imap_bool_string"] = MakeMethod("imap_bool_string", "MapInto", "void",
                "IMapInto<bool, string>", "external"),
            ["imap_double_string"] = MakeMethod("imap_double_string", "MapInto", "void",
                "IMapInto<double, string>", "external"),
            ["imap_long_string"] = MakeMethod("imap_long_string", "MapInto", "void",
                "IMapInto<long, string>", "external"),
            ["mapperA_mapinto"] = MakeMethod("mapperA_mapinto", "MapInto", "void", "MapperA"),
            ["mapperB_mapinto"] = MakeMethod("mapperB_mapinto", "MapInto", "void", "MapperB"),
            ["mapperC_mapinto"] = MakeMethod("mapperC_mapinto", "MapInto", "void", "MapperC"),
            ["mapperD_mapinto"] = MakeMethod("mapperD_mapinto", "MapInto", "void", "MapperD"),
            ["async_root"] = MakeMethod("async_root", "SaveAsync", "void", "DbContext"),
            ["another_async"] = MakeMethod("another_async", "ValidateAsync", "void", "Validator"),
            ["controller_action"] = MakeMethod("controller_action", "HandleRequest", "void", "Controller"),
        };

        var calls = new List<MethodCall>
        {
            MakeCall("mapperA_mapinto", "mapperB_mapinto"),
            MakeCall("mapperB_mapinto", "async_root"),
            MakeCall("mapperC_mapinto", "another_async"),
            MakeCall("controller_action", "imap_int_string"),
            MakeCall("controller_action", "imap_bool_string"),
            MakeCall("controller_action", "imap_double_string"),
            MakeCall("controller_action", "imap_long_string"),
        };

        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "mapperA_mapinto", InterfaceMethodId = "imap_int_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperB_mapinto", InterfaceMethodId = "imap_bool_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperC_mapinto", InterfaceMethodId = "imap_double_string" },
            new() { CallGraphId = "g", ImplementingMethodId = "mapperD_mapinto", InterfaceMethodId = "imap_long_string" },
        };

        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_int_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_bool_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_double_string", GenericMethodId = "imap_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_long_string", GenericMethodId = "imap_generic" },
        };

        var graph = CreateCallGraph(methods, calls, impls, genericInstantiations);

        // Use blocked generic to get scoped flooding (only directly affected mappers)
        var blocked = new HashSet<string> { "imap_generic" };
        var floodedGraph = await _analyzer.AnalyzeFloodingAsync(graph, ["async_root", "another_async"], blocked);

        // Verify flooding results
        floodedGraph.Methods["mapperA_mapinto"].ReturnType.Should().Be("Task");
        floodedGraph.Methods["mapperB_mapinto"].ReturnType.Should().Be("Task");
        floodedGraph.Methods["mapperC_mapinto"].ReturnType.Should().Be("Task");
        floodedGraph.Methods["mapperD_mapinto"].ReturnType.Should().Be("void");

        // Step 2: Detect problematic interfaces
        var problematic = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(graph, floodedGraph);

        problematic.Should().ContainKey("IMapInto<int, string>");
        problematic.Should().ContainKey("IMapInto<bool, string>");
        problematic.Should().ContainKey("IMapInto<double, string>");
        problematic.Should().NotContainKey("IMapInto<long, string>",
            "MapperD is not flooded, so its interface is not problematic");

        // Step 3: Apply interface replacement on source files
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };

        // Controller source with all 4 mapper injections
        var controllerSource = @"using System.Threading.Tasks;

namespace MyApp;

public class Controller
{
    private readonly IMapInto<int, string> _mapperA;
    private readonly IMapInto<bool, string> _mapperB;
    private readonly IMapInto<double, string> _mapperC;
    private readonly IMapInto<long, string> _mapperD;

    public Controller(
        IMapInto<int, string> mapperA,
        IMapInto<bool, string> mapperB,
        IMapInto<double, string> mapperC,
        IMapInto<long, string> mapperD)
    {
        _mapperA = mapperA;
        _mapperB = mapperB;
        _mapperC = mapperC;
        _mapperD = mapperD;
    }

    public void HandleRequest()
    {
        _mapperA.MapInto(""hello"", 42);
        _mapperB.MapInto(""world"", true);
        _mapperC.MapInto(""foo"", 3.14);
        _mapperD.MapInto(""bar"", 100L);
    }
}";

        var controllerResult = InterfaceReplacer.Transform(controllerSource, mappings);

        controllerResult.Should().NotBeNull();

        // All 4 field types replaced
        controllerResult.Should().Contain("IMapIntoAsync<int, string> _mapperA");
        controllerResult.Should().Contain("IMapIntoAsync<bool, string> _mapperB");
        controllerResult.Should().Contain("IMapIntoAsync<double, string> _mapperC");
        controllerResult.Should().Contain("IMapIntoAsync<long, string> _mapperD");

        // All 4 constructor parameter types replaced
        controllerResult.Should().Contain("IMapIntoAsync<int, string> mapperA");
        controllerResult.Should().Contain("IMapIntoAsync<bool, string> mapperB");
        controllerResult.Should().Contain("IMapIntoAsync<double, string> mapperC");
        controllerResult.Should().Contain("IMapIntoAsync<long, string> mapperD");

        // No remaining sync interface references
        controllerResult.Should().NotContain("IMapInto<");

        // Mapper source files — verify base list replacement
        var mapperASource = @"
public class MapperA : IMapInto<int, string>
{
    private readonly IMapInto<bool, string> _mapperB;
    public MapperA(IMapInto<bool, string> mapperB) { _mapperB = mapperB; }
    public void MapInto(string dest, int source) { _mapperB.MapInto(dest, source > 0); }
}";
        var mapperAResult = InterfaceReplacer.Transform(mapperASource, mappings);
        mapperAResult.Should().NotBeNull();
        mapperAResult.Should().Contain(": IMapIntoAsync<int, string>");
        mapperAResult.Should().Contain("IMapIntoAsync<bool, string> _mapperB");
        mapperAResult.Should().Contain("IMapIntoAsync<bool, string> mapperB");
    }

    #endregion

    #region Transformation with Compilation Verification

    [Fact]
    public async Task TransformAndCompile_MapperB_AsyncRootCall_CompilesBeforeAndAfter()
    {
        // MapperB calls DbContext.SaveAsync() — needs async/await transformation
        var source = @"using System.Threading.Tasks;

public class MapperB : IMapInto<bool, string>
{
    private readonly DbContext _db;

    public MapperB(DbContext db)
    {
        _db = db;
    }

    public void MapInto(string destination, bool source)
    {
        _db.SaveAsync();
    }
}";
        AssertCompiles(source, "original MapperB source should compile");

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperB.MapInto(string, bool)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 14, OriginalCallExpression = "_db.SaveAsync()" }
                }
            }
        };

        var transformed = await _transformer.TransformSourceAsync(source, transformations);

        transformed.Should().Contain("async Task MapInto(");
        transformed.Should().Contain("await _db.SaveAsync()");

        // Replace IMapInto with IMapIntoAsync in the transformed source
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };
        var replaced = InterfaceReplacer.Transform(transformed, mappings);
        replaced.Should().NotBeNull();
        replaced.Should().Contain(": IMapIntoAsync<bool, string>");
        replaced.Should().NotContain(": IMapInto<");

        AssertCompiles(replaced!, "transformed MapperB with IMapIntoAsync should compile");
    }

    [Fact]
    public async Task TransformAndCompile_MapperA_CallsMapperB_CompilesBeforeAndAfter()
    {
        // MapperA calls MapperB.MapInto internally via the IMapInto<bool, string> interface
        var source = @"using System.Threading.Tasks;

public class MapperA : IMapInto<int, string>
{
    private readonly IMapInto<bool, string> _mapperB;

    public MapperA(IMapInto<bool, string> mapperB)
    {
        _mapperB = mapperB;
    }

    public void MapInto(string destination, int source)
    {
        _mapperB.MapInto(destination, source > 0);
    }
}";
        AssertCompiles(source, "original MapperA source should compile");

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperA.MapInto(string, int)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 14, OriginalCallExpression = "_mapperB.MapInto(destination, source > 0)" }
                }
            }
        };

        var transformed = await _transformer.TransformSourceAsync(source, transformations);

        transformed.Should().Contain("async Task MapInto(");
        transformed.Should().Contain("await _mapperB.MapInto(destination, source > 0)");

        // Replace interface references
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };
        var replaced = InterfaceReplacer.Transform(transformed, mappings);
        replaced.Should().NotBeNull();
        replaced.Should().Contain(": IMapIntoAsync<int, string>");
        replaced.Should().Contain("IMapIntoAsync<bool, string> _mapperB");
        replaced.Should().Contain("IMapIntoAsync<bool, string> mapperB");
        replaced.Should().NotContain("IMapInto<");

        AssertCompiles(replaced!, "transformed MapperA with IMapIntoAsync should compile");
    }

    [Fact]
    public async Task TransformAndCompile_MapperC_DirectAsyncCall_CompilesBeforeAndAfter()
    {
        // MapperC calls Validator.ValidateAsync() directly
        var source = @"using System.Threading.Tasks;

public class MapperC : IMapInto<double, string>
{
    private readonly Validator _validator;

    public MapperC(Validator validator)
    {
        _validator = validator;
    }

    public void MapInto(string destination, double source)
    {
        _validator.ValidateAsync();
    }
}";
        AssertCompiles(source, "original MapperC source should compile");

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperC.MapInto(string, double)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 14, OriginalCallExpression = "_validator.ValidateAsync()" }
                }
            }
        };

        var transformed = await _transformer.TransformSourceAsync(source, transformations);

        transformed.Should().Contain("async Task MapInto(");
        transformed.Should().Contain("await _validator.ValidateAsync()");

        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };
        var replaced = InterfaceReplacer.Transform(transformed, mappings);
        replaced.Should().NotBeNull();
        replaced.Should().Contain(": IMapIntoAsync<double, string>");

        AssertCompiles(replaced!, "transformed MapperC with IMapIntoAsync should compile");
    }

    [Fact]
    public async Task TransformAndCompile_MapperD_PureSync_TaskCompletedTask_CompilesBeforeAndAfter()
    {
        // MapperD is pure sync — gets flooded via generic propagation, so uses Task.CompletedTask
        var source = @"using System.Threading.Tasks;

public class MapperD : IMapInto<long, string>
{
    public void MapInto(string destination, long source)
    {
        destination = source.ToString();
    }
}";
        AssertCompiles(source, "original MapperD source should compile");

        // MapperD has no async calls — NeedsAsyncKeyword = false, empty call sites
        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperD.MapInto(string, long)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var transformed = await _transformer.TransformSourceAsync(source, transformations);

        transformed.Should().Contain("Task MapInto(");
        transformed.Should().NotContain("async");
        transformed.Should().Contain("Task.CompletedTask");

        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };
        var replaced = InterfaceReplacer.Transform(transformed, mappings);
        replaced.Should().NotBeNull();
        replaced.Should().Contain(": IMapIntoAsync<long, string>");

        AssertCompiles(replaced!, "transformed MapperD with Task.CompletedTask and IMapIntoAsync should compile");
    }

    [Fact]
    public async Task TransformAndCompile_Controller_AllMappersAsync_CompilesBeforeAndAfter()
    {
        // Controller injects all 4 mappers via IMapInto interfaces and calls them in HandleRequest
        var source = @"using System.Threading.Tasks;

public class Controller
{
    private readonly IMapInto<int, string> _mapperA;
    private readonly IMapInto<bool, string> _mapperB;
    private readonly IMapInto<double, string> _mapperC;
    private readonly IMapInto<long, string> _mapperD;

    public Controller(
        IMapInto<int, string> mapperA,
        IMapInto<bool, string> mapperB,
        IMapInto<double, string> mapperC,
        IMapInto<long, string> mapperD)
    {
        _mapperA = mapperA;
        _mapperB = mapperB;
        _mapperC = mapperC;
        _mapperD = mapperD;
    }

    public void HandleRequest()
    {
        _mapperA.MapInto(""hello"", 42);
        _mapperB.MapInto(""world"", true);
        _mapperC.MapInto(""foo"", 3.14);
        _mapperD.MapInto(""bar"", 100L);
    }
}";
        AssertCompiles(source, "original Controller source should compile");

        // All 4 mapper calls need await
        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "Controller.HandleRequest()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 24, OriginalCallExpression = @"_mapperA.MapInto(""hello"", 42)" },
                    new() { LineNumber = 25, OriginalCallExpression = @"_mapperB.MapInto(""world"", true)" },
                    new() { LineNumber = 26, OriginalCallExpression = @"_mapperC.MapInto(""foo"", 3.14)" },
                    new() { LineNumber = 27, OriginalCallExpression = @"_mapperD.MapInto(""bar"", 100L)" },
                }
            }
        };

        var transformed = await _transformer.TransformSourceAsync(source, transformations);

        transformed.Should().Contain("async Task HandleRequest()");
        transformed.Should().Contain(@"await _mapperA.MapInto(""hello"", 42)");
        transformed.Should().Contain(@"await _mapperB.MapInto(""world"", true)");
        transformed.Should().Contain(@"await _mapperC.MapInto(""foo"", 3.14)");
        transformed.Should().Contain(@"await _mapperD.MapInto(""bar"", 100L)");

        // Replace all IMapInto references with IMapIntoAsync
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };
        var replaced = InterfaceReplacer.Transform(transformed, mappings);
        replaced.Should().NotBeNull();

        // Verify all field types replaced
        replaced.Should().Contain("IMapIntoAsync<int, string> _mapperA");
        replaced.Should().Contain("IMapIntoAsync<bool, string> _mapperB");
        replaced.Should().Contain("IMapIntoAsync<double, string> _mapperC");
        replaced.Should().Contain("IMapIntoAsync<long, string> _mapperD");

        // Verify all constructor parameters replaced
        replaced.Should().Contain("IMapIntoAsync<int, string> mapperA");
        replaced.Should().Contain("IMapIntoAsync<bool, string> mapperB");
        replaced.Should().Contain("IMapIntoAsync<double, string> mapperC");
        replaced.Should().Contain("IMapIntoAsync<long, string> mapperD");

        // No remaining sync references
        replaced.Should().NotContain("IMapInto<");

        AssertCompiles(replaced!, "transformed Controller with all IMapIntoAsync references should compile");
    }

    [Fact]
    public async Task TransformAndCompile_AllFiles_FullPipeline_CompileBeforeAndAfter()
    {
        // Full end-to-end: define all source files, verify they compile together,
        // apply transformations and interface replacements, verify they still compile.

        var mapperBSource = @"using System.Threading.Tasks;

public class MapperB : IMapInto<bool, string>
{
    private readonly DbContext _db;
    public MapperB(DbContext db) { _db = db; }
    public void MapInto(string destination, bool source)
    {
        _db.SaveAsync();
    }
}";

        var mapperASource = @"using System.Threading.Tasks;

public class MapperA : IMapInto<int, string>
{
    private readonly IMapInto<bool, string> _mapperB;
    public MapperA(IMapInto<bool, string> mapperB) { _mapperB = mapperB; }
    public void MapInto(string destination, int source)
    {
        _mapperB.MapInto(destination, source > 0);
    }
}";

        var mapperCSource = @"using System.Threading.Tasks;

public class MapperC : IMapInto<double, string>
{
    private readonly Validator _validator;
    public MapperC(Validator validator) { _validator = validator; }
    public void MapInto(string destination, double source)
    {
        _validator.ValidateAsync();
    }
}";

        var mapperDSource = @"using System.Threading.Tasks;

public class MapperD : IMapInto<long, string>
{
    public void MapInto(string destination, long source)
    {
        destination = source.ToString();
    }
}";

        var controllerSource = @"using System.Threading.Tasks;

public class Controller
{
    private readonly IMapInto<int, string> _mapperA;
    private readonly IMapInto<bool, string> _mapperB;
    private readonly IMapInto<double, string> _mapperC;
    private readonly IMapInto<long, string> _mapperD;

    public Controller(
        IMapInto<int, string> mapperA,
        IMapInto<bool, string> mapperB,
        IMapInto<double, string> mapperC,
        IMapInto<long, string> mapperD)
    {
        _mapperA = mapperA;
        _mapperB = mapperB;
        _mapperC = mapperC;
        _mapperD = mapperD;
    }

    public void HandleRequest()
    {
        _mapperA.MapInto(""hello"", 42);
        _mapperB.MapInto(""world"", true);
        _mapperC.MapInto(""foo"", 3.14);
        _mapperD.MapInto(""bar"", 100L);
    }
}";

        // Pre-check: all original sources compile together
        AssertCompilesMultiple(
            [mapperBSource, mapperASource, mapperCSource, mapperDSource, controllerSource],
            "all original sources should compile together");

        // Transform MapperB: async/await for SaveAsync
        var mapperBTransformed = await _transformer.TransformSourceAsync(mapperBSource, new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperB.MapInto(string, bool)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 9, OriginalCallExpression = "_db.SaveAsync()" }
                }
            }
        });

        // Transform MapperA: async/await for calling MapperB
        var mapperATransformed = await _transformer.TransformSourceAsync(mapperASource, new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperA.MapInto(string, int)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 9, OriginalCallExpression = "_mapperB.MapInto(destination, source > 0)" }
                }
            }
        });

        // Transform MapperC: async/await for ValidateAsync
        var mapperCTransformed = await _transformer.TransformSourceAsync(mapperCSource, new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperC.MapInto(string, double)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 9, OriginalCallExpression = "_validator.ValidateAsync()" }
                }
            }
        });

        // Transform MapperD: no async calls, uses Task.CompletedTask
        var mapperDTransformed = await _transformer.TransformSourceAsync(mapperDSource, new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MapperD.MapInto(string, long)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        });

        // Transform Controller: await all 4 mapper calls
        var controllerTransformed = await _transformer.TransformSourceAsync(controllerSource, new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "Controller.HandleRequest()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 24, OriginalCallExpression = @"_mapperA.MapInto(""hello"", 42)" },
                    new() { LineNumber = 25, OriginalCallExpression = @"_mapperB.MapInto(""world"", true)" },
                    new() { LineNumber = 26, OriginalCallExpression = @"_mapperC.MapInto(""foo"", 3.14)" },
                    new() { LineNumber = 27, OriginalCallExpression = @"_mapperD.MapInto(""bar"", 100L)" },
                }
            }
        });

        // Apply interface replacement to all transformed sources
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };

        var mapperBFinal = InterfaceReplacer.Transform(mapperBTransformed, mappings)!;
        var mapperAFinal = InterfaceReplacer.Transform(mapperATransformed, mappings)!;
        var mapperCFinal = InterfaceReplacer.Transform(mapperCTransformed, mappings)!;
        var mapperDFinal = InterfaceReplacer.Transform(mapperDTransformed, mappings)!;
        var controllerFinal = InterfaceReplacer.Transform(controllerTransformed, mappings)!;

        // Verify all interface replacements occurred
        mapperBFinal.Should().Contain(": IMapIntoAsync<bool, string>");
        mapperAFinal.Should().Contain(": IMapIntoAsync<int, string>");
        mapperAFinal.Should().Contain("IMapIntoAsync<bool, string> _mapperB");
        mapperCFinal.Should().Contain(": IMapIntoAsync<double, string>");
        mapperDFinal.Should().Contain(": IMapIntoAsync<long, string>");
        controllerFinal.Should().Contain("IMapIntoAsync<int, string> _mapperA");
        controllerFinal.Should().Contain("IMapIntoAsync<bool, string> _mapperB");
        controllerFinal.Should().Contain("IMapIntoAsync<double, string> _mapperC");
        controllerFinal.Should().Contain("IMapIntoAsync<long, string> _mapperD");

        // No remaining sync interface references in any file
        mapperAFinal.Should().NotContain("IMapInto<");
        mapperBFinal.Should().NotContain("IMapInto<");
        mapperCFinal.Should().NotContain("IMapInto<");
        mapperDFinal.Should().NotContain("IMapInto<");
        controllerFinal.Should().NotContain("IMapInto<");

        // Post-check: all transformed sources compile together
        AssertCompilesMultiple(
            [mapperBFinal, mapperAFinal, mapperCFinal, mapperDFinal, controllerFinal],
            "all transformed sources with IMapIntoAsync should compile together");
    }

    /// <summary>
    /// Verifies that multiple C# source files compile together without errors.
    /// </summary>
    private static void AssertCompilesMultiple(string[] sources, string because = "code should compile without errors")
    {
        var syntaxTrees = new List<SyntaxTree> { CSharpSyntaxTree.ParseText(StubTypes) };
        foreach (var source in sources)
            syntaxTrees.Add(CSharpSyntaxTree.ParseText(source));

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

    #endregion
}
