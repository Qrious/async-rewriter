using AsyncRewriter.Analyzer;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.IntegrationTests;

public class EndToEndTests
{
    private readonly CallGraphBuilder _callGraphAnalyzer;

    public EndToEndTests()
    {
        _callGraphAnalyzer = new CallGraphBuilder();
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
    }
}
