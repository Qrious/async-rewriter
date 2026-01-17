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
        transformedSource.Should().Contain("async Task<string> GetData()");
        transformedSource.Should().Contain("await FetchFromDatabase()");
        transformedSource.Should().Contain("async Task<string> FetchFromDatabase()");
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

        // Assert - GetData should be transformed
        transformedSource.Should().Contain("async Task<string> GetData()");

        // ProcessData should NOT be transformed (it doesn't call async methods)
        transformedSource.Should().Contain("void ProcessData(string data)");
        transformedSource.Should().NotContain("async Task ProcessData");
    }
}
