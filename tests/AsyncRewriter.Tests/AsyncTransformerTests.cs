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
    public async Task TransformSourceAsync_SimpleVoidMethod_AddsAsyncModifierAndTaskReturnType()
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
        result.Should().Contain("async Task TestMethod()");
        result.Should().Contain("using System.Threading.Tasks;");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodReturningInt_TransformsToTaskOfInt()
    {
        // Arrange
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

        // Assert
        result.Should().Contain("async Task<int> GetValue()");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodCallsAsyncMethod_AddsAwait()
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
        result.Should().Contain("async Task CallerMethod()");
        result.Should().Contain("await CalleeMethod()");
        result.Should().Contain("async Task CalleeMethod()");
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
        result.Should().Contain("public async Task TestMethod()");
    }

    [Fact]
    public async Task TransformSourceAsync_PrivateMethod_PreservesModifiers()
    {
        // Arrange
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

        // Assert
        result.Should().Contain("private async Task TestMethod()");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodReturningTaskAlready_KeepsTaskReturnType()
    {
        // Arrange
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

        // Assert
        result.Should().Contain("async Task TestMethod()");
        result.Should().NotContain("Task<Task>");
    }

    [Fact]
    public async Task TransformSourceAsync_MultipleMethodsInClass_TransformsOnlySpecified()
    {
        // Arrange
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

        // Assert
        result.Should().Contain("async Task Method1()");
        result.Should().Contain("void Method2()");
        result.Should().NotContain("async Task Method2()");
    }

    [Fact]
    public async Task TransformSourceAsync_MethodWithParameters_PreservesParameters()
    {
        // Arrange
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

        // Assert
        result.Should().Contain("async Task<int> Calculate(int x, string name)");
    }

    [Fact]
    public async Task TransformSourceAsync_GenericReturnType_TransformsToTaskOfGeneric()
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

        // Assert
        result.Should().Contain("async Task<List<string>> GetNames()");
    }

    [Fact]
    public async Task TransformSourceAsync_ChainOfMethodCalls_AddsAwaitToAll()
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

        // Assert
        result.Should().Contain("await Method2()");
        result.Should().Contain("await Method3()");
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
        // Arrange
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

        // Assert
        result.Should().Contain("public static async Task TestMethod()");
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
