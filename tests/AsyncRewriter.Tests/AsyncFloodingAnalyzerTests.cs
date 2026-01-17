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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().Contain(method.Id);
        method.RequiresAsyncTransformation.Should().BeTrue();
        method.AsyncReturnType.Should().Be("Task");
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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().NotContain(method.Id);
        method.RequiresAsyncTransformation.Should().BeFalse();
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

        callGraph.AddMethod(method1);
        callGraph.AddMethod(method2);
        callGraph.AddMethod(method3);

        // Method1 -> Method2 -> Method3
        callGraph.AddCall(new MethodCall
        {
            CallerId = method1.Id,
            CalleeId = method2.Id,
            CallerSignature = "Method1()",
            CalleeSignature = "Method2()"
        });

        callGraph.AddCall(new MethodCall
        {
            CallerId = method2.Id,
            CalleeId = method3.Id,
            CallerSignature = "Method2()",
            CalleeSignature = "Method3()"
        });

        // Root is Method3 (leaf)
        var rootMethods = new HashSet<string> { method3.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().HaveCount(3);
        result.FloodedMethods.Should().Contain(method1.Id);
        result.FloodedMethods.Should().Contain(method2.Id);
        result.FloodedMethods.Should().Contain(method3.Id);

        method1.RequiresAsyncTransformation.Should().BeTrue();
        method2.RequiresAsyncTransformation.Should().BeTrue();
        method3.RequiresAsyncTransformation.Should().BeTrue();
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

        callGraph.AddMethod(caller);
        callGraph.AddMethod(root1);
        callGraph.AddMethod(root2);

        // Caller -> Root1
        // Caller -> Root2
        callGraph.AddCall(new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = root1.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Root1()"
        });

        callGraph.AddCall(new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = root2.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Root2()"
        });

        var rootMethods = new HashSet<string> { root1.Id, root2.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().HaveCount(3);
        caller.RequiresAsyncTransformation.Should().BeTrue();
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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        method.AsyncReturnType.Should().Be("Task");
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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        method.AsyncReturnType.Should().Be("Task<int>");
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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        method.AsyncReturnType.Should().Be("Task");
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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        method.AsyncReturnType.Should().Be("Task<string>");
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

        callGraph.AddMethod(caller);
        callGraph.AddMethod(callee);

        var call = new MethodCall
        {
            CallerId = caller.Id,
            CalleeId = callee.Id,
            CallerSignature = "Caller()",
            CalleeSignature = "Callee()"
        };
        callGraph.AddCall(call);

        var rootMethods = new HashSet<string> { callee.Id };

        // Act
        await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        call.RequiresAwait.Should().BeTrue();
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

        callGraph.AddMethod(caller);
        callGraph.AddMethod(callee);

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
        callGraph.AddCall(call);

        callGraph.FloodedMethods = new HashSet<string> { caller.Id, callee.Id };

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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string> { method.Id };

        // Act
        await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        method.AsyncReturnType.Should().Be("Task<List<string>>");
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
        callGraph.AddMethod(method);

        var rootMethods = new HashSet<string>();

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().BeEmpty();
        method.RequiresAsyncTransformation.Should().BeFalse();
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

        callGraph.AddMethod(top);
        callGraph.AddMethod(left);
        callGraph.AddMethod(right);
        callGraph.AddMethod(bottom);

        // Diamond: Top -> Left -> Bottom, Top -> Right -> Bottom
        callGraph.AddCall(new MethodCall { CallerId = top.Id, CalleeId = left.Id, CallerSignature = "Top()", CalleeSignature = "Left()" });
        callGraph.AddCall(new MethodCall { CallerId = top.Id, CalleeId = right.Id, CallerSignature = "Top()", CalleeSignature = "Right()" });
        callGraph.AddCall(new MethodCall { CallerId = left.Id, CalleeId = bottom.Id, CallerSignature = "Left()", CalleeSignature = "Bottom()" });
        callGraph.AddCall(new MethodCall { CallerId = right.Id, CalleeId = bottom.Id, CallerSignature = "Right()", CalleeSignature = "Bottom()" });

        var rootMethods = new HashSet<string> { bottom.Id };

        // Act
        var result = await _analyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        result.FloodedMethods.Should().HaveCount(4);
        top.RequiresAsyncTransformation.Should().BeTrue();
        left.RequiresAsyncTransformation.Should().BeTrue();
        right.RequiresAsyncTransformation.Should().BeTrue();
        bottom.RequiresAsyncTransformation.Should().BeTrue();
    }
}
