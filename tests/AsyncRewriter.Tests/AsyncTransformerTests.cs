using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class AsyncTransformerTests
{
    private readonly AsyncTransformer _transformer;
    private readonly AsyncFloodingAnalyzer _floodingAnalyzer;

    public AsyncTransformerTests()
    {
        _floodingAnalyzer = new AsyncFloodingAnalyzer();
        _transformer = new AsyncTransformer(_floodingAnalyzer);
    }

    [Fact]
    public async Task TransformSourceAsync_SimpleVoidMethod_UsesTaskCompletedTask()
    {
        // Arrange - method has no async calls, so should use Task.CompletedTask instead of async
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.TestMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - no async keyword, uses Task.CompletedTask
        result.Should().Contain("Task TestMethod()");
        result.Should().Contain("Task.CompletedTask");
        result.Should().NotContain("async Task TestMethod()");
        result.Should().Contain("using System.Threading.Tasks;");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodReturningInt_UsesTaskFromResult()
    {
        // Arrange - method has no async calls, so should use Task.FromResult instead of async
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public int GetValue()
        {
            return 42;
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.GetValue()",
                OriginalReturnType = "int",
                NewReturnType = "Task<int>",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - no async keyword, uses Task.FromResult
        result.Should().Contain("Task<int> GetValue()");
        result.Should().Contain("Task.FromResult<int>(42)");
        result.Should().NotContain("async Task<int> GetValue()");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodCallsAsyncMethod_DirectlyReturnsTask()
    {
        // Arrange
        var sourceCode = @"
using System.Threading.Tasks;

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

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.CallerMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            },
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.CalleeMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert
        // CallerMethod only has one async call, so it directly returns the task (no async/await overhead)
        result.Should().Contain("Task CallerMethod()");
        result.Should().Contain("return CalleeMethod()");
        result.Should().NotContain("async Task CallerMethod()");
        // CalleeMethod has no async calls, so it should use Task.CompletedTask
        result.Should().Contain("Task CalleeMethod()");
        result.Should().Contain("Task.CompletedTask");
    }

    [Fact]
    public async Task TransformSourceAsync_AlreadyHasTaskUsing_DoesNotDuplicateUsing()
    {
        // Arrange
        var sourceCode = @"
using System.Threading.Tasks;

namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.TestMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert
        var usingCount = result.Split("using System.Threading.Tasks;").Length - 1;
        usingCount.Should().Be(1);
    }

    [Fact]
    public async Task TransformSourceAsync_PublicMethod_PreservesModifiers()
    {
        // Arrange - method has no async calls, so should use Task.CompletedTask
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.TestMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - preserves public modifier, uses Task.CompletedTask
        result.Should().Contain("public Task TestMethod()");
        result.Should().Contain("Task.CompletedTask");
    }

    [Fact]
    public async Task TransformSourceAsync_PrivateMethod_PreservesModifiers()
    {
        // Arrange - method has no async calls, so should use Task.CompletedTask
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        private void TestMethod()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.TestMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - preserves private modifier, uses Task.CompletedTask
        result.Should().Contain("private Task TestMethod()");
        result.Should().Contain("Task.CompletedTask");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodReturningTaskAlready_KeepsTaskReturnType()
    {
        // Arrange - method already returns Task and has no async calls, keeps Task return type
        var sourceCode = @"
using System.Threading.Tasks;

namespace TestNamespace
{
    public class TestClass
    {
        public Task TestMethod()
        {
            return Task.CompletedTask;
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.TestMethod()",
                OriginalReturnType = "Task",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - keeps Task return type, doesn't wrap in Task<Task>
        result.Should().Contain("Task TestMethod()");
        result.Should().NotContain("Task<Task>");
    }

    [Fact]
    public async Task TransformSourceAsync_MultipleMethodsInClass_TransformsOnlySpecified()
    {
        // Arrange - Method1 has no async calls, should use Task.CompletedTask
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void Method1()
        {
        }

        public void Method2()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.Method1()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - Method1 transformed, Method2 unchanged
        result.Should().Contain("Task Method1()");
        result.Should().Contain("Task.CompletedTask");
        result.Should().Contain("void Method2()");
        result.Should().NotContain("Task Method2()");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodWithParameters_PreservesParameters()
    {
        // Arrange - method has no async calls, should use Task.FromResult
        var sourceCode = @"
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

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.Calculate(int, string)",
                OriginalReturnType = "int",
                NewReturnType = "Task<int>",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - preserves parameters, uses Task.FromResult
        result.Should().Contain("Task<int> Calculate(int x, string name)");
        result.Should().Contain("Task.FromResult<int>(x)");
    }

    [Fact]
    public async Task TransformSourceAsync_GenericReturnType_UsesTaskFromResult()
    {
        // Arrange - method has no async calls, should use Task.FromResult
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

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.GetNames()",
                OriginalReturnType = "List<string>",
                NewReturnType = "Task<List<string>>",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - uses Task.FromResult with generic type
        result.Should().Contain("Task<List<string>> GetNames()");
        result.Should().Contain("Task.FromResult<List<string>>");
    }

    [Fact]
    public async Task TransformSourceAsync_ChainOfMethodCalls_DirectlyReturnsTasks()
    {
        // Arrange
        var sourceCode = @"
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

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.Method1()",
                OriginalReturnType = "void",
                NewReturnType = "Task"
            },
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.Method2()",
                OriginalReturnType = "void",
                NewReturnType = "Task"
            },
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.Method3()",
                OriginalReturnType = "void",
                NewReturnType = "Task"
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - Method1 and Method2 have single async calls, so directly return the task
        // Method3 has no async calls so uses Task.CompletedTask
        result.Should().Contain("return Method2()");
        result.Should().Contain("return Method3()");
        result.Should().Contain("Task.CompletedTask");
        result.Should().NotContain("await");
    }

    [Fact]
    public async Task TransformSourceAsync_EmptyTransformationList_ReturnsOriginalSource()
    {
        // Arrange
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public void TestMethod()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>();

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert
        result.Should().Contain("void TestMethod()");
        result.Should().NotContain("async");
    }

    [Fact]
    public async Task TransformSourceAsync_StaticMethod_PreservesStaticModifier()
    {
        // Arrange - method has no async calls, should use Task.CompletedTask
        var sourceCode = @"
namespace TestNamespace
{
    public class TestClass
    {
        public static void TestMethod()
        {
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new AsyncTransformationInfo
            {
                MethodId = "TestNamespace.TestClass.TestMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true
            }
        };

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert - preserves static modifier, uses Task.CompletedTask
        result.Should().Contain("public static Task TestMethod()");
        result.Should().Contain("Task.CompletedTask");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodWithAwaitAlready_DoesNotAddDuplicateAwait()
    {
        // Arrange
        var sourceCode = @"
using System.Threading.Tasks;

namespace TestNamespace
{
    public class TestClass
    {
        public async Task CallerMethod()
        {
            await CalleeMethod();
        }

        public async Task CalleeMethod()
        {
            await Task.Delay(100);
        }
    }
}";

        var transformations = new List<AsyncTransformationInfo>();

        // Act
        var result = await _transformer.TransformSourceAsync(sourceCode, transformations);

        // Assert
        result.Should().Contain("await CalleeMethod()");
        result.Should().NotContain("await await");
    }
}
