using System.Collections.Concurrent;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class ProblematicInterfaceAnalyzerTests
{
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
        var genBag = new ConcurrentBag<GenericInstantiation>(genericInstantiations ?? []);

        return new CallGraph(callBag, implBag, genericInstantiations: genBag)
        {
            Id = graphId,
            ProjectName = "TestProject",
            Methods = methodDict
        };
    }

    private static MethodNode MakeMethod(string id, string name, string returnType,
        string containingType = "TestClass", string filePath = "Test.cs",
        string containingNamespace = "TestNamespace", bool isReturnTypeParameter = false)
        => new()
        {
            CallGraphId = "g",
            Id = id,
            Name = name,
            ContainingType = containingType,
            ContainingNamespace = containingNamespace,
            ReturnType = returnType,
            Parameters = new List<string>(),
            FilePath = filePath,
            StartLine = 1,
            EndLine = 10,
            IsReturnTypeParameter = isReturnTypeParameter
        };

    #region FindExistingAsyncInterface

    [Fact]
    public void FindExistingAsyncInterface_NonGeneric_FindsAsyncSuffix()
    {
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>
        {
            ["IRepositoryAsync.Get"] = MakeMethod("IRepositoryAsync.Get", "Get", "Task<string>", "IRepositoryAsync"),
        });

        var methods = new List<ProblematicMethod>
        {
            new("IRepository.Get", MakeMethod("IRepository.Get", "Get", "string", "IRepository", "external"),
                MakeMethod("Repo.Get", "Get", "string"), MakeMethod("Repo.Get", "Get", "Task<string>"))
        };

        var result = ProblematicInterfaceAnalyzer.FindExistingAsyncInterface(callGraph, "IRepository", methods);
        result.Should().NotBeNull();
        result!.Value.TypeName.Should().Be("IRepositoryAsync");
    }

    [Fact]
    public void FindExistingAsyncInterface_Generic_FindsMatch()
    {
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>
        {
            ["IMapIntoAsync<TDest, TSource>.Map"] = MakeMethod(
                "IMapIntoAsync<TDest, TSource>.Map", "Map", "Task<TDest>", "IMapIntoAsync<TDest, TSource>"),
        });

        var methods = new List<ProblematicMethod>
        {
            new("IMapInto<TDest, TSource>.Map",
                MakeMethod("IMapInto<TDest, TSource>.Map", "Map", "TDest", "IMapInto<TDest, TSource>", "external"),
                MakeMethod("Mapper.Map", "Map", "TDest"),
                MakeMethod("Mapper.Map", "Map", "Task<TDest>"))
        };

        var result = ProblematicInterfaceAnalyzer.FindExistingAsyncInterface(callGraph, "IMapInto<TDest, TSource>", methods);
        result.Should().NotBeNull();
        result!.Value.TypeName.Should().Be("IMapIntoAsync<TDest, TSource>");
    }

    [Fact]
    public void FindExistingAsyncInterface_QualifiedGeneric_FindsMatch()
    {
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>
        {
            ["Some.Ns.IFooAsync<T>.Do"] = MakeMethod(
                "Some.Ns.IFooAsync<T>.Do", "Do", "Task<bool>", "Some.Ns.IFooAsync<T>"),
        });

        var methods = new List<ProblematicMethod>
        {
            new("Some.Ns.IFoo<T>.Do",
                MakeMethod("Some.Ns.IFoo<T>.Do", "Do", "bool", "Some.Ns.IFoo<T>", "external"),
                MakeMethod("Impl.Do", "Do", "bool"),
                MakeMethod("Impl.Do", "Do", "Task<bool>"))
        };

        var result = ProblematicInterfaceAnalyzer.FindExistingAsyncInterface(callGraph, "Some.Ns.IFoo<T>", methods);
        result.Should().NotBeNull();
        result!.Value.TypeName.Should().Be("Some.Ns.IFooAsync<T>");
    }

    [Fact]
    public void FindExistingAsyncInterface_NoMatch_ReturnsNull()
    {
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>());

        var methods = new List<ProblematicMethod>
        {
            new("IRepository.Get",
                MakeMethod("IRepository.Get", "Get", "string", "IRepository", "external"),
                MakeMethod("Repo.Get", "Get", "string"),
                MakeMethod("Repo.Get", "Get", "Task<string>"))
        };

        var result = ProblematicInterfaceAnalyzer.FindExistingAsyncInterface(callGraph, "IRepository", methods);
        result.Should().BeNull();
    }

    [Fact]
    public void FindExistingAsyncInterface_SignatureMismatch_ReturnsNull()
    {
        // Async interface exists but has wrong return type
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>
        {
            ["IRepositoryAsync.Get"] = MakeMethod("IRepositoryAsync.Get", "Get", "Task<int>", "IRepositoryAsync"),
        });

        var methods = new List<ProblematicMethod>
        {
            new("IRepository.Get",
                MakeMethod("IRepository.Get", "Get", "string", "IRepository", "external"),
                MakeMethod("Repo.Get", "Get", "string"),
                MakeMethod("Repo.Get", "Get", "Task<string>"))
        };

        var result = ProblematicInterfaceAnalyzer.FindExistingAsyncInterface(callGraph, "IRepository", methods);
        result.Should().BeNull();
    }

    #endregion

    #region DetectProblematicInterfaces

    [Fact]
    public void DetectProblematicInterfaces_DetectsExternalInterfaceWithChangedReturnType()
    {
        var ifaceMethod = MakeMethod("IRepo.Get", "Get", "string", "IRepo", "external");
        var implMethod = MakeMethod("Repo.Get", "Get", "string", "Repo");

        var syncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IRepo.Get"] = ifaceMethod,
                ["Repo.Get"] = implMethod,
            },
            interfaceImpls: new List<InterfaceImplementation>
            {
                new() { CallGraphId = "g", ImplementingMethodId = "Repo.Get", InterfaceMethodId = "IRepo.Get" }
            });

        var asyncImplMethod = MakeMethod("Repo.Get", "Get", "Task<string>", "Repo");
        var asyncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IRepo.Get"] = ifaceMethod,
                ["Repo.Get"] = asyncImplMethod,
            });

        var result = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(syncGraph, asyncGraph);

        result.Should().ContainKey("IRepo");
        result["IRepo"].Should().HaveCount(1);
        result["IRepo"][0].InterfaceMethodId.Should().Be("IRepo.Get");
    }

    [Fact]
    public void DetectProblematicInterfaces_SkipsInternalInterfaces()
    {
        var ifaceMethod = MakeMethod("IRepo.Get", "Get", "string", "IRepo", "src/IRepo.cs"); // internal
        var implMethod = MakeMethod("Repo.Get", "Get", "string", "Repo");

        var syncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IRepo.Get"] = ifaceMethod,
                ["Repo.Get"] = implMethod,
            },
            interfaceImpls: new List<InterfaceImplementation>
            {
                new() { CallGraphId = "g", ImplementingMethodId = "Repo.Get", InterfaceMethodId = "IRepo.Get" }
            });

        var asyncImplMethod = MakeMethod("Repo.Get", "Get", "Task<string>", "Repo");
        var asyncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IRepo.Get"] = ifaceMethod,
                ["Repo.Get"] = asyncImplMethod,
            });

        var result = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(syncGraph, asyncGraph);
        result.Should().BeEmpty();
    }

    [Fact]
    public void DetectProblematicInterfaces_SkipsReturnTypeParameters()
    {
        var ifaceMethod = MakeMethod("IMapper.Map", "Map", "TResult", "IMapper", "external", isReturnTypeParameter: true);
        var implMethod = MakeMethod("Mapper.Map", "Map", "TResult", "Mapper");

        var syncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IMapper.Map"] = ifaceMethod,
                ["Mapper.Map"] = implMethod,
            },
            interfaceImpls: new List<InterfaceImplementation>
            {
                new() { CallGraphId = "g", ImplementingMethodId = "Mapper.Map", InterfaceMethodId = "IMapper.Map" }
            });

        var asyncImplMethod = MakeMethod("Mapper.Map", "Map", "Task<TResult>", "Mapper");
        var asyncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IMapper.Map"] = ifaceMethod,
                ["Mapper.Map"] = asyncImplMethod,
            });

        var result = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(syncGraph, asyncGraph);
        result.Should().BeEmpty();
    }

    [Fact]
    public void DetectProblematicInterfaces_SkipsGenericInstantiationWithReturnTypeParameter()
    {
        // Generic definition: IMapper<TSource, TDestination>.Map returns TDestination (a type param)
        var genericIfaceMethod = MakeMethod(
            "IMapper<TSource, TDestination>.Map", "Map", "TDestination",
            "IMapper<TSource, TDestination>", "external", isReturnTypeParameter: true);

        // Instantiated: IMapper<Foo, Bar>.Map returns Bar (concrete, IsReturnTypeParameter = false)
        var instantiatedIfaceMethod = MakeMethod(
            "IMapper<Foo, Bar>.Map(Foo)", "Map", "Bar?",
            "IMapper<Foo, Bar>", "external");

        var implMethod = MakeMethod("MyMapper.Map", "Map", "Bar?", "MyMapper");

        var syncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IMapper<TSource, TDestination>.Map"] = genericIfaceMethod,
                ["IMapper<Foo, Bar>.Map(Foo)"] = instantiatedIfaceMethod,
                ["MyMapper.Map"] = implMethod,
            },
            interfaceImpls: new List<InterfaceImplementation>
            {
                new() { CallGraphId = "g", ImplementingMethodId = "MyMapper.Map", InterfaceMethodId = "IMapper<Foo, Bar>.Map(Foo)" }
            },
            genericInstantiations: new List<GenericInstantiation>
            {
                new() { CallGraphId = "g", InstantiatedMethodId = "IMapper<Foo, Bar>.Map(Foo)", GenericMethodId = "IMapper<TSource, TDestination>.Map" }
            });

        var asyncImplMethod = MakeMethod("MyMapper.Map", "Map", "Task<Bar?>", "MyMapper");
        var asyncGraph = CreateCallGraph(
            new Dictionary<string, MethodNode>
            {
                ["IMapper<TSource, TDestination>.Map"] = genericIfaceMethod,
                ["IMapper<Foo, Bar>.Map(Foo)"] = instantiatedIfaceMethod,
                ["MyMapper.Map"] = asyncImplMethod,
            });

        var result = ProblematicInterfaceAnalyzer.DetectProblematicInterfaces(syncGraph, asyncGraph);
        result.Should().BeEmpty("generic interface with covariant return type parameter is not problematic");
    }

    #endregion

    #region GetNamespaceFromCallGraph

    [Fact]
    public void GetNamespaceFromCallGraph_ReturnsNamespace()
    {
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>
        {
            ["IFoo.Bar"] = MakeMethod("IFoo.Bar", "Bar", "void", "IFoo", containingNamespace: "My.Namespace"),
        });

        var result = ProblematicInterfaceAnalyzer.GetNamespaceFromCallGraph(callGraph, "IFoo");
        result.Should().Be("My.Namespace");
    }

    [Fact]
    public void GetNamespaceFromCallGraph_NotFound_ReturnsNull()
    {
        var callGraph = CreateCallGraph(new Dictionary<string, MethodNode>());
        var result = ProblematicInterfaceAnalyzer.GetNamespaceFromCallGraph(callGraph, "IFoo");
        result.Should().BeNull();
    }

    #endregion
}
