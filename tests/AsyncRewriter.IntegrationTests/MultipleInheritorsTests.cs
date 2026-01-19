using AsyncRewriter.Analyzer;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.IntegrationTests;

public class MultipleInheritorsTests
{
    private readonly CallGraphAnalyzer _callGraphAnalyzer;
    private readonly AsyncFloodingAnalyzer _floodingAnalyzer;
    private readonly AsyncTransformer _transformer;

    public MultipleInheritorsTests()
    {
        _callGraphAnalyzer = new CallGraphAnalyzer();
        _floodingAnalyzer = new AsyncFloodingAnalyzer();
        _transformer = new AsyncTransformer(_floodingAnalyzer);
    }

    [Fact]
    public async Task MultipleInheritors_InterfaceWithThreeImplementations_FloodsAllImplementations()
    {
        // Arrange
        var source = @"
using System;

namespace MultipleInheritors
{
    public interface IDataProvider
    {
        string FetchData();
    }

    public class DatabaseProvider : IDataProvider
    {
        public string FetchData()
        {
            return QueryDatabase();
        }

        private string QueryDatabase()
        {
            Console.WriteLine(""Querying database..."");
            return ""database-data"";
        }
    }

    public class ApiProvider : IDataProvider
    {
        public string FetchData()
        {
            return CallExternalApi();
        }

        private string CallExternalApi()
        {
            Console.WriteLine(""Calling external API..."");
            return ""api-data"";
        }
    }

    public class FileProvider : IDataProvider
    {
        public string FetchData()
        {
            return ReadFromFile();
        }

        private string ReadFromFile()
        {
            Console.WriteLine(""Reading from file..."");
            return ""file-data"";
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Set all three private methods as roots
        var queryMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "QueryDatabase");
        var apiMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "CallExternalApi");
        var fileMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "ReadFromFile");

        var rootMethods = new HashSet<string> { queryMethod!.Id, apiMethod!.Id, fileMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - All three FetchData implementations should be flooded
        var dbFetchData = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchData" && m.ContainingType.Contains("DatabaseProvider"));
        dbFetchData.Should().NotBeNull();
        dbFetchData!.RequiresAsyncTransformation.Should().BeTrue();

        var apiFetchData = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchData" && m.ContainingType.Contains("ApiProvider"));
        apiFetchData.Should().NotBeNull();
        apiFetchData!.RequiresAsyncTransformation.Should().BeTrue();

        var fileFetchData = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchData" && m.ContainingType.Contains("FileProvider"));
        fileFetchData.Should().NotBeNull();
        fileFetchData!.RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleInheritors_AggregatorCallsAllImplementations_Floods()
    {
        // Arrange
        var source = @"
using System;
using System.Collections.Generic;
using System.Linq;

namespace MultipleInheritors
{
    public interface IDataProvider
    {
        string FetchData();
    }

    public class DatabaseProvider : IDataProvider
    {
        public string FetchData()
        {
            return QueryDatabase();
        }

        private string QueryDatabase()
        {
            return ""database-data"";
        }
    }

    public class ApiProvider : IDataProvider
    {
        public string FetchData()
        {
            return CallExternalApi();
        }

        private string CallExternalApi()
        {
            return ""api-data"";
        }
    }

    public class DataAggregator
    {
        private readonly List<IDataProvider> _providers;

        public DataAggregator(List<IDataProvider> providers)
        {
            _providers = providers;
        }

        public string AggregateData()
        {
            var results = _providers.Select(p => p.FetchData()).ToList();
            return string.Join("", "", results);
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var queryMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "QueryDatabase");
        var apiMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "CallExternalApi");

        var rootMethods = new HashSet<string> { queryMethod!.Id, apiMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - AggregateData should be flooded
        var aggregateMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "AggregateData");
        aggregateMethod.Should().NotBeNull();
        aggregateMethod!.RequiresAsyncTransformation.Should().BeTrue();
        aggregateMethod.AsyncReturnType.Should().Be("Task<string>");
    }

    [Fact]
    public async Task MultipleInheritors_OneImplementationHasAsyncRoot_AllImplementationsFlooded()
    {
        // Arrange
        // For non-generic interfaces, when one implementation becomes async,
        // the interface signature must change, requiring ALL implementations to change
        var source = @"
using System;

namespace MultipleInheritors
{
    public interface IDataProvider
    {
        string FetchData();
    }

    public class DatabaseProvider : IDataProvider
    {
        public string FetchData()
        {
            return QueryDatabase();
        }

        private string QueryDatabase()
        {
            return ""database-data"";
        }
    }

    public class InMemoryProvider : IDataProvider
    {
        public string FetchData()
        {
            return GetFromCache();
        }

        private string GetFromCache()
        {
            return ""cache-data"";
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Only set QueryDatabase as root (not GetFromCache)
        var queryMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "QueryDatabase");
        var rootMethods = new HashSet<string> { queryMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - DatabaseProvider.FetchData should be flooded
        var dbFetchData = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchData" && m.ContainingType.Contains("DatabaseProvider"));
        dbFetchData.Should().NotBeNull();
        dbFetchData!.RequiresAsyncTransformation.Should().BeTrue();

        // InMemoryProvider.FetchData MUST also be flooded because the interface
        // signature changes to Task<string>, requiring all implementations to match
        var memoryFetchData = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchData" && m.ContainingType.Contains("InMemoryProvider"));
        memoryFetchData.Should().NotBeNull();
        memoryFetchData!.RequiresAsyncTransformation.Should().BeTrue(
            "all implementations must match the async interface signature");

        // The interface method itself should be marked for transformation
        var interfaceMethod = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "FetchData" && m.IsInterfaceMethod);
        interfaceMethod.Should().NotBeNull();
        interfaceMethod!.RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleInheritors_ComplexHierarchy_FloodsCorrectly()
    {
        // Arrange
        var source = @"
using System;
using System.Collections.Generic;

namespace MultipleInheritors
{
    public interface IMessageHandler
    {
        void HandleMessage(string message);
    }

    public abstract class BaseMessageHandler : IMessageHandler
    {
        public virtual void HandleMessage(string message)
        {
            LogMessage(message);
        }

        protected void LogMessage(string message)
        {
            WriteToLog(message);
        }

        private void WriteToLog(string message)
        {
            Console.WriteLine($""Logging: {message}"");
        }
    }

    public class EmailMessageHandler : BaseMessageHandler
    {
        public override void HandleMessage(string message)
        {
            base.HandleMessage(message);
            SendEmail(message);
        }

        private void SendEmail(string message)
        {
            Console.WriteLine($""Sending email: {message}"");
        }
    }

    public class SmsMessageHandler : BaseMessageHandler
    {
        public override void HandleMessage(string message)
        {
            base.HandleMessage(message);
            SendSms(message);
        }

        private void SendSms(string message)
        {
            Console.WriteLine($""Sending SMS: {message}"");
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Set WriteToLog and SendEmail as roots
        var writeLogMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "WriteToLog");
        var sendEmailMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "SendEmail");

        var rootMethods = new HashSet<string> { writeLogMethod!.Id, sendEmailMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - Both handlers should have HandleMessage flooded
        var emailHandleMessage = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "HandleMessage" && m.ContainingType.Contains("EmailMessageHandler"));
        emailHandleMessage.Should().NotBeNull();
        emailHandleMessage!.RequiresAsyncTransformation.Should().BeTrue();

        // SmsMessageHandler should be flooded through the base class path (WriteToLog)
        var smsHandleMessage = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "HandleMessage" && m.ContainingType.Contains("SmsMessageHandler"));
        smsHandleMessage.Should().NotBeNull();
        smsHandleMessage!.RequiresAsyncTransformation.Should().BeTrue();

        // Base class methods should be flooded
        var baseHandleMessage = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "HandleMessage" && m.ContainingType.Contains("BaseMessageHandler"));
        baseHandleMessage.Should().NotBeNull();
        baseHandleMessage!.RequiresAsyncTransformation.Should().BeTrue();

        var logMessage = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "LogMessage");
        logMessage.Should().NotBeNull();
        logMessage!.RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task MultipleInheritors_DeepCallChain_FloodsAllLevels()
    {
        // Arrange
        var source = @"
using System;

namespace MultipleInheritors
{
    public class DeepChain
    {
        public void Level1()
        {
            Level2();
        }

        private void Level2()
        {
            Level3();
        }

        private void Level3()
        {
            Level4();
        }

        private void Level4()
        {
            Level5Async();
        }

        private void Level5Async()
        {
            Console.WriteLine(""Async operation"");
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var level5Method = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "Level5Async");
        var rootMethods = new HashSet<string> { level5Method!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - All levels should be flooded
        for (int i = 1; i <= 5; i++)
        {
            var levelMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name.Contains($"Level{i}"));
            levelMethod.Should().NotBeNull($"Level{i} should exist");
            levelMethod!.RequiresAsyncTransformation.Should().BeTrue($"Level{i} should be flooded");
        }

        // Verify Level1 has correct return type
        var level1 = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "Level1");
        level1!.AsyncReturnType.Should().Be("Task");
    }
}
