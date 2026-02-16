using System.Collections.Concurrent;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class AsyncFloodingAnalyzerTests
{
    private readonly AsyncFloodingAnalyzer _analyzer = new();

    private static CallGraph CreateCallGraph(
        Dictionary<string, MethodNode> methods,
        List<MethodCall>? calls = null,
        List<InterfaceImplementation>? interfaceImpls = null,
        List<MethodOverride>? overrides = null,
        List<GenericInstantiation>? genericInstantiations = null)
    {
        var graphId = Guid.NewGuid().ToString();
        var methodDict = new ConcurrentDictionary<string, MethodNode>(methods);
        var callBag = new ConcurrentBag<MethodCall>(calls ?? []);
        var implBag = new ConcurrentBag<InterfaceImplementation>(interfaceImpls ?? []);
        var overrideBag = new ConcurrentBag<MethodOverride>(overrides ?? []);
        var giBag = new ConcurrentBag<GenericInstantiation>(genericInstantiations ?? []);

        var graph = new CallGraph(callBag, implBag, overrideBag, giBag)
        {
            Id = graphId,
            ProjectName = "TestProject",
            Methods = methodDict
        };
        return graph;
    }

    private static MethodNode MakeMethod(string id, string name, string returnType, string graphId = "g")
        => new()
        {
            CallGraphId = graphId,
            Id = id,
            Name = name,
            ContainingType = "TestClass",
            ContainingNamespace = "TestNs",
            ReturnType = returnType,
            Parameters = [],
            FilePath = "test.cs",
            StartLine = 1,
            EndLine = 10
        };

    private static MethodCall MakeCall(string callerId, string calleeId, string graphId = "g")
        => new()
        {
            CallGraphId = graphId,
            Id = Guid.NewGuid().ToString(),
            CallerId = callerId,
            CalleeId = calleeId,
            LineNumber = 1,
            FilePath = "test.cs"
        };

    [Fact]
    public async Task FloodFromRoot_MarksRootMethod()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "DoWork", "void")
        };
        var graph = CreateCallGraph(methods);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task FloodFromRoot_TransformsVoidToTask()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Caller", "void")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m1") };
        var graph = CreateCallGraph(methods, calls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
        result.Methods["m2"].ReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task FloodFromRoot_TransformsReturnTypeToTaskOfT()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "int"),
            ["m2"] = MakeMethod("m2", "Caller", "string")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m1") };
        var graph = CreateCallGraph(methods, calls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task<int>");
        result.Methods["m2"].ReturnType.Should().Be("Task<string>");
    }

    [Fact]
    public async Task FloodFromRoot_PreservesAlreadyTaskReturnType()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "Task<int>")
        };
        var graph = CreateCallGraph(methods);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task<int>");
    }

    [Fact]
    public async Task FloodFromRoot_PreservesTaskReturnType()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "Task")
        };
        var graph = CreateCallGraph(methods);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task FloodFromRoot_DoesNotFloodUnrelatedMethods()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Unrelated", "int")
        };
        // No calls between m1 and m2
        var graph = CreateCallGraph(methods);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
        result.Methods["m2"].ReturnType.Should().Be("int");
    }

    [Fact]
    public async Task FloodFromRoot_FloodsTransitiveCallers()
    {
        // m3 -> m2 -> m1 (root)
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Middle", "bool"),
            ["m3"] = MakeMethod("m3", "Top", "string")
        };
        var calls = new List<MethodCall>
        {
            MakeCall("m2", "m1"),
            MakeCall("m3", "m2")
        };
        var graph = CreateCallGraph(methods, calls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
        result.Methods["m2"].ReturnType.Should().Be("Task<bool>");
        result.Methods["m3"].ReturnType.Should().Be("Task<string>");
    }

    [Fact]
    public async Task FloodFromRoot_FloodsThroughNonGenericInterfaceImplementation()
    {
        // m2 calls interface method m_iface; m1 implements m_iface
        // Interface IService has no generic type parameters, so return type "void"
        // cannot be adjusted via type arguments — flooding must propagate.
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Impl", "void"),
            ["m_iface"] = MakeMethod("m_iface", "DoWork", "void") with { ContainingType = "IService" },
            ["m2"] = MakeMethod("m2", "Consumer", "int")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m_iface") };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m1", InterfaceMethodId = "m_iface" }
        };
        var graph = CreateCallGraph(methods, calls, impls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
        result.Methods["m_iface"].ReturnType.Should().Be("Task", "non-generic interface method should be flooded");
        result.Methods["m2"].ReturnType.Should().Be("Task<int>", "caller of flooded interface method should be flooded");
    }

    [Fact]
    public async Task FloodFromRoot_FloodsThroughOverrides()
    {
        // m_override overrides m_base; m_caller calls m_base
        // Flooding from m_override should flood m_base (via override) and then m_caller
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_override"] = MakeMethod("m_override", "DoWork", "void"),
            ["m_base"] = MakeMethod("m_base", "DoWork", "void"),
            ["m_caller"] = MakeMethod("m_caller", "Run", "string")
        };
        var calls = new List<MethodCall> { MakeCall("m_caller", "m_base") };
        var overrides = new List<MethodOverride>
        {
            new() { CallGraphId = "g", OverridingMethodId = "m_override", BaseMethodId = "m_base" }
        };
        var graph = CreateCallGraph(methods, calls, overrides: overrides);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_override"]);

        result.Methods["m_override"].ReturnType.Should().Be("Task");
        result.Methods["m_base"].ReturnType.Should().Be("Task");
        result.Methods["m_caller"].ReturnType.Should().Be("Task<string>");
    }

    [Fact]
    public async Task FloodFromRoot_FloodsFromNonGenericInterfaceToImplementors()
    {
        // Non-generic interface: flooding from the interface method should flood all implementors
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_iface"] = MakeMethod("m_iface", "DoWork", "void") with { ContainingType = "IService" },
            ["m_impl1"] = MakeMethod("m_impl1", "DoWork", "void"),
            ["m_impl2"] = MakeMethod("m_impl2", "DoWork", "void")
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl1", InterfaceMethodId = "m_iface" },
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl2", InterfaceMethodId = "m_iface" }
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_iface"]);

        result.Methods["m_iface"].ReturnType.Should().Be("Task");
        result.Methods["m_impl1"].ReturnType.Should().Be("Task", "implementors of non-generic interface should be flooded");
        result.Methods["m_impl2"].ReturnType.Should().Be("Task", "implementors of non-generic interface should be flooded");
    }

    [Fact]
    public async Task FloodFromRoot_DoesNotFloodFromGenericInterfaceToImplementors()
    {
        // Generic interface where return type is a type parameter:
        // flooding the generic interface should NOT flood through GenericInstantiation to instantiations or implementations
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_iface_generic"] = MakeMethod("m_iface_generic", "Map", "TDestination?") with { ContainingType = "IMapper<TSource, TDestination>" },
            ["m_iface_foo"] = MakeMethod("m_iface_foo", "Map", "FooOutput?") with { ContainingType = "IMapper<FooInput, FooOutput>" },
            ["m_iface_bar"] = MakeMethod("m_iface_bar", "Map", "BarOutput?") with { ContainingType = "IMapper<BarInput, BarOutput>" },
            ["m_impl1"] = MakeMethod("m_impl1", "Map", "FooOutput?") with { ContainingType = "FooMapper" },
            ["m_impl2"] = MakeMethod("m_impl2", "Map", "BarOutput?") with { ContainingType = "BarMapper" }
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl1", InterfaceMethodId = "m_iface_foo" },
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl2", InterfaceMethodId = "m_iface_bar" }
        };
        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_foo", GenericMethodId = "m_iface_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_bar", GenericMethodId = "m_iface_generic" }
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls, genericInstantiations: genericInstantiations);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_iface_generic"]);

        result.Methods["m_iface_generic"].ReturnType.Should().Be("Task<TDestination?>",
            "the generic interface method itself is flooded since it was a root");
        result.Methods["m_iface_foo"].ReturnType.Should().Be("FooOutput?",
            "instantiated nodes should not be flooded when generic return type is a type parameter");
        result.Methods["m_iface_bar"].ReturnType.Should().Be("BarOutput?",
            "instantiated nodes should not be flooded when generic return type is a type parameter");
        result.Methods["m_impl1"].ReturnType.Should().Be("FooOutput?",
            "implementors should not be flooded when return type is a generic type parameter");
        result.Methods["m_impl2"].ReturnType.Should().Be("BarOutput?",
            "implementors should not be flooded when return type is a generic type parameter");
    }

    [Fact]
    public async Task FloodFromRoot_FloodsFromBaseToOverrides()
    {
        // Flooding from base method should also flood overriding methods
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_base"] = MakeMethod("m_base", "DoWork", "int"),
            ["m_child"] = MakeMethod("m_child", "DoWork", "int")
        };
        var overrides = new List<MethodOverride>
        {
            new() { CallGraphId = "g", OverridingMethodId = "m_child", BaseMethodId = "m_base" }
        };
        var graph = CreateCallGraph(methods, overrides: overrides);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_base"]);

        result.Methods["m_base"].ReturnType.Should().Be("Task<int>");
        result.Methods["m_child"].ReturnType.Should().Be("Task<int>");
    }

    [Fact]
    public async Task FloodFromRoot_HandlesMultipleRoots()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root1", "void"),
            ["m2"] = MakeMethod("m2", "Root2", "int"),
            ["m3"] = MakeMethod("m3", "CallerOf1", "string"),
            ["m4"] = MakeMethod("m4", "CallerOf2", "bool")
        };
        var calls = new List<MethodCall>
        {
            MakeCall("m3", "m1"),
            MakeCall("m4", "m2")
        };
        var graph = CreateCallGraph(methods, calls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1", "m2"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
        result.Methods["m2"].ReturnType.Should().Be("Task<int>");
        result.Methods["m3"].ReturnType.Should().Be("Task<string>");
        result.Methods["m4"].ReturnType.Should().Be("Task<bool>");
    }

    [Fact]
    public async Task FloodFromRoot_HandlesCyclicCalls()
    {
        // m1 -> m2 -> m1 (cycle)
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "A", "void"),
            ["m2"] = MakeMethod("m2", "B", "void")
        };
        var calls = new List<MethodCall>
        {
            MakeCall("m1", "m2"),
            MakeCall("m2", "m1")
        };
        var graph = CreateCallGraph(methods, calls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Methods["m1"].ReturnType.Should().Be("Task");
        result.Methods["m2"].ReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task FloodFromRoot_CreatesNewCallGraphWithDifferentId()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void")
        };
        var graph = CreateCallGraph(methods);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Id.Should().NotBe(graph.Id);
        result.ProjectName.Should().Be("TestProject-async");
    }

    [Fact]
    public async Task FloodFromRoot_PreservesCallRelationships()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Caller", "void")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m1") };
        var graph = CreateCallGraph(methods, calls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.Calls.Should().HaveCount(1);
        var call = result.Calls.First();
        call.CallerId.Should().Be("m2");
        call.CalleeId.Should().Be("m1");
        call.CallGraphId.Should().Be(result.Id);
    }

    [Fact]
    public async Task FloodFromRoot_PreservesInterfaceImplementations()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Impl", "void"),
            ["m_iface"] = MakeMethod("m_iface", "DoWork", "void")
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m1", InterfaceMethodId = "m_iface" }
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);

        result.InterfaceImplementations.Should().HaveCount(1);
        var impl = result.InterfaceImplementations.First();
        impl.ImplementingMethodId.Should().Be("m1");
        impl.InterfaceMethodId.Should().Be("m_iface");
        impl.CallGraphId.Should().Be(result.Id);
    }

    [Fact]
    public async Task FloodFromRoot_SupportsCancellation()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Caller", "void")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m1") };
        var graph = CreateCallGraph(methods, calls);

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _analyzer.AnalyzeFloodingAsync(graph, ["m1"], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FloodFromRoot_ReportsProgress()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Caller", "void")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m1") };
        var graph = CreateCallGraph(methods, calls);

        var progressCalls = new List<(string method, int current, int total)>();
        await _analyzer.AnalyzeFloodingAsync(graph, ["m1"], (method, current, total) =>
        {
            progressCalls.Add((method, current, total));
        });

        progressCalls.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("void", "Task")]
    [InlineData("int", "Task<int>")]
    [InlineData("string", "Task<string>")]
    [InlineData("List<int>", "Task<List<int>>")]
    [InlineData("Task", "Task")]
    [InlineData("Task<int>", "Task<int>")]
    [InlineData("ValueTask", "ValueTask")]
    [InlineData("ValueTask<bool>", "ValueTask<bool>")]
    public void TransformReturnType_TransformsCorrectly(string input, string expected)
    {
        AsyncFloodingAnalyzer.TransformReturnType(input).Should().Be(expected);
    }

    [Fact]
    public async Task GetTransformationInfo_ReturnsFloodedMethods()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Root", "void"),
            ["m2"] = MakeMethod("m2", "Caller", "int")
        };
        var calls = new List<MethodCall> { MakeCall("m2", "m1") };
        var graph = CreateCallGraph(methods, calls);

        var asyncGraph = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);
        var infos = await _analyzer.GetTransformationInfoAsync(asyncGraph);

        infos.Should().HaveCount(2);
        infos.Should().Contain(i => i.MethodId == "m1" && i.NewReturnType == "Task");
        infos.Should().Contain(i => i.MethodId == "m2" && i.NewReturnType == "Task<int>");
    }

    [Fact]
    public async Task GetTransformationInfo_IncludesInterfaceMethodIds()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m1"] = MakeMethod("m1", "Impl", "void"),
            ["m_iface"] = MakeMethod("m_iface", "DoWork", "void")
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m1", InterfaceMethodId = "m_iface" }
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls);

        var asyncGraph = await _analyzer.AnalyzeFloodingAsync(graph, ["m1"]);
        var infos = await _analyzer.GetTransformationInfoAsync(asyncGraph);

        var implInfo = infos.Single(i => i.MethodId == "m1");
        implInfo.ImplementsInterfaceMethods.Should().Contain("m_iface");
    }

    [Theory]
    [InlineData("IService", new string[0])]
    [InlineData("IMapper<TSource, TDestination>", new[] { "TSource", "TDestination" })]
    [InlineData("IConverter<TIn, TOut>", new[] { "TIn", "TOut" })]
    [InlineData("IHandler<TRequest, IEnumerable<TResponse>>", new[] { "TRequest", "IEnumerable<TResponse>" })]
    [InlineData("ISimple<T>", new[] { "T" })]
    public void ParseGenericTypeParameters_ExtractsCorrectly(string containingType, string[] expected)
    {
        AsyncFloodingAnalyzer.ParseGenericTypeParameters(containingType).Should().BeEquivalentTo(expected);
    }

    [Fact]
    public async Task FloodFromRoot_GenericInterfaceImpl_DoesNotFloodInterfaceOrSiblingImpls()
    {
        // Scenario: IMapper<TSource, TDestination> with Map(TSource) -> TDestination
        // Two implementations: FooMapper and BarMapper both implement IMapper.Map
        // When FooMapper.Map is flooded (e.g. it calls a task wrapper):
        //   - FooMapper.Map return type changes: TDestination -> Task<TDestination>
        //   - FooMapper changes its interface from IMapper<X, Y> to IMapper<X, Task<Y>>
        //   - IMapper.Map itself stays unchanged
        //   - BarMapper.Map stays unchanged
        //   - Callers of IMapper.Map stay unchanged

        var methods = new Dictionary<string, MethodNode>
        {
            // Generic interface method: IMapper<TSource, TDestination>.Map(TSource) -> TDestination?
            ["imap_map_generic"] = MakeMethod("imap_map_generic", "Map", "TDestination?") with { ContainingType = "IMapper<TSource, TDestination>" },

            // Instantiated interface methods
            ["imap_map_foo"] = MakeMethod("imap_map_foo", "Map", "FooOutput?") with { ContainingType = "IMapper<FooInput, FooOutput>" },
            ["imap_map_bar"] = MakeMethod("imap_map_bar", "Map", "BarOutput?") with { ContainingType = "IMapper<BarInput, BarOutput>" },

            // FooMapper : IMapper<FooInput, FooOutput>
            ["foo_map"] = MakeMethod("foo_map", "Map", "FooOutput?") with { ContainingType = "FooMapper" },

            // BarMapper : IMapper<BarInput, BarOutput>
            ["bar_map"] = MakeMethod("bar_map", "Map", "BarOutput?") with { ContainingType = "BarMapper" },

            // A consumer that calls IMapper.Map via the instantiated interface
            ["consumer"] = MakeMethod("consumer", "ProcessItem", "void") with { ContainingType = "ItemProcessor" },

            // The task wrapper root that FooMapper.Map calls
            ["task_wrapper"] = MakeMethod("task_wrapper", "RunSync", "FooOutput") with { ContainingType = "SyncHelper" },
        };

        var calls = new List<MethodCall>
        {
            // FooMapper.Map calls the task wrapper (this is why it needs to become async)
            MakeCall("foo_map", "task_wrapper"),
            // Consumer calls the instantiated interface method
            MakeCall("consumer", "imap_map_foo"),
        };

        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "foo_map", InterfaceMethodId = "imap_map_foo" },
            new() { CallGraphId = "g", ImplementingMethodId = "bar_map", InterfaceMethodId = "imap_map_bar" },
        };

        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_map_foo", GenericMethodId = "imap_map_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "imap_map_bar", GenericMethodId = "imap_map_generic" },
        };

        var graph = CreateCallGraph(methods, calls, impls, genericInstantiations: genericInstantiations);

        // Flood from the task wrapper (the root async source)
        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["task_wrapper"]);

        // Task wrapper itself: already has a non-Task return type, so it gets wrapped
        result.Methods["task_wrapper"].ReturnType.Should().Be("Task<FooOutput>");

        // FooMapper.Map: flooded because it calls the task wrapper
        result.Methods["foo_map"].ReturnType.Should().Be("Task<FooOutput?>",
            "FooMapper.Map is a caller of the task wrapper and must become async");

        // Instantiated interface for Foo: flooded via InterfaceImplementation from foo_map
        result.Methods["imap_map_foo"].ReturnType.Should().Be("Task<FooOutput?>",
            "the instantiated interface method is flooded via interface implementation");

        // Generic interface method: NOT flooded — return type is a type parameter
        result.Methods["imap_map_generic"].ReturnType.Should().Be("TDestination?",
            "the generic interface method should not change; the return type is a type parameter");

        // Instantiated interface for Bar: NOT flooded — not connected to the flooding
        result.Methods["imap_map_bar"].ReturnType.Should().Be("BarOutput?",
            "sibling instantiation should not be flooded");

        // BarMapper.Map: NOT flooded — it's a sibling implementation, unrelated to the flooding
        result.Methods["bar_map"].ReturnType.Should().Be("BarOutput?",
            "sibling implementations of the same interface should not be flooded");

        // Consumer: flooded — it calls the instantiated interface method which IS flooded
        result.Methods["consumer"].ReturnType.Should().Be("Task",
            "callers of the flooded instantiated interface method should be flooded");
    }

    [Fact]
    public async Task FloodFromRoot_FloodsThroughGenericInstantiation_WhenReturnTypeIsNotTypeParameter()
    {
        // Generic interface where return type is NOT a type parameter (e.g. void):
        // flooding from instantiated → generic should propagate
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_iface_generic"] = MakeMethod("m_iface_generic", "Execute", "void") with { ContainingType = "IHandler<TRequest>" },
            ["m_iface_foo"] = MakeMethod("m_iface_foo", "Execute", "void") with { ContainingType = "IHandler<FooRequest>" },
            ["m_iface_bar"] = MakeMethod("m_iface_bar", "Execute", "void") with { ContainingType = "IHandler<BarRequest>" },
            ["m_impl_foo"] = MakeMethod("m_impl_foo", "Execute", "void") with { ContainingType = "FooHandler" },
            ["m_impl_bar"] = MakeMethod("m_impl_bar", "Execute", "void") with { ContainingType = "BarHandler" },
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl_foo", InterfaceMethodId = "m_iface_foo" },
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl_bar", InterfaceMethodId = "m_iface_bar" },
        };
        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_foo", GenericMethodId = "m_iface_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_bar", GenericMethodId = "m_iface_generic" },
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls, genericInstantiations: genericInstantiations);

        // Flood from FooHandler implementation
        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_impl_foo"]);

        result.Methods["m_impl_foo"].ReturnType.Should().Be("Task");
        result.Methods["m_iface_foo"].ReturnType.Should().Be("Task",
            "instantiated interface should be flooded via InterfaceImplementation");
        result.Methods["m_iface_generic"].ReturnType.Should().Be("Task",
            "generic interface should be flooded since return type (void) is not a type parameter");
        result.Methods["m_iface_bar"].ReturnType.Should().Be("Task",
            "sibling instantiation should be flooded from generic → instantiated");
        result.Methods["m_impl_bar"].ReturnType.Should().Be("Task",
            "sibling implementation should be flooded from instantiated interface");
    }

    [Fact]
    public async Task FloodFromRoot_BlockedGenericMethodIds_PreventsInstantiationToGenericPropagation()
    {
        // Same setup as FloodFromRoot_FloodsThroughGenericInstantiation_WhenReturnTypeIsNotTypeParameter
        // but with the generic method ID blocked — flooding should NOT cross the instantiation↔generic boundary
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_iface_generic"] = MakeMethod("m_iface_generic", "Execute", "void") with { ContainingType = "IHandler<TRequest>" },
            ["m_iface_foo"] = MakeMethod("m_iface_foo", "Execute", "void") with { ContainingType = "IHandler<FooRequest>" },
            ["m_iface_bar"] = MakeMethod("m_iface_bar", "Execute", "void") with { ContainingType = "IHandler<BarRequest>" },
            ["m_impl_foo"] = MakeMethod("m_impl_foo", "Execute", "void") with { ContainingType = "FooHandler" },
            ["m_impl_bar"] = MakeMethod("m_impl_bar", "Execute", "void") with { ContainingType = "BarHandler" },
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl_foo", InterfaceMethodId = "m_iface_foo" },
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl_bar", InterfaceMethodId = "m_iface_bar" },
        };
        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_foo", GenericMethodId = "m_iface_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_bar", GenericMethodId = "m_iface_generic" },
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls, genericInstantiations: genericInstantiations);

        // Block the generic method — prevents instantiation↔generic traversal
        var blocked = new HashSet<string> { "m_iface_generic" };
        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_impl_foo"], blocked);

        result.Methods["m_impl_foo"].ReturnType.Should().Be("Task",
            "root method is flooded");
        result.Methods["m_iface_foo"].ReturnType.Should().Be("Task",
            "instantiated interface is flooded via InterfaceImplementation");
        result.Methods["m_iface_generic"].ReturnType.Should().Be("void",
            "generic interface should NOT be flooded when blocked");
        result.Methods["m_iface_bar"].ReturnType.Should().Be("void",
            "sibling instantiation should NOT be flooded when generic is blocked");
        result.Methods["m_impl_bar"].ReturnType.Should().Be("void",
            "sibling implementation should NOT be flooded when generic is blocked");
    }

    [Fact]
    public async Task FloodFromRoot_BlockedGenericMethodIds_DebugVersion_PreventsInstantiationToGenericPropagation()
    {
        // Same test as above but using the debug (WithDebug) overload
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_iface_generic"] = MakeMethod("m_iface_generic", "Execute", "void") with { ContainingType = "IHandler<TRequest>" },
            ["m_iface_foo"] = MakeMethod("m_iface_foo", "Execute", "void") with { ContainingType = "IHandler<FooRequest>" },
            ["m_iface_bar"] = MakeMethod("m_iface_bar", "Execute", "void") with { ContainingType = "IHandler<BarRequest>" },
            ["m_impl_foo"] = MakeMethod("m_impl_foo", "Execute", "void") with { ContainingType = "FooHandler" },
            ["m_impl_bar"] = MakeMethod("m_impl_bar", "Execute", "void") with { ContainingType = "BarHandler" },
        };
        var impls = new List<InterfaceImplementation>
        {
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl_foo", InterfaceMethodId = "m_iface_foo" },
            new() { CallGraphId = "g", ImplementingMethodId = "m_impl_bar", InterfaceMethodId = "m_iface_bar" },
        };
        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_foo", GenericMethodId = "m_iface_generic" },
            new() { CallGraphId = "g", InstantiatedMethodId = "m_iface_bar", GenericMethodId = "m_iface_generic" },
        };
        var graph = CreateCallGraph(methods, interfaceImpls: impls, genericInstantiations: genericInstantiations);

        var blocked = new HashSet<string> { "m_iface_generic" };
        var (result, floodingResult) = await _analyzer.AnalyzeFloodingWithDebugAsync(graph, ["m_impl_foo"], blocked);

        result.Methods["m_impl_foo"].ReturnType.Should().Be("Task");
        result.Methods["m_iface_foo"].ReturnType.Should().Be("Task");
        result.Methods["m_iface_generic"].ReturnType.Should().Be("void",
            "generic interface should NOT be flooded when blocked (debug version)");
        result.Methods["m_iface_bar"].ReturnType.Should().Be("void",
            "sibling instantiation should NOT be flooded when blocked (debug version)");
        result.Methods["m_impl_bar"].ReturnType.Should().Be("void",
            "sibling implementation should NOT be flooded when blocked (debug version)");

        // Verify debug info doesn't include blocked methods
        floodingResult.FloodedMethods.Should().ContainKey("m_impl_foo");
        floodingResult.FloodedMethods.Should().ContainKey("m_iface_foo");
        floodingResult.FloodedMethods.Should().NotContainKey("m_iface_generic");
        floodingResult.FloodedMethods.Should().NotContainKey("m_iface_bar");
    }

    [Fact]
    public async Task FloodFromRoot_PreservesGenericInstantiations()
    {
        var methods = new Dictionary<string, MethodNode>
        {
            ["m_generic"] = MakeMethod("m_generic", "Map", "TDestination") with { ContainingType = "IMapper<TSource, TDestination>" },
            ["m_inst"] = MakeMethod("m_inst", "Map", "Foo") with { ContainingType = "IMapper<Bar, Foo>" },
        };
        var genericInstantiations = new List<GenericInstantiation>
        {
            new() { CallGraphId = "g", InstantiatedMethodId = "m_inst", GenericMethodId = "m_generic" },
        };
        var graph = CreateCallGraph(methods, genericInstantiations: genericInstantiations);

        var result = await _analyzer.AnalyzeFloodingAsync(graph, ["m_inst"]);

        result.GenericInstantiations.Should().HaveCount(1);
        var gi = result.GenericInstantiations.First();
        gi.InstantiatedMethodId.Should().Be("m_inst");
        gi.GenericMethodId.Should().Be("m_generic");
        gi.CallGraphId.Should().Be(result.Id);
    }
}
