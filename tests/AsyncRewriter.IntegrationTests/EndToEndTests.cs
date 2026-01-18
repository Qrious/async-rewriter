using AsyncRewriter.Analyzer;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.IntegrationTests;

public class EndToEndTests
{
    private readonly CallGraphAnalyzer _callGraphAnalyzer;
    private readonly AsyncFloodingAnalyzer _floodingAnalyzer;
    private readonly AsyncTransformer _transformer;

    public EndToEndTests()
    {
        _callGraphAnalyzer = new CallGraphAnalyzer();
        _floodingAnalyzer = new AsyncFloodingAnalyzer();
        _transformer = new AsyncTransformer(_floodingAnalyzer);
    }

    [Fact]
    public async Task EndToEnd_CompleteWorkflow_AnalyzeFloodTransform()
    {
        // Arrange - A complete application scenario
        var source = @"
using System;
using System.Collections.Generic;

namespace MyApp
{
    public interface IUserService
    {
        User GetUser(int id);
        void UpdateUser(User user);
    }

    public class User
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }

    public class UserService : IUserService
    {
        private readonly IDatabase _database;

        public UserService(IDatabase database)
        {
            _database = database;
        }

        public User GetUser(int id)
        {
            return _database.Query<User>(id);
        }

        public void UpdateUser(User user)
        {
            _database.Save(user);
        }
    }

    public interface IDatabase
    {
        T Query<T>(int id);
        void Save<T>(T entity);
    }

    public class Database : IDatabase
    {
        public T Query<T>(int id)
        {
            return (T)ExecuteQuery(id);
        }

        public void Save<T>(T entity)
        {
            ExecuteCommand(entity);
        }

        private object ExecuteQuery(int id)
        {
            Console.WriteLine($""Executing query for id {id}"");
            return new object();
        }

        private void ExecuteCommand(object entity)
        {
            Console.WriteLine($""Executing command for entity"");
        }
    }

    public class UserController
    {
        private readonly IUserService _userService;

        public UserController(IUserService userService)
        {
            _userService = userService;
        }

        public void HandleGetRequest(int id)
        {
            var user = _userService.GetUser(id);
            Console.WriteLine($""Got user: {user.Name}"");
        }

        public void HandleUpdateRequest(User user)
        {
            _userService.UpdateUser(user);
        }
    }
}";

        // Act - Step 1: Analyze call graph
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Assert - Call graph should contain all methods
        callGraph.Methods.Should().HaveCountGreaterThan(0);

        // Act - Step 2: Identify root async methods (ExecuteQuery and ExecuteCommand)
        var executeQueryMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "ExecuteQuery");
        var executeCommandMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "ExecuteCommand");

        executeQueryMethod.Should().NotBeNull();
        executeCommandMethod.Should().NotBeNull();

        var rootMethods = new HashSet<string> { executeQueryMethod!.Id, executeCommandMethod!.Id };

        // Act - Step 3: Perform async flooding analysis
        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - Verify flooding
        var handleGetRequest = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "HandleGetRequest");
        handleGetRequest.Should().NotBeNull();
        handleGetRequest!.RequiresAsyncTransformation.Should().BeTrue();

        var handleUpdateRequest = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "HandleUpdateRequest");
        handleUpdateRequest.Should().NotBeNull();
        handleUpdateRequest!.RequiresAsyncTransformation.Should().BeTrue();

        // Act - Step 4: Get transformation info
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);

        transformations.Should().HaveCountGreaterThan(0);

        // Act - Step 5: Transform source code
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert - Verify transformations
        transformedSource.Should().Contain("using System.Threading.Tasks;");
        // Methods with single calls directly return the task (no async overhead)
        transformedSource.Should().Contain("Task<User> GetUser(int id)");
        transformedSource.Should().Contain("return _database.Query<User>(id)");
        transformedSource.Should().Contain("Task UpdateUser(User user)");
        transformedSource.Should().Contain("return _database.Save(user)");
        // HandleGetRequest has 2 statements so needs async/await
        transformedSource.Should().Contain("async Task HandleGetRequest(int id)");
        transformedSource.Should().Contain("await _userService.GetUser(id)");
        // HandleUpdateRequest has single statement so directly returns task
        transformedSource.Should().Contain("Task HandleUpdateRequest(User user)");
        transformedSource.Should().Contain("return _userService.UpdateUser(user)");
    }

    [Fact]
    public async Task EndToEnd_MultipleRootMethods_TransformsCorrectly()
    {
        // Arrange
        var source = @"
using System;

namespace MyApp
{
    public class Service
    {
        public void Method1()
        {
            Helper1();
        }

        public void Method2()
        {
            Helper2();
        }

        private void Helper1()
        {
            AsyncOperation1();
        }

        private void Helper2()
        {
            AsyncOperation2();
        }

        private void AsyncOperation1()
        {
            Console.WriteLine(""Async Op 1"");
        }

        private void AsyncOperation2()
        {
            Console.WriteLine(""Async Op 2"");
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var asyncOp1 = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "AsyncOperation1");
        var asyncOp2 = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "AsyncOperation2");

        var rootMethods = new HashSet<string> { asyncOp1!.Id, asyncOp2!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert - All methods have single calls so they directly return tasks
        transformedSource.Should().Contain("Task Method1()");
        transformedSource.Should().Contain("return Helper1()");
        transformedSource.Should().Contain("Task Method2()");
        transformedSource.Should().Contain("return Helper2()");
        transformedSource.Should().Contain("return AsyncOperation1()");
        transformedSource.Should().Contain("return AsyncOperation2()");
        // No async/await needed
        transformedSource.Should().NotContain("await");
    }

    [Fact]
    public async Task EndToEnd_ConditionalAsyncCalls_TransformsCorrectly()
    {
        // Arrange
        var source = @"
using System;

namespace MyApp
{
    public class Service
    {
        public string ProcessData(bool useCache)
        {
            if (useCache)
            {
                return GetFromCache();
            }
            else
            {
                return GetFromDatabase();
            }
        }

        private string GetFromCache()
        {
            Console.WriteLine(""Cache"");
            return ""cached"";
        }

        private string GetFromDatabase()
        {
            Console.WriteLine(""DB"");
            return ""from-db"";
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var dbMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "GetFromDatabase");
        var rootMethods = new HashSet<string> { dbMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert
        transformedSource.Should().Contain("async Task<string> ProcessData(bool useCache)");
        transformedSource.Should().Contain("await GetFromDatabase()");
        // GetFromCache should not have await since it's not in the root set
        transformedSource.Should().NotContain("await GetFromCache()");
    }

    [Fact]
    public async Task EndToEnd_RecursiveMethod_TransformsCorrectly()
    {
        // Arrange
        var source = @"
using System;

namespace MyApp
{
    public class Service
    {
        public int Calculate(int n)
        {
            if (n <= 0)
                return 0;

            AsyncOperation();
            return n + Calculate(n - 1);
        }

        private void AsyncOperation()
        {
            Console.WriteLine(""Async"");
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var asyncOp = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "AsyncOperation");
        var rootMethods = new HashSet<string> { asyncOp!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert
        transformedSource.Should().Contain("async Task<int> Calculate(int n)");
        transformedSource.Should().Contain("await AsyncOperation()");
        transformedSource.Should().Contain("await Calculate(n - 1)");
    }
}
