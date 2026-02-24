using System.Collections.Concurrent;
using AsyncRewriter.Analyzer.ServiceInterface;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class AsyncInterfaceMethodExtractorTests
{
    private readonly AsyncInterfaceMethodExtractor _extractor = new();

    private static CallGraph CreateCallGraph(
        Dictionary<string, IMethodNode> methods,
        List<IInterfaceImplementation>? implementations = null)
    {
        var graphId = Guid.NewGuid().ToString();
        var methodDict = new ConcurrentDictionary<string, IMethodNode>(methods);
        var implBag = implementations != null
            ? new ConcurrentBag<IInterfaceImplementation>(implementations)
            : new ConcurrentBag<IInterfaceImplementation>();
        return new CallGraph(graphId, methodDict, [], implBag);
    }

    private static MethodNode MakeMethod(string id, string name, string containingType, string graphId = "g")
        => new()
        {
            CallGraphId = graphId,
            Id = id,
            Name = name,
            ContainingType = containingType,
            ContainingNamespace = "App",
            ReturnType = "void",
            Parameters = [],
            FilePath = "test.cs",
            StartLine = 1,
            EndLine = 10,
            IsReturnTypeParameter = false
        };

    private static InterfaceImplementation MakeImpl(string implementingId, string interfaceId, string graphId = "g")
        => new()
        {
            CallGraphId = graphId,
            ImplementingMethodId = implementingId,
            InterfaceMethodId = interfaceId
        };

    [Fact]
    public void Extract_EmptyCallGraph_ReturnsEmpty()
    {
        var graph = CreateCallGraph([]);

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MethodOnServiceInterface_IsMarked()
    {
        var method = MakeMethod("m1", "GetUser", "IUserService");
        var graph = CreateCallGraph(new() { [method.Id] = method });

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().ContainSingle()
            .Which.Key.Should().Be(method.Id);
    }

    [Fact]
    public void Extract_MethodOnServiceInterface_MetadataIsCorrect()
    {
        var method = MakeMethod("m1", "GetUser", "IUserService");
        var graph = CreateCallGraph(new() { [method.Id] = method });

        var result = _extractor.Extract(graph);

        var meta = result.MethodMetadata[method.Id];
        meta.IsServiceInterfaceMethod.Should().BeTrue();
        meta.InterfaceName.Should().Be("IUserService");
    }

    [Fact]
    public void Extract_MethodNotOnServiceInterface_IsNotMarked()
    {
        var method = MakeMethod("m1", "DoWork", "MyHelper");
        var graph = CreateCallGraph(new() { [method.Id] = method });

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().BeEmpty();
    }

    [Theory]
    [InlineData("IOrderService")]
    [InlineData("IPaymentService")]
    [InlineData("OrderService")]
    [InlineData("CustomerService")]
    public void Extract_ContainingTypeEndingWithService_IsMarked(string containingType)
    {
        var method = MakeMethod("m1", "Execute", containingType);
        var graph = CreateCallGraph(new() { [method.Id] = method });

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().ContainSingle();
    }

    [Theory]
    [InlineData("IServiceHelper")]   // contains "Service" but does not end with it
    [InlineData("ServiceFactory")]
    [InlineData("MyRepository")]
    [InlineData("DbContext")]
    public void Extract_ContainingTypeNotEndingWithService_IsNotMarked(string containingType)
    {
        var method = MakeMethod("m1", "Execute", containingType);
        var graph = CreateCallGraph(new() { [method.Id] = method });

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().BeEmpty();
    }

    [Fact]
    public void Extract_ConcreteImplementationOfServiceInterfaceMethod_IsMarked()
    {
        var interfaceMethod = MakeMethod("iface-m1", "GetUser", "IUserService");
        var concreteMethod = MakeMethod("impl-m1", "GetUser", "UserServiceImpl");
        var impl = MakeImpl(concreteMethod.Id, interfaceMethod.Id);

        var graph = CreateCallGraph(
            new() { [interfaceMethod.Id] = interfaceMethod, [concreteMethod.Id] = concreteMethod },
            [impl]);

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Keys.Should().BeEquivalentTo([interfaceMethod.Id, concreteMethod.Id]);
    }

    [Fact]
    public void Extract_ConcreteImplementationMetadata_ReferencesInterfaceName()
    {
        var interfaceMethod = MakeMethod("iface-m1", "GetUser", "IUserService");
        var concreteMethod = MakeMethod("impl-m1", "GetUser", "UserServiceImpl");
        var impl = MakeImpl(concreteMethod.Id, interfaceMethod.Id);

        var graph = CreateCallGraph(
            new() { [interfaceMethod.Id] = interfaceMethod, [concreteMethod.Id] = concreteMethod },
            [impl]);

        var result = _extractor.Extract(graph);

        result.MethodMetadata[concreteMethod.Id].InterfaceName.Should().Be("IUserService");
    }

    [Fact]
    public void Extract_ImplementationOfNonServiceInterface_IsNotMarked()
    {
        var interfaceMethod = MakeMethod("iface-m1", "DoWork", "IHelper");
        var concreteMethod = MakeMethod("impl-m1", "DoWork", "HelperImpl");
        var impl = MakeImpl(concreteMethod.Id, interfaceMethod.Id);

        var graph = CreateCallGraph(
            new() { [interfaceMethod.Id] = interfaceMethod, [concreteMethod.Id] = concreteMethod },
            [impl]);

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().BeEmpty();
    }

    [Fact]
    public void Extract_MultipleMethodsOnSameServiceInterface_AllMarked()
    {
        var m1 = MakeMethod("m1", "GetUser", "IUserService");
        var m2 = MakeMethod("m2", "CreateUser", "IUserService");
        var m3 = MakeMethod("m3", "DeleteUser", "IUserService");

        var graph = CreateCallGraph(new() { [m1.Id] = m1, [m2.Id] = m2, [m3.Id] = m3 });

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().HaveCount(3);
        result.MethodMetadata.Keys.Should().BeEquivalentTo([m1.Id, m2.Id, m3.Id]);
    }

    [Fact]
    public void Extract_MethodAlreadyMarkedAsInterfaceMethod_NotDuplicatedByImplementation()
    {
        // A method node that is itself on a *Service interface AND also has an impl record
        // pointing to another interface method — should appear only once.
        var interfaceMethod = MakeMethod("iface-m1", "Execute", "ITaskService");
        var concreteMethod = MakeMethod("impl-m1", "Execute", "TaskServiceImpl");
        var impl = MakeImpl(concreteMethod.Id, interfaceMethod.Id);

        var graph = CreateCallGraph(
            new() { [interfaceMethod.Id] = interfaceMethod, [concreteMethod.Id] = concreteMethod },
            [impl]);

        var result = _extractor.Extract(graph);

        result.MethodMetadata.Should().HaveCount(2);
    }

    [Fact]
    public void Extract_ImplWithMissingInterfaceMethodInGraph_DoesNotThrow()
    {
        var concreteMethod = MakeMethod("impl-m1", "Execute", "TaskServiceImpl");
        var orphanImpl = MakeImpl(concreteMethod.Id, "nonexistent-interface-method");

        var graph = CreateCallGraph(
            new() { [concreteMethod.Id] = concreteMethod },
            [orphanImpl]);

        var result = _extractor.Extract(graph);

        // orphan impl referencing unknown interface method should be ignored
        result.MethodMetadata.Should().BeEmpty();
    }
}
