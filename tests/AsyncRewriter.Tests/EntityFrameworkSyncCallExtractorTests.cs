using System.Collections.Concurrent;
using AsyncRewriter.Analyzer.EntityFramework;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class EntityFrameworkSyncCallExtractorTests
{
    private readonly EntityFrameworkSyncCallExtractor _extractor = new();

    private static CallGraph CreateCallGraph(
        Dictionary<string, IMethodNode> methods,
        List<IMethodCall>? calls = null)
    {
        var graphId = Guid.NewGuid().ToString();
        var methodDict = new ConcurrentDictionary<string, IMethodNode>(methods);
        var callBag = new ConcurrentBag<IMethodCall>(calls ?? []);
        return new CallGraph(graphId, methodDict, callBag, [], [], []);
    }

    private static MethodNode MakeMethod(string id, string name, string containingType, string containingNamespace, string graphId = "g")
        => new()
        {
            CallGraphId = graphId,
            Id = id,
            Name = name,
            ContainingType = containingType,
            ContainingNamespace = containingNamespace,
            ReturnType = "void",
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
    public void Extract_EmptyCallGraph_ReturnsEmpty()
    {
        var graph = CreateCallGraph([]);

        var result = _extractor.Extract(graph);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Extract_CallToEfSaveChanges_ReturnsCaller()
    {
        var caller = MakeMethod("caller", "DoWork", "MyRepo", "App.Data");
        var callee = MakeMethod("callee", "SaveChanges", "DbContext", "System.Data.Entity");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [callee.Id] = callee },
            [MakeCall(caller.Id, callee.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().ContainSingle()
            .Which.MethodId.Should().Be(caller.Id);
    }

    [Fact]
    public void Extract_CallToEfToList_ReturnsCaller()
    {
        var caller = MakeMethod("caller", "GetAll", "MyService", "App.Services");
        var callee = MakeMethod("callee", "ToList", "QueryableExtensions", "System.Data.Entity");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [callee.Id] = callee },
            [MakeCall(caller.Id, callee.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().ContainSingle()
            .Which.MethodId.Should().Be(caller.Id);
    }

    [Theory]
    [InlineData("ToList", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("ToArray", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("FirstOrDefault", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("First", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("SingleOrDefault", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Single", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Count", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Any", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Sum", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Min", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Max", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("Average", "QueryableExtensions", "System.Data.Entity")]
    [InlineData("SaveChanges", "DbContext", "System.Data.Entity")]
    [InlineData("Find", "DbSet", "System.Data.Entity")]
    [InlineData("Load", "DbQuery", "System.Data.Entity")]
    [InlineData("ExecuteSqlCommand", "Database", "System.Data.Entity")]
    public void Extract_KnownEfSyncMethod_ReturnsCaller(string methodName, string containingType, string containingNamespace)
    {
        var caller = MakeMethod("caller", "DoWork", "MyClass", "App");
        var callee = MakeMethod("callee", methodName, containingType, containingNamespace);

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [callee.Id] = callee },
            [MakeCall(caller.Id, callee.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().ContainSingle()
            .Which.MethodId.Should().Be(caller.Id);
    }

    [Fact]
    public void Extract_NonEfMethodWithSameName_ReturnsEmpty()
    {
        var caller = MakeMethod("caller", "DoWork", "MyClass", "App");
        var callee = MakeMethod("callee", "ToList", "MyCollection", "App.Collections");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [callee.Id] = callee },
            [MakeCall(caller.Id, callee.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Extract_UnknownMethodNameInEfNamespace_ReturnsEmpty()
    {
        var caller = MakeMethod("caller", "DoWork", "MyClass", "App");
        var callee = MakeMethod("callee", "SomeNonAsyncMethod", "DbContext", "System.Data.Entity");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [callee.Id] = callee },
            [MakeCall(caller.Id, callee.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Extract_CallerCallsMultipleEfMethods_ReturnCallerOnlyOnce()
    {
        var caller = MakeMethod("caller", "DoWork", "MyRepo", "App.Data");
        var toList = MakeMethod("toList", "ToList", "QueryableExtensions", "System.Data.Entity");
        var saveChanges = MakeMethod("saveChanges", "SaveChanges", "DbContext", "System.Data.Entity");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [toList.Id] = toList, [saveChanges.Id] = saveChanges },
            [MakeCall(caller.Id, toList.Id), MakeCall(caller.Id, saveChanges.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().ContainSingle()
            .Which.MethodId.Should().Be(caller.Id);
    }

    [Fact]
    public void Extract_MultipleCallersOfEfMethod_ReturnsAllCallers()
    {
        var callerA = MakeMethod("callerA", "MethodA", "RepoA", "App.Data");
        var callerB = MakeMethod("callerB", "MethodB", "RepoB", "App.Data");
        var saveChanges = MakeMethod("saveChanges", "SaveChanges", "DbContext", "System.Data.Entity");

        var graph = CreateCallGraph(
            new() { [callerA.Id] = callerA, [callerB.Id] = callerB, [saveChanges.Id] = saveChanges },
            [MakeCall(callerA.Id, saveChanges.Id), MakeCall(callerB.Id, saveChanges.Id)]);

        var result = _extractor.Extract(graph);

        result.Should().HaveCount(2);
        result.Select(r => r.MethodId).Should().BeEquivalentTo([callerA.Id, callerB.Id]);
    }

    [Fact]
    public void Extract_ReasonDescribesEfMethod()
    {
        var caller = MakeMethod("caller", "DoWork", "MyRepo", "App.Data");
        var callee = MakeMethod("callee", "SaveChanges", "DbContext", "System.Data.Entity");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller, [callee.Id] = callee },
            [MakeCall(caller.Id, callee.Id)]);

        var result = _extractor.Extract(graph);

        result.Single().Reason.Should().Contain("SaveChanges");
    }

    [Fact]
    public void Extract_CallWithMissingCalleeInGraph_DoesNotThrow()
    {
        var caller = MakeMethod("caller", "DoWork", "MyRepo", "App.Data");
        var orphanCall = MakeCall(caller.Id, "nonexistent-callee");

        var graph = CreateCallGraph(
            new() { [caller.Id] = caller },
            [orphanCall]);

        var result = _extractor.Extract(graph);

        result.Should().BeEmpty();
    }
}
