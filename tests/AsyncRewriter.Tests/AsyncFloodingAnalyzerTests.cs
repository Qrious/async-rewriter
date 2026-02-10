using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class AsyncFloodingAnalyzerTests
{
    private readonly AsyncFloodingAnalyzer _analyzer;

    public AsyncFloodingAnalyzerTests()
    {
        _analyzer = new AsyncFloodingAnalyzer();
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_SingleRootMethod_MarksMethodForTransformation()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "void",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().Contain(method.Id);
        var updatedMethod = result.Methods[method.Id];
        updatedMethod.RequiresAsyncTransformation.Should().BeTrue();
        updatedMethod.AsyncReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_AlreadyAsyncMethod_DoesNotFlood()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "Task",
            IsAsync = true
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().NotContain(method.Id);
        var updatedMethod = result.Methods[method.Id];
        updatedMethod.RequiresAsyncTransformation.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_ChainOfCalls_FloodsAllCallers()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };

        var method1 = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "void",
            IsAsync = false
        };

        var method2 = new MethodNode
        {
            Id = "TestClass.Method2()",
            Name = "Method2",
            ReturnType = "void",
            IsAsync = false
        };

        var method3 = new MethodNode
        {
            Id = "TestClass.Method3()",
            Name = "Method3",
            ReturnType = "void",
            IsAsync = false
        };

        callGraph.Methods.TryAdd(method1.Id, method1);
        callGraph.Methods.TryAdd(method2.Id, method2);
        callGraph.Methods.TryAdd(method3.Id, method3);

        // Method1 -> Method2 -> Method3
        var call1 = new MethodCall
        {
            CallerId = method1.Id,
            CalleeId = method2.Id,
            CallerSignature = "Method1()",
            CalleeSignature = "Method2()"
        };
        callGraph.Calls[call1.Id] = call1;

        var call2 = new MethodCall
        {
            CallerId = method2.Id,
            CalleeId = method3.Id,
            CallerSignature = "Method2()",
            CalleeSignature = "Method3()"
        };
        callGraph.Calls[call2.Id] = call2;

        // Root is Method3 (leaf)
        var rootMethods = new HashSet<string> { method3.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().HaveCount(3);
        result.FloodedMethods.Should().Contain(method1.Id);
        result.FloodedMethods.Should().Contain(method2.Id);
        result.FloodedMethods.Should().Contain(method3.Id);

        result.Methods[method1.Id].RequiresAsyncTransformation.Should().BeTrue();
        result.Methods[method2.Id].RequiresAsyncTransformation.Should().BeTrue();
        result.Methods[method3.Id].RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_MultipleRootMethods_FloodsAllPaths()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };

        var caller = new MethodNode
        {
            Id = "TestClass.Caller()",
            Name = "Caller",
            ReturnType = "void",
            IsAsync = false
        };

        var root1 = new MethodNode
        {
            Id = "TestClass.Root1()",
            Name = "Root1",
            ReturnType = "void",
            IsAsync = false
        };

        var root2 = new MethodNode
        {
            Id = "TestClass.Root2()",
            Name = "Root2",
            ReturnType = "void",
            IsAsync = false
        };

        callGraph.Methods.TryAdd(caller.Id, caller);
        callGraph.Methods.TryAdd(root1.Id, root1);
        callGraph.Methods.TryAdd(root2.Id, root2);

        // Caller -> Root1
        // Caller -> Root2
        var call1 = new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = root1.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Root1()"
        };
        callGraph.Calls[call1.Id] = call1;

        var call2 = new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = root2.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Root2()"
        };
        callGraph.Calls[call2.Id] = call2;

        var rootMethods = new HashSet<string> { root1.Id, root2.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().HaveCount(3);
        result.Methods[caller.Id].RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_VoidReturnType_ConvertsToTask()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "void",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.Methods[method.Id].AsyncReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_IntReturnType_ConvertsToTaskOfInt()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "int",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.Methods[method.Id].AsyncReturnType.Should().Be("Task<int>");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_TaskReturnType_KeepsTask()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "Task",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.Methods[method.Id].AsyncReturnType.Should().Be("Task");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_TaskOfTReturnType_KeepsTaskOfT()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "Task<string>",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.Methods[method.Id].AsyncReturnType.Should().Be("Task<string>");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_MarksCallsRequiringAwait()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };

        var caller = new MethodNode
        {
            Id = "TestClass.Caller()",
            Name = "Caller",
            ReturnType = "void",
            IsAsync = false
        };

        var callee = new MethodNode
        {
            Id = "TestClass.Callee()",
            Name = "Callee",
            ReturnType = "void",
            IsAsync = false
        };

        callGraph.Methods.TryAdd(caller.Id, caller);
        callGraph.Methods.TryAdd(callee.Id, callee);

        var call = new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = callee.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Callee()"
        };
        callGraph.Calls[call.Id] = call;

        var rootMethods = new HashSet<string> { callee.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.Calls[call.Id].RequiresAwait.Should().BeTrue();
    }

    [Fact]
    public async Task GetTransformationInfoAsync_CreatesTransformationInfo()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };

        var caller = new MethodNode
        {
            Id = "TestClass.Caller()",
            Name = "Caller",
            ReturnType = "int",
            IsAsync = false,
            RequiresAsyncTransformation = true,
            AsyncReturnType = "Task<int>"
        };

        var callee = new MethodNode
        {
            Id = "TestClass.Callee()",
            Name = "Callee",
            ReturnType = "string",
            IsAsync = false,
            RequiresAsyncTransformation = true
        };

        callGraph.Methods.TryAdd(caller.Id, caller);
        callGraph.Methods.TryAdd(callee.Id, callee);

        var call = new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = callee.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Callee()",
            RequiresAwait = true,
            FilePath = "test.cs",
            LineNumber = 10
        };
        callGraph.Calls[call.Id] = call;

        callGraph.FloodedMethods.UnionWith(new[] { caller.Id, callee.Id });

        // Act
        var transformations = await _analyzer.GetTransformationInfoAsync(callGraph);

        // Assert
        transformations.Should().HaveCount(2);

        var callerTransformation = transformations.FirstOrDefault(t => t.MethodId == caller.Id);
        callerTransformation.Should().NotBeNull();
        callerTransformation!.OriginalReturnType.Should().Be("int");
        callerTransformation.NewReturnType.Should().Be("Task<int>");
        callerTransformation.NeedsAsyncKeyword.Should().BeTrue();
        callerTransformation.CallSitesToTransform.Should().ContainSingle();

        var callSite = callerTransformation.CallSitesToTransform.First();
        callSite.LineNumber.Should().Be(10);
        callSite.FilePath.Should().Be("test.cs");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_ComplexReturnType_ConvertsToTaskOfT()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "List<string>",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.Methods[method.Id].AsyncReturnType.Should().Be("Task<List<string>>");
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_NoRootMethods_NoFlooding()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };
        var method = new MethodNode
        {
            Id = "TestClass.Method1()",
            Name = "Method1",
            ReturnType = "void",
            IsAsync = false
        };
        callGraph.Methods.TryAdd(method.Id, method);

        var rootMethods = new HashSet<string>();

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().BeEmpty();
        result.Methods[method.Id].RequiresAsyncTransformation.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeFloodingAsync_DiamondDependency_HandlesCorrectly()
    {
        // Arrange
        var callGraph = new CallGraph { ProjectName = "Test" };

        var top = new MethodNode { Id = "Top()", Name = "Top", ReturnType = "void", IsAsync = false };
        var left = new MethodNode { Id = "Left()", Name = "Left", ReturnType = "void", IsAsync = false };
        var right = new MethodNode { Id = "Right()", Name = "Right", ReturnType = "void", IsAsync = false };
        var bottom = new MethodNode { Id = "Bottom()", Name = "Bottom", ReturnType = "void", IsAsync = false };

        callGraph.Methods.TryAdd(top.Id, top);
        callGraph.Methods.TryAdd(left.Id, left);
        callGraph.Methods.TryAdd(right.Id, right);
        callGraph.Methods.TryAdd(bottom.Id, bottom);

        // Diamond: Top -> Left -> Bottom, Top -> Right -> Bottom
        var c1 = new MethodCall { CallerId = top.Id, CalleeId = left.Id, CallerSignature = "Top()", CalleeSignature = "Left()" };
        var c2 = new MethodCall { CallerId = top.Id, CalleeId = right.Id, CallerSignature = "Top()", CalleeSignature = "Right()" };
        var c3 = new MethodCall { CallerId = left.Id, CalleeId = bottom.Id, CallerSignature = "Left()", CalleeSignature = "Bottom()" };
        var c4 = new MethodCall { CallerId = right.Id, CalleeId = bottom.Id, CallerSignature = "Right()", CalleeSignature = "Bottom()" };
        callGraph.Calls[c1.Id] = c1;
        callGraph.Calls[c2.Id] = c2;
        callGraph.Calls[c3.Id] = c3;
        callGraph.Calls[c4.Id] = c4;

        var rootMethods = new HashSet<string> { bottom.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().HaveCount(4);
        result.Methods[top.Id].RequiresAsyncTransformation.Should().BeTrue();
        result.Methods[left.Id].RequiresAsyncTransformation.Should().BeTrue();
        result.Methods[right.Id].RequiresAsyncTransformation.Should().BeTrue();
        result.Methods[bottom.Id].RequiresAsyncTransformation.Should().BeTrue();
    }
}
