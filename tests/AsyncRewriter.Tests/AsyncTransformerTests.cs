using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AsyncRewriter.Tests;

public class AsyncTransformerTests
{
    private readonly AsyncTransformer _transformer = new();

    /// <summary>
    /// Verifies that the given C# source compiles without errors.
    /// Stub types are provided in a separate syntax tree so usings don't conflict.
    /// </summary>
    private static void AssertCompiles(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var stubTree = CSharpSyntaxTree.ParseText(StubTypes);

        var references = new List<MetadataReference>();
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        foreach (var assembly in trustedAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(assembly);
            if (name is "System.Runtime" or "System.Threading.Tasks" or "System.Console"
                or "System.Private.CoreLib" or "netstandard")
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        var compilation = CSharpCompilation.Create("TestCompilation",
            new[] { syntaxTree, stubTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        diagnostics.Should().BeEmpty(
            "transformed code should compile without errors, but got:\n" +
            string.Join("\n", diagnostics.Select(d => $"  {d.Location.GetLineSpan()}: {d.GetMessage()}")));
    }

    // Stub types used by test sources to make them compilable
    private const string StubTypes = @"
using System.Threading.Tasks;

interface IRepo
{
    Task Open();
    Task Close();
    Task Connect();
    Task<int> GetValue();
    Task<int> GetCount();
}

static class Helper
{
    public static Task Run() => Task.CompletedTask;
}

interface IBuilder
{
    Task<IBuilder> Configure();
    IBuilder SetName(string name);
    Task Build();
    Task<int> GetCount();
}

static class SyncHelper
{
    public static T RunSync<T>(System.Func<Task<T>> func) => func().GetAwaiter().GetResult();
    public static void RunSync(System.Func<Task> func) => func().GetAwaiter().GetResult();
}
";

    [Fact]
    public async Task TransformSourceAsync_VoidMethod_WithAwaitableCall_AddsAsyncAndAwait()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    void Connect()
    {
        _repo.Open();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Connect()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_repo.Open()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task Connect()");
        result.Should().Contain("return _repo.Open();");
        result.Should().NotContain("async");
        result.Should().NotContain("await");
        result.Should().Contain("using System.Threading.Tasks;");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_ReturningMethod_WithAwaitableCall_TransformsReturnType()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    int Fetch()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Fetch()",
                OriginalReturnType = "int",
                NewReturnType = "Task<int>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<int> Fetch()");
        result.Should().Contain("return _repo.GetValue();");
        result.Should().NotContain("async");
        result.Should().NotContain("await");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_NonFloodedCallSites_RemainUnchanged()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    void Process()
    {
        _repo.Open();
        _repo.Close();
    }
}";

        // Only line 6 (_repo.Open) is flooded, line 7 (_repo.Close) is not
        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Process()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_repo.Open()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("await _repo.Open()");
        result.Should().Contain("_repo.Close();");
        result.Should().NotContain("await _repo.Close()");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_AddsUsingDirective_WhenNotPresent()
    {
        var source = @"using System;

class MyService
{
    void DoWork()
    {
        Helper.Run();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.DoWork()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 7, OriginalCallExpression = "Helper.Run()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("using System.Threading.Tasks;");
        result.Should().Contain("using System;");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_DoesNotDuplicateUsingDirective()
    {
        var source = @"using System;
using System.Threading.Tasks;

class MyService
{
    void DoWork()
    {
        Helper.Run();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.DoWork()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 8, OriginalCallExpression = "Helper.Run()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        // Should have exactly one occurrence
        var count = result.Split("using System.Threading.Tasks;").Length - 1;
        count.Should().Be(1);
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_FloodedMethod_NoAwaitableCalls_UsesTaskCompletedTask()
    {
        var source = @"using System;

class MyService
{
    void NoOp()
    {
        Console.WriteLine(""hello"");
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.NoOp()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task NoOp()");
        result.Should().Contain("Task.CompletedTask");
        result.Should().NotContain("async");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_VoidMethodWithEarlyReturn_NoAwaitableCalls_ReturnsTaskCompletedTask()
    {
        var source = @"using System;

class MyService
{
    void Process(string? input)
    {
        if (input == null)
            return;
        Console.WriteLine(input);
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Process(string?)",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task Process(");
        result.Should().NotContain("async");
        // The early return should become "return Task.CompletedTask;"
        result.Should().NotContain("\n            return;\n");
        // Should have Task.CompletedTask for both early return and end of method
        var count = result.Split("return Task.CompletedTask").Length - 1;
        count.Should().Be(2, "both the early return and the appended return should use Task.CompletedTask");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_FloodedMethod_NoAwaitableCalls_UsesTaskFromResult()
    {
        var source = @"class MyService
{
    bool IsConnected()
    {
        return true;
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.IsConnected()",
                OriginalReturnType = "bool",
                NewReturnType = "Task<bool>",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<bool> IsConnected()");
        result.Should().Contain("Task.FromResult<bool>(true)");
        result.Should().NotContain("async");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformProjectAsync_WithFloodedCallGraph_TransformsCorrectly()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "async-rewriter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "TestService.cs");

        try
        {
            var source = @"class TestService
{
    private IRepo _repo;
    void DoWork()
    {
        _repo.Connect();
    }
}";
            await File.WriteAllTextAsync(tempFile, source);

            var callGraph = CreateFloodedCallGraph(tempFile);
            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);
            result.ModifiedFiles[0].TransformedContent.Should().Contain("Task DoWork()");
            result.ModifiedFiles[0].TransformedContent.Should().Contain("return _repo.Connect();");
            result.ModifiedFiles[0].TransformedContent.Should().NotContain("async");
            result.ModifiedFiles[0].TransformedContent.Should().NotContain("await");
            AssertCompiles(result.ModifiedFiles[0].TransformedContent);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TransformSourceAsync_MultipleMethodsInSameFile()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    void First()
    {
        _repo.Open();
    }
    int Second()
    {
        return _repo.GetCount();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.First()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_repo.Open()" }
                }
            },
            new()
            {
                MethodId = "MyService.Second()",
                OriginalReturnType = "int",
                NewReturnType = "Task<int>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 10, OriginalCallExpression = "_repo.GetCount()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task First()");
        result.Should().Contain("return _repo.Open();");
        result.Should().Contain("Task<int> Second()");
        result.Should().Contain("return _repo.GetCount();");
        result.Should().NotContain("async");
        result.Should().NotContain("await");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_FullyQualifiedReturnType_IsPreserved()
    {
        var source = @"using System.Collections.Generic;

class MyService
{
    private IRepo _repo;
    System.Collections.Generic.List<int> GetItems()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetItems()",
                OriginalReturnType = "System.Collections.Generic.List<int>",
                NewReturnType = "Task<System.Collections.Generic.List<int>>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 8, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<System.Collections.Generic.List<int>> GetItems()");
        result.Should().NotContain("Task<List<int>> GetItems()");
    }

    [Fact]
    public async Task TransformSourceAsync_FullyQualifiedReturnType_PreservedInTaskFromResult()
    {
        var source = @"using System.Collections.Generic;

class MyService
{
    System.Collections.Generic.List<string> GetNames()
    {
        return new System.Collections.Generic.List<string>();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetNames()",
                OriginalReturnType = "System.Collections.Generic.List<string>",
                NewReturnType = "Task<System.Collections.Generic.List<string>>",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<System.Collections.Generic.List<string>> GetNames()");
        result.Should().Contain("Task.FromResult<System.Collections.Generic.List<string>>");
    }

    [Fact]
    public async Task TransformSourceAsync_TypeAlias_IsPreserved()
    {
        var source = @"using System.Collections.Generic;
using StringList = System.Collections.Generic.List<string>;

class MyService
{
    private IRepo _repo;
    StringList GetNames()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetNames()",
                OriginalReturnType = "StringList",
                NewReturnType = "Task<StringList>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 9, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<StringList> GetNames()");
        result.Should().NotContain("Task<List<string>> GetNames()");
    }

    [Fact]
    public async Task TransformSourceAsync_TypeAlias_PreservedInTaskFromResult()
    {
        var source = @"using System.Collections.Generic;
using StringList = System.Collections.Generic.List<string>;

class MyService
{
    StringList GetNames()
    {
        return new List<string>();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetNames()",
                OriginalReturnType = "StringList",
                NewReturnType = "Task<StringList>",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<StringList> GetNames()");
        result.Should().Contain("Task.FromResult<StringList>");
    }

    [Fact]
    public async Task TransformSourceAsync_NamespaceAlias_IsPreserved()
    {
        var source = @"using Col = System.Collections.Generic;

class MyService
{
    private IRepo _repo;
    Col.List<int> GetItems()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetItems()",
                OriginalReturnType = "Col.List<int>",
                NewReturnType = "Task<Col.List<int>>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 8, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<Col.List<int>> GetItems()");
        result.Should().NotContain("Task<List<int>> GetItems()");
    }

    [Fact]
    public async Task TransformSourceAsync_NamespaceAlias_PreservedInTaskFromResult()
    {
        var source = @"using Col = System.Collections.Generic;

class MyService
{
    Col.List<int> GetItems()
    {
        return new Col.List<int>();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetItems()",
                OriginalReturnType = "Col.List<int>",
                NewReturnType = "Task<Col.List<int>>",
                NeedsAsyncKeyword = false,
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<Col.List<int>> GetItems()");
        result.Should().Contain("Task.FromResult<Col.List<int>>");
    }

    [Fact]
    public async Task TransformSourceAsync_FullyQualifiedReturnType_InInterface_IsPreserved()
    {
        var source = @"using System.Collections.Generic;

interface IMyService
{
    System.Collections.Generic.List<int> GetItems();
}

class MyService : IMyService
{
    private IRepo _repo;
    public System.Collections.Generic.List<int> GetItems()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetItems()",
                OriginalReturnType = "System.Collections.Generic.List<int>",
                NewReturnType = "Task<System.Collections.Generic.List<int>>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 13, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<System.Collections.Generic.List<int>> GetItems()");
        result.Should().NotContain("Task<List<int>> GetItems()");
    }

    [Fact]
    public async Task TransformSourceAsync_TypeAlias_InInterface_IsPreserved()
    {
        var source = @"using System.Collections.Generic;
using StringList = System.Collections.Generic.List<string>;

interface IMyService
{
    StringList GetNames();
}

class MyService : IMyService
{
    private IRepo _repo;
    public StringList GetNames()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetNames()",
                OriginalReturnType = "StringList",
                NewReturnType = "Task<StringList>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 14, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<StringList> GetNames()");
        result.Should().NotContain("Task<List<string>> GetNames()");
    }

    [Fact]
    public async Task TransformSourceAsync_NamespaceAlias_InInterface_IsPreserved()
    {
        var source = @"using Col = System.Collections.Generic;

interface IMyService
{
    Col.List<int> GetItems();
}

class MyService : IMyService
{
    private IRepo _repo;
    public Col.List<int> GetItems()
    {
        return _repo.GetValue();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetItems()",
                OriginalReturnType = "Col.List<int>",
                NewReturnType = "Task<Col.List<int>>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 13, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<Col.List<int>> GetItems()");
        result.Should().NotContain("Task<List<int>> GetItems()");
    }

    [Fact]
    public async Task TransformSourceAsync_LocalFunction_CallingFloodedMethod_BecomesAsyncAndAwaited()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    void OuterMethod()
    {
        void LocalFunc()
        {
            _repo.Open();
        }
        LocalFunc();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.OuterMethod().LocalFunc()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 8, OriginalCallExpression = "_repo.Open()" }
                }
            },
            new()
            {
                MethodId = "MyService.OuterMethod()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 10, OriginalCallExpression = "LocalFunc()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("async Task LocalFunc()");
        result.Should().Contain("await _repo.Open()");
        result.Should().Contain("await LocalFunc()");
        result.Should().Contain("async Task OuterMethod()");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_LambdaWithAwaitedCall_BecomesAsyncLambda()
    {
        var source = @"using System;

class MyService
{
    private IRepo _repo;
    void Process()
    {
        Action action = () => _repo.Open();
        action();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Process()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 8, OriginalCallExpression = "_repo.Open()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("async () => await _repo.Open()");
        result.Should().Contain("async Task Process()");
    }

    [Fact]
    public async Task TransformSourceAsync_ParenthesizedLambdaWithAwaitedCall_BecomesAsyncLambda()
    {
        var source = @"using System;

class MyService
{
    private IRepo _repo;
    void Process()
    {
        Action<int> action = (x) => _repo.Open();
        action(1);
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Process()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 8, OriginalCallExpression = "_repo.Open()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("async (x) => await _repo.Open()");
        result.Should().Contain("async Task Process()");
    }

    [Fact]
    public async Task TransformProjectAsync_WithInterfaceMappings_UpdatesBaseListAndMethods()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "async-rewriter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "RepoImpl.cs");

        try
        {
            var source = @"class RepoImpl : IRepository
{
    private readonly object _db;
    public string Get()
    {
        return _db.ToString();
    }
}";
            await File.WriteAllTextAsync(tempFile, source);

            var callGraph = CreateFloodedCallGraphWithInterface(tempFile);
            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Method should be transformed
            transformed.Should().Contain("Task<string> Get()");

            // Base list should be updated to async interface
            transformed.Should().Contain(": IRepositoryAsync");
            transformed.Should().NotContain(": IRepository\r");
            transformed.Should().NotContain(": IRepository\n");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TransformSourceAsync_MethodChain_AwaitWrappedInParentheses()
    {
        var source = @"class MyService
{
    private IBuilder _builder;
    void Setup()
    {
        _builder.Configure().SetName(""test"");
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Setup()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_builder.Configure()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("(await _builder.Configure()).SetName(\"test\")");
        result.Should().Contain("async Task Setup()");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_MethodChain_MultiLine_AwaitWrappedInParentheses()
    {
        var source = @"class MyService
{
    private IBuilder _builder;
    void Setup()
    {
        _builder.Configure()
            .SetName(""test"");
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Setup()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_builder.Configure()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("(await _builder.Configure())");
        result.Should().Contain(".SetName(\"test\")");
        result.Should().Contain("async Task Setup()");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_MethodChain_MultiLine_PreservesFormatting()
    {
        var source = @"class MyService
{
    private IBuilder _builder;
    void Setup()
    {
        _builder.Configure()
            .SetName(""test"")
            .Build();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Setup()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_builder.Configure()" },
                    new() { LineNumber = 8, OriginalCallExpression = "_builder.Configure().SetName(\"test\").Build()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        // Configure() should be parenthesized because it's chained
        result.Should().Contain("(await _builder.Configure())");
        // The multiline chain formatting should be preserved
        result.Should().Contain(".SetName(\"test\")");
        result.Should().Contain("async Task Setup()");
    }

    [Fact]
    public async Task TransformSourceAsync_SyncWrapperCall_ExpressionLambda_Unwrapped()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    int Fetch()
    {
        return SyncHelper.RunSync(() => _repo.GetValue());
    }
}";

        var syncWrapperIds = new HashSet<string> { "SyncHelper.RunSync<int>(Func<Task<int>>)" };
        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Fetch()",
                OriginalReturnType = "int",
                NewReturnType = "Task<int>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new()
                    {
                        LineNumber = 6,
                        OriginalCallExpression = "SyncHelper.RunSync(() => _repo.GetValue())",
                        CalledMethodSignature = "SyncHelper.RunSync<int>(Func<Task<int>>)"
                    }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(
            source, transformations, syncWrapperIds);

        result.Should().Contain("return _repo.GetValue();");
        result.Should().NotContain("RunSync");
        result.Should().Contain("Task<int> Fetch()");
        result.Should().NotContain("async");
        result.Should().NotContain("await");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_SyncWrapperCall_BlockLambda_Unwrapped()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    int Fetch()
    {
        return SyncHelper.RunSync(() => { return _repo.GetValue(); });
    }
}";

        var syncWrapperIds = new HashSet<string> { "SyncHelper.RunSync<int>(Func<Task<int>>)" };
        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Fetch()",
                OriginalReturnType = "int",
                NewReturnType = "Task<int>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new()
                    {
                        LineNumber = 6,
                        OriginalCallExpression = "SyncHelper.RunSync(() => { return _repo.GetValue(); })",
                        CalledMethodSignature = "SyncHelper.RunSync<int>(Func<Task<int>>)"
                    }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(
            source, transformations, syncWrapperIds);

        result.Should().Contain("return _repo.GetValue();");
        result.Should().NotContain("RunSync");
        result.Should().NotContain("async");
        result.Should().NotContain("await");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_SyncWrapperCall_VoidReturn_Unwrapped()
    {
        var source = @"class MyService
{
    private IRepo _repo;
    void Process()
    {
        SyncHelper.RunSync(() => _repo.Open());
    }
}";

        var syncWrapperIds = new HashSet<string> { "SyncHelper.RunSync(Func<Task>)" };
        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Process()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new()
                    {
                        LineNumber = 6,
                        OriginalCallExpression = "SyncHelper.RunSync(() => _repo.Open())",
                        CalledMethodSignature = "SyncHelper.RunSync(Func<Task>)"
                    }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(
            source, transformations, syncWrapperIds);

        result.Should().Contain("return _repo.Open();");
        result.Should().NotContain("RunSync");
        result.Should().Contain("Task Process()");
        result.Should().NotContain("async");
        result.Should().NotContain("await");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformProjectAsync_SyncWrapperCall_DetectedAndUnwrapped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "async-rewriter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "TestService.cs");

        try
        {
            var source = @"class TestService
{
    private IRepo _repo;
    int Fetch()
    {
        return SyncHelper.RunSync(() => _repo.GetValue());
    }
}";
            await File.WriteAllTextAsync(tempFile, source);

            var callGraph = CreateFloodedCallGraphWithSyncWrapper(tempFile);
            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);
            result.ModifiedFiles[0].TransformedContent.Should().Contain("return _repo.GetValue();");
            result.ModifiedFiles[0].TransformedContent.Should().NotContain("RunSync");
            result.ModifiedFiles[0].TransformedContent.Should().Contain("Task<int> Fetch()");
            result.ModifiedFiles[0].TransformedContent.Should().NotContain("async");
            result.ModifiedFiles[0].TransformedContent.Should().NotContain("await");
            AssertCompiles(result.ModifiedFiles[0].TransformedContent);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static CallGraph CreateFloodedCallGraphWithSyncWrapper(string tempFile)
    {
        var methods = new ConcurrentDictionary<string, MethodNode>();
        methods["TestService.Fetch()"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "TestService.Fetch()",
            Name = "Fetch",
            ContainingType = "TestService",
            ContainingNamespace = "",
            ReturnType = "Task<int>", // flooded
            Parameters = new List<string>(),
            FilePath = tempFile,
            StartLine = 4,
            EndLine = 7
        };
        methods["SyncHelper.RunSync<int>(Func<Task<int>>)"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "SyncHelper.RunSync<int>(Func<Task<int>>)",
            Name = "RunSync",
            ContainingType = "SyncHelper",
            ContainingNamespace = "",
            ReturnType = "Task<int>", // flooded (it was originally int with Func<Task<int>> param)
            Parameters = new List<string> { "Func<Task<int>> func" },
            FilePath = "external",
            StartLine = 0,
            EndLine = 0
        };
        methods["IRepo.GetValue()"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "IRepo.GetValue()",
            Name = "GetValue",
            ContainingType = "IRepo",
            ContainingNamespace = "",
            ReturnType = "Task<int>",
            Parameters = new List<string>(),
            FilePath = "external",
            StartLine = 0,
            EndLine = 0
        };

        var calls = new ConcurrentBag<MethodCall>();
        calls.Add(new MethodCall
        {
            CallGraphId = "test",
            Id = "call1",
            CallerId = "TestService.Fetch()",
            CalleeId = "SyncHelper.RunSync<int>(Func<Task<int>>)",
            LineNumber = 6,
            FilePath = tempFile
        });

        var graph = new CallGraph(calls)
        {
            Methods = methods
        };

        return graph;
    }

    private static CallGraph CreateFloodedCallGraphWithInterface(string tempFile)
    {
        var methods = new ConcurrentDictionary<string, MethodNode>();
        methods["RepoImpl.Get()"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "RepoImpl.Get()",
            Name = "Get",
            ContainingType = "RepoImpl",
            ContainingNamespace = "",
            ReturnType = "Task<string>", // flooded
            Parameters = new List<string>(),
            FilePath = tempFile,
            StartLine = 4,
            EndLine = 7
        };
        methods["IRepository.Get()"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "IRepository.Get()",
            Name = "Get",
            ContainingType = "IRepository",
            ContainingNamespace = "",
            ReturnType = "Task<string>", // flooded
            Parameters = new List<string>(),
            FilePath = "external",
            StartLine = 0,
            EndLine = 0
        };

        var calls = new ConcurrentBag<MethodCall>();
        var impls = new ConcurrentBag<InterfaceImplementation>();
        impls.Add(new InterfaceImplementation
        {
            CallGraphId = "test",
            ImplementingMethodId = "RepoImpl.Get()",
            InterfaceMethodId = "IRepository.Get()"
        });

        var graph = new CallGraph(calls, impls)
        {
            Methods = methods,
            InterfaceMappings = new List<InterfaceMapping>
            {
                new() { SyncInterfaceName = "IRepository", AsyncInterfaceName = "IRepositoryAsync" }
            }
        };

        return graph;
    }

    [Fact]
    public async Task TransformSourceAsync_LambdaWithAsyncOverload_AwaitsParentCall()
    {
        // When a lambda arg becomes async, the parent Execute() call resolves to
        // async overload returning Task<string>, so it needs await
        var source = @"using System;
using System.Threading.Tasks;
class MyService
{
    string GetName()
    {
        return Execute(session => session.GetName());
    }
    T Execute<T>(Func<ISession, T> func) => default;
    Task<T> Execute<T>(Func<ISession, Task<T>> func) => default;
}
interface ISession { string GetName(); }";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetName()",
                OriginalReturnType = "string",
                NewReturnType = "Task<string>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    // Line 7: the Execute() call needs await (async overload)
                    new() { LineNumber = 7, OriginalCallExpression = "Execute(session => session.GetName())" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("Task<string> GetName()");
        result.Should().Contain("return Execute(");
        result.Should().NotContain("async Task<string>");
        result.Should().NotContain("await");
    }

    [Fact]
    public async Task TransformSourceAsync_LambdaWithoutAsyncOverload_NoAwaitOnParentCall()
    {
        // When there's no async overload, the parent call should NOT get await
        var source = @"using System;
class MyService
{
    string GetName()
    {
        return Execute(session => session.GetName());
    }
    T Execute<T>(Func<string, T> func) => default;
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.GetName()",
                OriginalReturnType = "string",
                NewReturnType = "Task<string>",
                NeedsAsyncKeyword = true,
                // No call sites to transform — no async overload detected
                CallSitesToTransform = new List<CallSiteTransformation>()
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        // Method return type should change but Execute should NOT be awaited
        result.Should().Contain("Task<string> GetName()");
        result.Should().NotContain("await Execute(");
    }

    private static CallGraph CreateFloodedCallGraph(string tempFile)
    {
        var methods = new ConcurrentDictionary<string, MethodNode>();
        methods["TestService.DoWork()"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "TestService.DoWork()",
            Name = "DoWork",
            ContainingType = "TestService",
            ContainingNamespace = "",
            ReturnType = "Task", // flooded
            Parameters = new List<string>(),
            FilePath = tempFile,
            StartLine = 4,
            EndLine = 7
        };
        methods["IRepo.Connect()"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "IRepo.Connect()",
            Name = "Connect",
            ContainingType = "IRepo",
            ContainingNamespace = "",
            ReturnType = "Task", // flooded
            Parameters = new List<string>(),
            FilePath = "external",
            StartLine = 0,
            EndLine = 0
        };

        var calls = new ConcurrentBag<MethodCall>();
        calls.Add(new MethodCall
        {
            CallGraphId = "test",
            Id = "call1",
            CallerId = "TestService.DoWork()",
            CalleeId = "IRepo.Connect()",
            LineNumber = 6,
            FilePath = tempFile
        });

        var graph = new CallGraph(calls)
        {
            Methods = methods
        };

        return graph;
    }

    [Fact]
    public async Task TransformSourceAsync_VoidMethod_MultipleStatements_StillUsesAsyncAwait()
    {
        var source = @"using System;

class MyService
{
    private IRepo _repo;
    void Process()
    {
        Console.WriteLine(""connecting"");
        _repo.Open();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Process()",
                OriginalReturnType = "void",
                NewReturnType = "Task",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 9, OriginalCallExpression = "_repo.Open()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("async Task Process()");
        result.Should().Contain("await _repo.Open()");
        AssertCompiles(result);
    }

    [Fact]
    public async Task TransformSourceAsync_ReturningMethod_MismatchedReturnType_StillUsesAsyncAwait()
    {
        // When the awaited call returns a different type, async/await is still needed
        var source = @"class MyService
{
    private IRepo _repo;
    string Describe()
    {
        return _repo.GetValue().ToString();
    }
}";

        var transformations = new List<AsyncTransformationInfo>
        {
            new()
            {
                MethodId = "MyService.Describe()",
                OriginalReturnType = "string",
                NewReturnType = "Task<string>",
                NeedsAsyncKeyword = true,
                CallSitesToTransform = new List<CallSiteTransformation>
                {
                    new() { LineNumber = 6, OriginalCallExpression = "_repo.GetValue()" }
                }
            }
        };

        var result = await _transformer.TransformSourceAsync(source, transformations);

        result.Should().Contain("async Task<string> Describe()");
        result.Should().Contain("await");
        AssertCompiles(result);
    }
}
