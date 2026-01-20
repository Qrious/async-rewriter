using AsyncRewriter.Analyzer;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.IntegrationTests;

public class InterfaceImplementationTests
{
    private readonly CallGraphAnalyzer _callGraphAnalyzer;
    private readonly AsyncFloodingAnalyzer _floodingAnalyzer;
    private readonly AsyncTransformer _transformer;

    public InterfaceImplementationTests()
    {
        _callGraphAnalyzer = new CallGraphAnalyzer();
        _floodingAnalyzer = new AsyncFloodingAnalyzer();
        _transformer = new AsyncTransformer(_floodingAnalyzer);
    }

    [Fact]
    public async Task InterfaceImplementation_CallInMethodThatIsPartOfInterface_TransformsCorrectly()
    {
        // Arrange
        var serviceSource = @"
using System;

namespace InterfaceImplementation
{
    public interface IService
    {
        string GetData();
        void ProcessData(string data);
    }

    public class ServiceImplementation : IService
    {
        public string GetData()
        {
            return FetchFromDatabase();
        }

        public void ProcessData(string data)
        {
            SaveToDatabase(data);
        }

        private string FetchFromDatabase()
        {
            Console.WriteLine(""Fetching from database..."");
            return ""data"";
        }

        private void SaveToDatabase(string data)
        {
            Console.WriteLine($""Saving {data} to database..."");
        }
    }
}";

        // Act - Analyze call graph
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(serviceSource);

        // Assert - Verify call graph structure
        callGraph.Methods.Should().HaveCountGreaterThan(0);

        // Find the FetchFromDatabase method as root
        var fetchMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchFromDatabase");
        fetchMethod.Should().NotBeNull();

        // Act - Perform async flooding
        var rootMethods = new HashSet<string> { fetchMethod!.Id };
        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - GetData should be flooded because it calls FetchFromDatabase
        var getDataMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetData");
        getDataMethod.Should().NotBeNull();
        getDataMethod!.RequiresAsyncTransformation.Should().BeTrue();
        getDataMethod.AsyncReturnType.Should().Be("Task<string>");

        // Act - Transform the code
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(serviceSource, transformations.ToList());

        // Assert - Verify transformations
        // GetData has a single call to FetchFromDatabase, so it directly returns the task
        transformedSource.Should().Contain("Task<string> GetData()");
        transformedSource.Should().Contain("return FetchFromDatabase()");
        transformedSource.Should().NotContain("async Task<string> GetData()");
        // FetchFromDatabase has no async calls, so it uses Task.FromResult
        transformedSource.Should().Contain("Task<string> FetchFromDatabase()");
        transformedSource.Should().Contain("Task.FromResult");
        transformedSource.Should().NotContain("async Task<string> FetchFromDatabase()");
    }

