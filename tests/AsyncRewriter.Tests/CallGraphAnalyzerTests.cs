using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class CallGraphAnalyzerTests
{
    private readonly CallGraphAnalyzer _analyzer;

    public CallGraphAnalyzerTests()
    {
        _analyzer = new CallGraphAnalyzer();
    }

    [Fact]
    public async Task AnalyzeSourceAsync_SimpleMethod_CreatesMethodNode()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Should().NotBeNull();
        callGraph.Methods.Should().ContainSingle();

        var method = callGraph.Methods.Values.First();
        method.Name.Should().Be("TestMethod");
        method.ContainingType.Should().Be("TestNamespace.TestClass");
        method.ReturnType.Should().Be("void");
        method.IsAsync.Should().BeFalse();
    }

    [Fact]
    public async Task AnalyzeSourceAsync_MethodWithParameters_CapturesParameters()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public int Calculate(int x, string name)
        {
            return x;
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        var method = callGraph.Methods.Values.First();
        method.Parameters.Should().HaveCount(2);
        method.Parameters.Should().Contain("int x");
        method.Parameters.Should().Contain("string name");
        method.ReturnType.Should().Be("int");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_AsyncMethod_IdentifiesAsAsync()
    {
        // Arrange
        var sourceCode = @"
using System.Threading.Tasks;

namespace TestNamespace
{
    public class TestClass
    {
        public async Task<int> GetValueAsync()
        {
            await Task.Delay(100);
            return 42;
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        var method = callGraph.Methods.Values.First();
        method.Name.Should().Be("GetValueAsync");
        method.IsAsync.Should().BeTrue();
        method.ReturnType.Should().Be("Task<int>");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_MethodCallsAnotherMethod_CreatesMethodCall()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void CallerMethod()
        {
            CalleeMethod();
        }

        public void CalleeMethod()
        {
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Methods.Should().HaveCount(2);
        callGraph.Calls.Should().ContainSingle();

        var call = callGraph.Calls.First();
        call.CallerSignature.Should().Be("CallerMethod()");
        call.CalleeSignature.Should().Be("CalleeMethod()");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_ChainedMethodCalls_CreatesMultipleCalls()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void Method1()
        {
            Method2();
        }

        public void Method2()
        {
            Method3();
        }

        public void Method3()
        {
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Methods.Should().HaveCount(3);
        callGraph.Calls.Should().HaveCount(2);

        var call1 = callGraph.Calls.FirstOrDefault(c => c.CallerSignature == "Method1()");
        call1.Should().NotBeNull();
        call1!.CalleeSignature.Should().Be("Method2()");

        var call2 = callGraph.Calls.FirstOrDefault(c => c.CallerSignature == "Method2()");
        call2.Should().NotBeNull();
        call2!.CalleeSignature.Should().Be("Method3()");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_MultipleCallsInSameMethod_CapturesAllCalls()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void CallerMethod()
        {
            Method1();
            Method2();
            Method3();
        }

        public void Method1() { }
        public void Method2() { }
        public void Method3() { }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Methods.Should().HaveCount(4);
        callGraph.Calls.Should().HaveCount(3);

        var callsFromCaller = callGraph.Calls.Where(c => c.CallerSignature == "CallerMethod()");
        callsFromCaller.Should().HaveCount(3);
    }

    [Fact]
    public async Task AnalyzeSourceAsync_ExternalMethodCall_AddsExternalMethodNode()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
            Console.WriteLine(""Hello"");
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Methods.Should().HaveCount(2); // TestMethod + WriteLine

        var writeLineMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "WriteLine");
        writeLineMethod.Should().NotBeNull();
        writeLineMethod!.FilePath.Should().Be("external");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_MethodWithLineNumbers_CapturesCorrectLineNumbers()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void Method1()
        {
            Method2();
        }

        public void Method2()
        {
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        var method1 = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "Method1");
        method1.Should().NotBeNull();
        method1!.StartLine.Should().BeGreaterThan(0);
        method1.EndLine.Should().BeGreaterOrEqualTo(method1.StartLine);

        var call = callGraph.Calls.First();
        call.LineNumber.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task AnalyzeSourceAsync_ClassWithMultipleMethods_IdentifiesAllMethods()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void Method1() { }
        public int Method2(string s) { return 0; }
        public async Task Method3() { await Task.Delay(1); }
        private string Method4(int x, int y) { return """"; }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Methods.Should().HaveCount(4);

        var methodNames = callGraph.Methods.Values.Select(m => m.Name).ToList();
        methodNames.Should().Contain("Method1");
        methodNames.Should().Contain("Method2");
        methodNames.Should().Contain("Method3");
        methodNames.Should().Contain("Method4");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_GenericReturnType_CapturesGenericType()
    {
        // Arrange
        var sourceCode = @"
using System.Collections.Generic;

namespace TestNamespace
{
    public class TestClass
    {
        public List<string> GetNames()
        {
            return new List<string>();
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        var method = callGraph.Methods.Values.First();
        method.ReturnType.Should().Contain("List<string>");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_MethodInvocationWithArguments_StillCreatesCall()
    {
        // Arrange
        var sourceCode = @"
using System;

namespace TestNamespace
{
    public class TestClass
    {
        public void CallerMethod()
        {
            CalleeMethod(42, ""test"");
        }

        public void CalleeMethod(int x, string s)
        {
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Calls.Should().ContainSingle();

        var call = callGraph.Calls.First();
        call.CalleeSignature.Should().Be("CalleeMethod(int, string)");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_RecursiveMethod_CreatesCallToSelf()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public int Factorial(int n)
        {
            if (n <= 1) return 1;
            return n * Factorial(n - 1);
        }
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Methods.Should().ContainSingle();
        callGraph.Calls.Should().ContainSingle();

        var call = callGraph.Calls.First();
        call.CallerSignature.Should().Be(call.CalleeSignature);
        call.CalleeSignature.Should().Be("Factorial(int)");
    }

    [Fact]
    public async Task AnalyzeSourceAsync_EmptyClass_CreatesEmptyCallGraph()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class EmptyClass
    {
    }
}";

        // Act
        var callGraph = await _analyzer.AnalyzeSourceAsync(sourceCode);

        // Assert
        callGraph.Should().NotBeNull();
        callGraph.Methods.Should().BeEmpty();
        callGraph.Calls.Should().BeEmpty();
    }
}