    [Fact]
    public async Task InterfaceImplementation_ControllerCallsInterfaceMethod_Floods()
    {
        // Arrange
        var fullSource = @"
using System;

namespace InterfaceImplementation
{
    public interface IService
    {
        string GetData();
    }

    public class ServiceImplementation : IService
    {
        public string GetData()
        {
            return FetchFromDatabase();
        }

        private string FetchFromDatabase()
        {
            Console.WriteLine(""Fetching..."");
            return ""data"";
        }
    }

    public class Controller
    {
        private readonly IService _service;

        public Controller(IService service)
        {
            _service = service;
        }

        public void HandleRequest()
        {
            var data = _service.GetData();
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(fullSource);

        var fetchMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "FetchFromDatabase");
        var rootMethods = new HashSet<string> { fetchMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        var handleRequestMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "HandleRequest");
        handleRequestMethod.Should().NotBeNull();
        handleRequestMethod!.RequiresAsyncTransformation.Should().BeTrue();

        var getDataMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "GetData");
        getDataMethod.Should().NotBeNull();
        getDataMethod!.RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task InterfaceImplementation_MultipleInterfaceMethods_TransformsIndependently()
    {
        // Arrange
        var source = @"
using System;

namespace InterfaceImplementation
{
    public interface IService
    {
        string GetData();
        void ProcessData(string data);
    }

    public class ServiceImplementation : IService
    {
        public string GetData()
        {
            return FetchFromDatabase();
        }

        public void ProcessData(string data)
        {
            Console.WriteLine(data);
        }

        private string FetchFromDatabase()
        {
            Console.WriteLine(""Fetching..."");
            return ""data"";
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);
        var fetchMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "FetchFromDatabase");
        var rootMethods = new HashSet<string> { fetchMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert - GetData has a single call to FetchFromDatabase, so it directly returns the task
        transformedSource.Should().Contain("Task<string> GetData()");
        transformedSource.Should().Contain("return FetchFromDatabase()");
        transformedSource.Should().NotContain("async Task<string> GetData()");

        // FetchFromDatabase has no async calls, so it uses Task.FromResult
        transformedSource.Should().Contain("Task<string> FetchFromDatabase()");
        transformedSource.Should().Contain("Task.FromResult");

        // ProcessData should NOT be transformed (it doesn't call async methods)
        transformedSource.Should().Contain("void ProcessData(string data)");
        transformedSource.Should().NotContain("async Task ProcessData");
    }

    [Fact]
    public async Task GenericInterface_WithCovariantReturnType_TransformsBaseTypeArgumentNotInterface()
    {
        // Arrange - Generic interface with covariant (out) type parameter
        var source = @"
using System;

namespace GenericInterfaceTest
{
    public interface IMapper<in TSource, out TResult>
    {
        TResult Map(TSource source);
    }

    public class A { public string Name { get; set; } }
    public class B { public string Value { get; set; } }

    public class ABMapper : IMapper<A, B>
    {
        public B Map(A source)
        {
            return CreateB(source);
        }

        private B CreateB(A source)
        {
            Console.WriteLine(""Creating B from A"");
            return new B { Value = source.Name };
        }
    }

    public class OtherMapper : IMapper<A, B>
    {
        public B Map(A source)
        {
            return new B { Value = ""other"" };
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Mark CreateB as the async root (this should make ABMapper.Map async)
        var createBMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "CreateB");
        createBMethod.Should().NotBeNull();

        var rootMethods = new HashSet<string> { createBMethod!.Id };
        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - The interface method should NOT be marked for transformation
        var interfaceMapMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Map" && m.IsInterfaceMethod);
        interfaceMapMethod.Should().NotBeNull();
        interfaceMapMethod!.RequiresAsyncTransformation.Should().BeFalse(
            "interface methods with covariant type parameter returns should not be transformed");
        interfaceMapMethod.IsReturnTypeParameter.Should().BeTrue(
            "return type should be detected as a type parameter");

        // Assert - ABMapper.Map SHOULD be marked for transformation
        var abMapperMapMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Map" && m.ContainingType.Contains("ABMapper"));
        abMapperMapMethod.Should().NotBeNull();
        abMapperMapMethod!.RequiresAsyncTransformation.Should().BeTrue();

        // Assert - OtherMapper.Map should NOT be marked for transformation (it doesn't call async methods)
        var otherMapperMapMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Map" && m.ContainingType.Contains("OtherMapper"));
        otherMapperMapMethod.Should().NotBeNull();
        otherMapperMapMethod!.RequiresAsyncTransformation.Should().BeFalse(
            "OtherMapper.Map should not be flooded just because ABMapper.Map is async");

        // Assert - Base type transformation should be recorded
        callGraph.BaseTypeTransformations.Should().ContainKey("GenericInterfaceTest.ABMapper");
        var transformation = callGraph.BaseTypeTransformations["GenericInterfaceTest.ABMapper"].First();
        transformation.TypeArgumentIndex.Should().Be(1, "TResult is at index 1");

        // Act - Transform the source
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(
            source,
            transformations.ToList(),
            null,
            null,
            callGraph.BaseTypeTransformations,
            default);

        // Assert - Interface should remain unchanged
        transformedSource.Should().Contain("public interface IMapper<in TSource, out TResult>");
        transformedSource.Should().Contain("TResult Map(TSource source);");

        // Assert - ABMapper's base type should be transformed to use Task<B>
        // Note: The rewriter may produce slightly different whitespace
        transformedSource.Should().Match("*ABMapper : IMapper<A*Task<B>>*");

        // Assert - ABMapper.Map should have Task<B> return type
        transformedSource.Should().Contain("Task<B> Map(A source)");

        // Assert - OtherMapper should remain unchanged
        transformedSource.Should().Contain("OtherMapper : IMapper<A, B>");
    }

    [Fact]
    public async Task InterfaceMapping_SyncInterfaceReplacedWithAsyncInterface()
    {
        // Arrange - A sync interface and its async counterpart
        var source = @"
using System;
using System.Threading.Tasks;

namespace InterfaceMappingTest
{
    public interface IRepository
    {
        string GetData();
        void SaveData(string data);
    }

    public interface IRepositoryAsync
    {
        Task<string> GetDataAsync();
        Task SaveDataAsync(string data);
    }

    public class Repository : IRepository
    {
        public string GetData()
        {
            return FetchFromDatabase();
        }

        public void SaveData(string data)
        {
            WriteToDatabase(data);
        }

        private string FetchFromDatabase()
        {
            Console.WriteLine(""Fetching..."");
            return ""data"";
        }

        private void WriteToDatabase(string data)
        {
            Console.WriteLine($""Writing {data}..."");
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Add interface mapping: IRepository -> IRepositoryAsync
        callGraph.InterfaceMappings["InterfaceMappingTest.IRepository"] = "IRepositoryAsync";

        // Mark FetchFromDatabase as async root
        var fetchMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "FetchFromDatabase");
        fetchMethod.Should().NotBeNull();

        var rootMethods = new HashSet<string> { fetchMethod!.Id };
        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - The sync interface should NOT be marked for transformation
        var syncInterfaceGetMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetData" && m.IsInterfaceMethod);
        syncInterfaceGetMethod.Should().NotBeNull();
        syncInterfaceGetMethod!.RequiresAsyncTransformation.Should().BeFalse(
            "sync interface should not be transformed when a mapping exists");

        // Assert - Repository.GetData SHOULD be marked for transformation
        var repoGetMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetData" && m.ContainingType.Contains("Repository") && !m.IsInterfaceMethod);
        repoGetMethod.Should().NotBeNull();
        repoGetMethod!.RequiresAsyncTransformation.Should().BeTrue();

        // Act - Transform the source
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(
            source,
            transformations.ToList(),
            null,
            null,
            callGraph.BaseTypeTransformations,
            default);

        // Assert - Sync interface IRepository should remain unchanged
        transformedSource.Should().Contain("public interface IRepository");
        transformedSource.Should().Contain("string GetData();");
        transformedSource.Should().Contain("void SaveData(string data);");

        // Assert - Async interface IRepositoryAsync should remain unchanged
        transformedSource.Should().Contain("public interface IRepositoryAsync");
        transformedSource.Should().Contain("Task<string> GetDataAsync();");

        // Assert - Repository should implement IRepositoryAsync instead of IRepository
        transformedSource.Should().Contain("Repository : IRepositoryAsync");
        transformedSource.Should().NotContain("Repository : IRepository");

        // Assert - Repository.GetData should be transformed
        transformedSource.Should().Contain("Task<string> GetData()");

        // Assert - Repository.SaveData should NOT be transformed (doesn't call async methods)
        transformedSource.Should().Contain("void SaveData(string data)");
        transformedSource.Should().NotContain("Task SaveData");
    }
}
