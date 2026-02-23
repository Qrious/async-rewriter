using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AsyncRewriter.Tests;

public class OutParameterTransformTests
{
    private readonly AsyncTransformer _transformer = new();

    private static void AssertCompiles(string source, string? extraStubs = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trees = new List<SyntaxTree>
        {
            syntaxTree,
            CSharpSyntaxTree.ParseText(StubTypes)
        };

        if (extraStubs != null)
        {
            trees.Add(CSharpSyntaxTree.ParseText(extraStubs));
        }

        var references = new List<MetadataReference>();
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        foreach (var assembly in trustedAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(assembly);

            if (name is "System.Runtime" or "System.Threading.Tasks" or "System.Console"
                or "System.Private.CoreLib" or "netstandard" or "System.ValueTuple")
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        var compilation = CSharpCompilation.Create("TestCompilation",
            trees,
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        diagnostics.Should().BeEmpty(
            "transformed code should compile without errors, but got:\n" +
            string.Join("\n", diagnostics.Select(d => $"  {d.Location.GetLineSpan()}: {d.GetMessage()}")));
    }

    private const string NamespacedAsyncOutResult = @"
namespace AsyncRewriter.Generated
{
    public class AsyncOutResult<T>
    {
        public T Value { get; }
        public bool HasValue { get; }
        public AsyncOutResult(T value, bool hasValue) { Value = value; HasValue = hasValue; }
        public bool TryGetValue(out T value) { value = HasValue ? Value : default!; return HasValue; }
    }
}
";

    private const string StubTypes = @"
using System.Threading.Tasks;

public class AsyncOutResult<T>
{
    public T Value { get; }
    public bool HasValue { get; }
    public AsyncOutResult(T value, bool hasValue) { Value = value; HasValue = hasValue; }
    public bool TryGetValue(out T value) { value = HasValue ? Value : default!; return HasValue; }
}

interface IRepo
{
    Task Open();
    Task Close();
    Task Connect();
    Task<int> GetValue();
}
";

    [Fact]
    public async Task BoolTryPattern_SingleOutParam_TransformsReturnType()
    {
        // Source with a bool-returning method with an out parameter
        var source = @"class Cache
{
    private string _value = ""hello"";
    bool TryGetValue(string key, out string value)
    {
        value = _value;
        return true;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Cache.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Cache.TryGetValue(string, string)",
                methodName: "TryGetValue",
                containingType: "Cache",
                returnType: "Task<bool>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "key" },
                    new() { Type = "string", Name = "value", RefKind = "out" }
                },
                startLine: 4, endLine: 8);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Should have AsyncOutResult return type
            transformed.Should().Contain("Task<AsyncOutResult<string>>");
            // Out parameter should be removed from parameter list
            transformed.Should().NotContain("out string value");
            // Should contain new AsyncOutResult constructor
            transformed.Should().Contain("new AsyncOutResult<string>");
            // Should have using for Tasks
            transformed.Should().Contain("using System.Threading.Tasks;");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TuplePattern_SingleOutParam_TransformsReturnType()
    {
        var source = @"class Processor
{
    int Process(out string message)
    {
        message = ""ok"";
        return 42;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Processor.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Processor.Process(string)",
                methodName: "Process",
                containingType: "Processor",
                returnType: "Task<int>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "message", RefKind = "out" }
                },
                startLine: 3, endLine: 7);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Should have tuple return type
            transformed.Should().Contain("Task<(int Result, string message)>");
            // Out parameter should be removed
            transformed.Should().NotContain("out string message");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BoolTryPattern_MultipleOutParams_UsesTupleInsideAsyncOutResult()
    {
        var source = @"class Cache
{
    bool TryGet(string key, out string name, out int age)
    {
        name = ""Alice"";
        age = 30;
        return true;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Cache.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Cache.TryGet(string, string, int)",
                methodName: "TryGet",
                containingType: "Cache",
                returnType: "Task<bool>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "key" },
                    new() { Type = "string", Name = "name", RefKind = "out" },
                    new() { Type = "int", Name = "age", RefKind = "out" }
                },
                startLine: 3, endLine: 8);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Should use tuple inside AsyncOutResult for multiple out params
            transformed.Should().Contain("Task<AsyncOutResult<(string name, int age)>>");
            // Out params removed from parameter list, but key remains
            transformed.Should().Contain("string key");
            transformed.Should().NotContain("out string name");
            transformed.Should().NotContain("out int age");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BoolTryPattern_AddsUsingDirectiveForAsyncOutResult()
    {
        var source = @"class Cache
{
    private string _value = ""hello"";
    bool TryGetValue(string key, out string value)
    {
        value = _value;
        return true;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Cache.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Cache.TryGetValue(string, string)",
                methodName: "TryGetValue",
                containingType: "Cache",
                returnType: "Task<bool>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "key" },
                    new() { Type = "string", Name = "value", RefKind = "out" }
                },
                startLine: 4, endLine: 8);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Should have using directive for AsyncOutResult namespace
            transformed.Should().Contain("using AsyncRewriter.Generated;",
                "BoolTryPattern uses AsyncOutResult<T> which lives in AsyncRewriter.Generated");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BoolTryPattern_UsesCustomNamespaceForAsyncOutResult()
    {
        var source = @"class Cache
{
    private string _value = ""hello"";
    bool TryGetValue(string key, out string value)
    {
        value = _value;
        return true;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Cache.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Cache.TryGetValue(string, string)",
                methodName: "TryGetValue",
                containingType: "Cache",
                returnType: "Task<bool>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "key" },
                    new() { Type = "string", Name = "value", RefKind = "out" }
                },
                startLine: 4, endLine: 8);

            // Set a custom namespace for AsyncOutResult
            callGraph.AsyncOutResultNamespace = "MyProject.Helpers";

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Should use the custom namespace, not the default
            transformed.Should().Contain("using MyProject.Helpers;");
            transformed.Should().NotContain("using AsyncRewriter.Generated;");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BoolTryPattern_SingleOutParam_ProducesCompilableCode()
    {
        var source = @"class Cache
{
    private string _value = ""hello"";
    bool TryGetValue(string key, out string value)
    {
        value = _value;
        return true;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Cache.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Cache.TryGetValue(string, string)",
                methodName: "TryGetValue",
                containingType: "Cache",
                returnType: "Task<bool>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "key" },
                    new() { Type = "string", Name = "value", RefKind = "out" }
                },
                startLine: 4, endLine: 8);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // The removed out parameter should be declared as a local variable
            transformed.Should().Contain("string value = default!");

            // The transformed code should compile successfully
            AssertCompiles(transformed, NamespacedAsyncOutResult);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task TuplePattern_SingleOutParam_ProducesCompilableCode()
    {
        var source = @"class Processor
{
    int Process(out string message)
    {
        message = ""ok"";
        return 42;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Processor.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Processor.Process(string)",
                methodName: "Process",
                containingType: "Processor",
                returnType: "Task<int>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "message", RefKind = "out" }
                },
                startLine: 3, endLine: 7);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // The removed out parameter should be declared as a local variable
            transformed.Should().Contain("string message = default!");

            // The transformed code should compile successfully
            AssertCompiles(transformed);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BoolTryPattern_MultipleOutParams_ProducesCompilableCode()
    {
        var source = @"class Cache
{
    bool TryGet(string key, out string name, out int age)
    {
        name = ""Alice"";
        age = 30;
        return true;
    }
}";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Cache.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var callGraph = CreateFloodedCallGraphWithOutParam(tempFile,
                methodId: "Cache.TryGet(string, string, int)",
                methodName: "TryGet",
                containingType: "Cache",
                returnType: "Task<bool>",
                parameters: new List<MethodParameter>
                {
                    new() { Type = "string", Name = "key" },
                    new() { Type = "string", Name = "name", RefKind = "out" },
                    new() { Type = "int", Name = "age", RefKind = "out" }
                },
                startLine: 3, endLine: 8);

            var result = await _transformer.TransformProjectAsync(tempDir, callGraph);

            result.Success.Should().BeTrue();
            result.ModifiedFiles.Should().HaveCount(1);

            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Both removed out parameters should be declared as local variables
            transformed.Should().Contain("string name = default!");
            transformed.Should().Contain("int age = default!");

            // The transformed code should compile successfully
            AssertCompiles(transformed, NamespacedAsyncOutResult);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OutParameterAnalyzer_DetectsOutParameterMethods()
    {
        var originalMethods = new ConcurrentDictionary<string, IMethodNode>();
        originalMethods["Svc.TryGet(string, Foo)"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "Svc.TryGet(string, Foo)",
            Name = "TryGet",
            ContainingType = "Svc",
            ContainingNamespace = "",
            ReturnType = "bool",
            Parameters = new List<MethodParameter>
            {
                new() { Type = "string", Name = "key" },
                new() { Type = "Foo", Name = "value", RefKind = "out" }
            },
            FilePath = "/test/Svc.cs",
            StartLine = 1,
            EndLine = 5
        };

        var asyncMethods = new ConcurrentDictionary<string, IMethodNode>();
        asyncMethods["Svc.TryGet(string, Foo)"] = (MethodNode)originalMethods["Svc.TryGet(string, Foo)"] with
        {
            ReturnType = "Task<bool>"
        };

        var originalGraph = CreateCallGraphWithMethods(originalMethods);
        var asyncGraph = CreateFloodedCallGraphWithMetadata(asyncMethods);

        var results = new Analyzer.OutParameterAnalyzer().DetectOutParameterMethods(originalGraph, asyncGraph);

        results.MethodMetadata.Should().HaveCount(1);
        var result = results.MethodMetadata.Values.Single();
        result.TransformKind.Should().Be(OutParameterTransformKind.BoolTryPattern);
        result.OutParameterIndices.Should().Equal(1);
        result.OutParameterTypes.Should().Equal("Foo");
        result.OutParameterNames.Should().Equal("value");
        result.NewAsyncReturnType.Should().Be("Task<AsyncOutResult<Foo>>");
    }

    [Fact]
    public void OutParameterAnalyzer_DetectsTuplePattern()
    {
        var originalMethods = new ConcurrentDictionary<string, IMethodNode>();
        originalMethods["Svc.Process(string)"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "Svc.Process(string)",
            Name = "Process",
            ContainingType = "Svc",
            ContainingNamespace = "",
            ReturnType = "int",
            Parameters = new List<MethodParameter>
            {
                new() { Type = "string", Name = "result", RefKind = "out" }
            },
            FilePath = "/test/Svc.cs",
            StartLine = 1,
            EndLine = 5
        };

        var asyncMethods = new ConcurrentDictionary<string, IMethodNode>();
        asyncMethods["Svc.Process(string)"] = (MethodNode)originalMethods["Svc.Process(string)"] with
        {
            ReturnType = "Task<int>"
        };

        var originalGraph = CreateCallGraphWithMethods(originalMethods);
        var asyncGraph = CreateFloodedCallGraphWithMetadata(asyncMethods);

        var results = new Analyzer.OutParameterAnalyzer().DetectOutParameterMethods(originalGraph, asyncGraph);

        results.MethodMetadata.Should().HaveCount(1);
        var result = results.MethodMetadata.Values.Single();
        result.TransformKind.Should().Be(OutParameterTransformKind.TuplePattern);
        result.NewAsyncReturnType.Should().Be("Task<(int Result, string result)>");
    }

    [Fact]
    public void AsyncOutResultGenerator_ProducesValidSource()
    {
        var source = AsyncOutResultGenerator.Generate("TestNs");

        source.Should().Contain("namespace TestNs;");
        source.Should().Contain("public class AsyncOutResult<T>");
        source.Should().Contain("public bool TryGetValue(out T value)");

        // Verify it compiles
        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference>();
        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        foreach (var assembly in trustedAssemblies)
        {
            var name = Path.GetFileNameWithoutExtension(assembly);

            if (name is "System.Runtime" or "System.Private.CoreLib" or "netstandard")
            {
                references.Add(MetadataReference.CreateFromFile(assembly));
            }
        }

        var compilation = CSharpCompilation.Create("Test",
            new[]
            {
                tree
            }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var diagnostics = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        diagnostics.Should().BeEmpty("AsyncOutResult<T> should compile");
    }

    [Fact]
    public void MethodNode_HasOutParameters_ReturnsTrueWhenOutParamsPresent()
    {
        var node = new MethodNode
        {
            CallGraphId = "test",
            Id = "Test.Method(string)",
            Name = "Method",
            ContainingType = "Test",
            ContainingNamespace = "",
            ReturnType = "bool",
            Parameters = new List<MethodParameter>
            {
                new() { Type = "string", Name = "key" },
                new() { Type = "string", Name = "value", RefKind = "out" }
            },
            FilePath = "/test.cs",
            StartLine = 1,
            EndLine = 3
        };

        node.HasOutParameters.Should().BeTrue();
    }

    [Fact]
    public void MethodNode_HasOutParameters_ReturnsFalseWhenNoOutParams()
    {
        var node = new MethodNode
        {
            CallGraphId = "test",
            Id = "Test.Method(string)",
            Name = "Method",
            ContainingType = "Test",
            ContainingNamespace = "",
            ReturnType = "void",
            Parameters = new List<MethodParameter>
            {
                new() { Type = "string", Name = "key" }
            },
            FilePath = "/test.cs",
            StartLine = 1,
            EndLine = 3
        };

        node.HasOutParameters.Should().BeFalse();
    }

    [Fact]
    public void MethodNode_HasOutParameters_ReturnsFalseForRefOnly()
    {
        var node = new MethodNode
        {
            CallGraphId = "test",
            Id = "Test.Method(string)",
            Name = "Method",
            ContainingType = "Test",
            ContainingNamespace = "",
            ReturnType = "void",
            Parameters = new List<MethodParameter>
            {
                new() { Type = "string", Name = "key", RefKind = "ref" }
            },
            FilePath = "/test.cs",
            StartLine = 1,
            EndLine = 3
        };

        node.HasOutParameters.Should().BeFalse();
    }

    [Fact]
    public async Task OutParamMethod_WithAwaitableCalls_DoesNotUseTaskFromResult()
    {
        // An out-param method that also has awaitable calls should use async/await,
        // NOT Task.FromResult wrapping (which produces wrong types in async methods)
        var source = @"using System.Threading.Tasks;

class Service
{
    private IRepo _repo;

    bool TryConnect(out string status)
    {
        _repo.Open();
        status = ""connected"";
        return true;
    }
}

interface IRepo
{
    Task Open();
}
";

        var tempDir = Path.Combine(Path.GetTempPath(), $"outparam_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempFile = Path.Combine(tempDir, "Service.cs");
        await File.WriteAllTextAsync(tempFile, source);

        try
        {
            var methods = new ConcurrentDictionary<string, IMethodNode>();
            var tryConnectId = "Service.TryConnect(string)";
            methods[tryConnectId] = new MethodNode
            {
                CallGraphId = "test",
                Id = tryConnectId,
                Name = "TryConnect",
                ContainingType = "Service",
                ContainingNamespace = "",
                ReturnType = "Task<bool>",
                Parameters = new List<MethodParameter>
                {
                    new() { Type = "string", Name = "status", RefKind = "out" }
                },
                FilePath = tempFile,
                StartLine = 7,
                EndLine = 12
            };
            methods["IRepo.Open()"] = new MethodNode
            {
                CallGraphId = "test",
                Id = "IRepo.Open()",
                Name = "Open",
                ContainingType = "IRepo",
                ContainingNamespace = "",
                ReturnType = "Task",
                Parameters = new List<MethodParameter>(),
                FilePath = tempFile,
                StartLine = 16,
                EndLine = 16
            };

            var calls = new ConcurrentBag<IMethodCall>();
            calls.Add(new MethodCall
            {
                CallGraphId = "test",
                Id = $"{tryConnectId}->IRepo.Open()",
                CallerId = tryConnectId,
                CalleeId = "IRepo.Open()",
                FilePath = tempFile,
                LineNumber = 9
            });

            var graph = new CallGraph("test", methods, calls);

            var result = await _transformer.TransformProjectAsync(tempDir, graph);

            result.Success.Should().BeTrue();
            var transformed = result.ModifiedFiles[0].TransformedContent;

            // Method should be async (has awaitable calls)
            transformed.Should().Contain("async Task<AsyncOutResult<string>>");
            // Should NOT contain Task.FromResult (async method returns are auto-wrapped)
            transformed.Should().NotContain("Task.FromResult");
            // Should contain await for the inner call
            transformed.Should().Contain("await");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OutParameterCallSiteRewriter_DoesNotTransformUnrelatedInvocation()
    {
        // An invocation whose callee symbol is NOT in the out-param metadata should not
        // be transformed, even if other methods are registered as out-param methods.
        var source = @"
class Client
{
    public string GetWorkflowStateAsync(string id) => id;
}

class Cache
{
    public bool TryGetValue(string key, out string value) { value = key; return true; }
}

class Caller
{
    private Client _client = new();
    private Cache _cache = new();

    void Run()
    {
        var x = _client.GetWorkflowStateAsync(""id"");
    }
}";
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        // Build a minimal compilation for semantic model
        var compilation = CSharpCompilation.Create("Test",
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semanticModel = compilation.GetSemanticModel(tree);

        // Create a call graph with out-param metadata for Cache.TryGetValue — NOT for Client.GetWorkflowStateAsync
        var methods = new ConcurrentDictionary<string, IMethodNode>();
        var calleeId = "Cache.TryGetValue(string, string)";
        methods[calleeId] = new MethodNode
        {
            CallGraphId = "test",
            Id = calleeId,
            Name = "TryGetValue",
            ContainingType = "Cache",
            ContainingNamespace = "",
            ReturnType = "Task<bool>",
            Parameters = new List<MethodParameter> { new() { Type = "string", Name = "key" }, new() { Type = "string", Name = "value", RefKind = "out" } },
            FilePath = "/test.cs",
            StartLine = 9,
            EndLine = 9
        };
        var callerId = "Caller.Run()";
        methods[callerId] = new MethodNode
        {
            CallGraphId = "test",
            Id = callerId,
            Name = "Run",
            ContainingType = "Caller",
            ContainingNamespace = "",
            ReturnType = "Task",
            Parameters = new List<MethodParameter>(),
            FilePath = "/test.cs",
            StartLine = 22,
            EndLine = 25
        };

        var baseGraph = new CallGraph("test", methods, new ConcurrentBag<IMethodCall>());
        var methodMetadata = new Dictionary<string, CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>>
        {
            [calleeId] = new()
            {
                First = new FloodingMethodMetadata
                {
                    OriginalReturnType = "bool",
                    FloodedById = "",
                    Depth = 0,
                    Reason = FloodReason.Root
                },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = new OutParameterMetadata
                {
                    OriginalReturnType = "bool",
                    TransformKind = OutParameterTransformKind.BoolTryPattern,
                    OutParameterIndices = new List<int> { 1 },
                    OutParameterTypes = new List<string> { "string" },
                    OutParameterNames = new List<string> { "value" },
                    NewAsyncReturnType = "Task<AsyncOutResult<string>>"
                }
            },
            [callerId] = new()
            {
                First = new FloodingMethodMetadata
                {
                    OriginalReturnType = "void",
                    FloodedById = "",
                    Depth = 0,
                    Reason = FloodReason.Root
                },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = OutParameterMetadata.None
            }
        };

        var callGraph = new CallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            "test", baseGraph, methodMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());

        var rewriter = new OutParameterCallSiteRewriter(semanticModel, callGraph);
        rewriter.Visit(root);

        // The rewriter should NOT have transformed anything because GetWorkflowStateAsync
        // is not in the out-param metadata — only TryGetValue is
        rewriter.AnyTransformed.Should().BeFalse();
    }

    private static CallGraph CreateCallGraphWithMethods(ConcurrentDictionary<string, IMethodNode> methods)
    {
        var calls = new ConcurrentBag<IMethodCall>();
        var graph = new CallGraph("test", methods, calls);

        return graph;
    }

    private static CallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> CreateFloodedCallGraphWithMetadata(
        ConcurrentDictionary<string, IMethodNode> methods)
    {
        var baseGraph = CreateCallGraphWithMethods(methods);
        var floodingMetadata = new Dictionary<string, FloodingMethodMetadata>();
        foreach (var (id, method) in methods)
        {
            floodingMetadata[id] = new FloodingMethodMetadata
            {
                FloodedById = null,
                Depth = 0,
                Reason = FloodReason.Root,
                OriginalReturnType = method.ReturnType
            };
        }
        return new CallGraphWithMetadata<FloodingMethodMetadata, EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            baseGraph.Id,
            baseGraph,
            floodingMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());
    }

    private static CallGraph CreateFloodedCallGraphWithOutParam(
        string tempFile,
        string methodId,
        string methodName,
        string containingType,
        string returnType,
        List<MethodParameter> parameters,
        int startLine,
        int endLine)
    {
        var methods = new ConcurrentDictionary<string, IMethodNode>();
        methods[methodId] = new MethodNode
        {
            CallGraphId = "test",
            Id = methodId,
            Name = methodName,
            ContainingType = containingType,
            ContainingNamespace = "",
            ReturnType = returnType,
            Parameters = parameters,
            FilePath = tempFile,
            StartLine = startLine,
            EndLine = endLine
        };

        var calls = new ConcurrentBag<IMethodCall>();
        var graph = new CallGraph("test", methods, calls);

        foreach (var (k, v) in methods)
        {
            graph.Methods[k] = v;
        }

        return graph;
    }
}

/// <summary>
/// Tests for <see cref="FloodedCallGraphTransformer"/> out-parameter call-site rewriting,
/// specifically the injection of a <c>using</c> directive for the <c>AsyncOutResult&lt;T&gt;</c>
/// namespace when <c>--async-out-result-namespace</c> is provided.
/// </summary>
public class FloodedCallGraphTransformerOutParamTests
{
    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Inline <see cref="IDocumentSemanticModelProvider"/> that returns a pre-built
    /// (root, semanticModel) pair for a single file path, so symbol resolution works
    /// properly without a full MSBuild workspace.
    /// </summary>
    private sealed class SingleFileSemanticModelProvider : IDocumentSemanticModelProvider
    {
        private readonly string _filePath;
        private readonly SyntaxNode _root;
        private readonly SemanticModel _semanticModel;

        public SingleFileSemanticModelProvider(string filePath, SyntaxNode root, SemanticModel semanticModel)
        {
            _filePath = filePath;
            _root = root;
            _semanticModel = semanticModel;
        }

        public Task<(SyntaxNode Root, SemanticModel SemanticModel)?> GetForFileAsync(
            string filePath, CancellationToken cancellationToken = default)
        {
            if (string.Equals(filePath, _filePath, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult<(SyntaxNode, SemanticModel)?>(((_root, _semanticModel)));
            return Task.FromResult<(SyntaxNode, SemanticModel)?>(null);
        }
    }

    /// <summary>
    /// Builds a <see cref="CSharpCompilation"/> from the given source text and file path,
    /// including references to mscorlib and System.Threading.Tasks so that Task symbols resolve.
    /// </summary>
    private static (SyntaxNode Root, SemanticModel SemanticModel) BuildSemanticModel(
        string source, string filePath)
    {
        var tree = CSharpSyntaxTree.ParseText(source, path: filePath);

        var trustedAssemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);
        var references = trustedAssemblies
            .Where(a =>
            {
                var name = Path.GetFileNameWithoutExtension(a);
                return name is "System.Runtime" or "System.Threading.Tasks" or
                              "System.Private.CoreLib" or "netstandard";
            })
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a))
            .ToList();

        var compilation = CSharpCompilation.Create("TestAsm",
            new[] { tree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var root = tree.GetRoot();
        var semanticModel = compilation.GetSemanticModel(tree);
        return (root, semanticModel);
    }

    /// <summary>
    /// Builds a flooded <see cref="CallGraphWithMetadata{...}"/> containing:
    /// <list type="bullet">
    ///   <item>A callee method with BoolTryPattern out-parameter metadata.</item>
    ///   <item>A flooded caller method that calls the callee.</item>
    /// </list>
    /// </summary>
    private static CallGraphWithMetadata<
        CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
        EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>
        BuildBoolTryPatternCallGraph(
            string filePath,
            string calleeId,
            string callerId,
            List<MethodParameter> calleeParameters,
            OutParameterMetadata outParamMeta)
    {
        var methods = new ConcurrentDictionary<string, IMethodNode>();
        methods[calleeId] = new MethodNode
        {
            CallGraphId = "test",
            Id = calleeId,
            Name = calleeId.Split('.')[1].Split('(')[0],
            ContainingType = calleeId.Split('.')[0],
            ContainingNamespace = "",
            ReturnType = "Task<bool>",
            Parameters = calleeParameters,
            FilePath = filePath,
            StartLine = 1,
            EndLine = 5
        };
        methods[callerId] = new MethodNode
        {
            CallGraphId = "test",
            Id = callerId,
            Name = callerId.Split('.')[1].Split('(')[0],
            ContainingType = callerId.Split('.')[0],
            ContainingNamespace = "",
            ReturnType = "Task",
            Parameters = new List<MethodParameter>(),
            FilePath = filePath,
            StartLine = 7,
            EndLine = 12
        };

        var calls = new ConcurrentBag<IMethodCall>();
        calls.Add(new MethodCall
        {
            CallGraphId = "test",
            Id = $"{callerId}->{calleeId}",
            CallerId = callerId,
            CalleeId = calleeId,
            FilePath = filePath,
            LineNumber = 9
        });

        var baseGraph = new CallGraph("test", methods, calls);

        var methodMetadata = new Dictionary<string, CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>>
        {
            [calleeId] = new()
            {
                First = new FloodingMethodMetadata { OriginalReturnType = "bool", FloodedById = "", Depth = 0, Reason = FloodReason.Root },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = outParamMeta
            },
            [callerId] = new()
            {
                First = new FloodingMethodMetadata { OriginalReturnType = "void", FloodedById = "", Depth = 1, Reason = FloodReason.Caller },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = OutParameterMetadata.None
            }
        };

        return new CallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            "test", baseGraph, methodMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tests
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task BoolTryPattern_WithNamespace_AddsUsingToCallerFile()
    {
        // Arrange – source with a flooded caller that calls a BoolTryPattern callee
        var source = @"using System.Threading.Tasks;

class Cache
{
    public Task<bool> TryGetValue(string key) { return Task.FromResult(true); }
}

class Caller
{
    private Cache _cache = new Cache();

    public async Task Run()
    {
        if (_cache.TryGetValue(""k"")) { }
    }
}
";
        var tempDir = Path.Combine(Path.GetTempPath(), $"fct_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "Source.cs");
        await File.WriteAllTextAsync(filePath, source);

        try
        {
            var (root, semanticModel) = BuildSemanticModel(source, filePath);
            var documentProvider = new SingleFileSemanticModelProvider(filePath, root, semanticModel);

            var calleeId = "Cache.TryGetValue(string)";
            var callerId = "Caller.Run()";
            var outParamMeta = new OutParameterMetadata
            {
                OriginalReturnType = "bool",
                TransformKind = OutParameterTransformKind.BoolTryPattern,
                OutParameterIndices = new List<int>(),
                OutParameterTypes = new List<string> { "string" },
                OutParameterNames = new List<string> { "value" },
                NewAsyncReturnType = "Task<AsyncOutResult<string>>"
            };

            var callGraph = BuildBoolTryPatternCallGraph(
                filePath, calleeId, callerId,
                new List<MethodParameter> { new() { Type = "string", Name = "key" } },
                outParamMeta);

            var transformer = new FloodedCallGraphTransformer();

            // Act
            var results = await transformer.TransformAsync(callGraph, documentProvider,
                asyncOutResultNamespace: "My.Namespace");

            // Assert – at least one file was transformed and contains the using directive
            results.Should().NotBeEmpty();
            var transformed = results[0].TransformedContent;
            transformed.Should().Contain("using My.Namespace;",
                "FloodedCallGraphTransformer should add the using directive for AsyncOutResult<T> " +
                "when a BoolTryPattern call site is rewritten and a namespace is provided");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BoolTryPattern_WithoutNamespace_DoesNotAddUsing()
    {
        // Same setup but no asyncOutResultNamespace provided
        var source = @"using System.Threading.Tasks;

class Cache
{
    public Task<bool> TryGetValue(string key) { return Task.FromResult(true); }
}

class Caller
{
    private Cache _cache = new Cache();

    public async Task Run()
    {
        if (_cache.TryGetValue(""k"")) { }
    }
}
";
        var tempDir = Path.Combine(Path.GetTempPath(), $"fct_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "Source.cs");
        await File.WriteAllTextAsync(filePath, source);

        try
        {
            var (root, semanticModel) = BuildSemanticModel(source, filePath);
            var documentProvider = new SingleFileSemanticModelProvider(filePath, root, semanticModel);

            var calleeId = "Cache.TryGetValue(string)";
            var callerId = "Caller.Run()";
            var outParamMeta = new OutParameterMetadata
            {
                OriginalReturnType = "bool",
                TransformKind = OutParameterTransformKind.BoolTryPattern,
                OutParameterIndices = new List<int>(),
                OutParameterTypes = new List<string> { "string" },
                OutParameterNames = new List<string> { "value" },
                NewAsyncReturnType = "Task<AsyncOutResult<string>>"
            };

            var callGraph = BuildBoolTryPatternCallGraph(
                filePath, calleeId, callerId,
                new List<MethodParameter> { new() { Type = "string", Name = "key" } },
                outParamMeta);

            var transformer = new FloodedCallGraphTransformer();

            // Act – no asyncOutResultNamespace
            var results = await transformer.TransformAsync(callGraph, documentProvider,
                asyncOutResultNamespace: null);

            // Assert – no My.Namespace using injected (namespace was null)
            if (results.Count > 0)
            {
                results[0].TransformedContent.Should().NotContain("using My.Namespace;");
            }
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void OutParameterCallSiteRewriter_BoolTryPattern_SetsUsedBoolTryPattern()
    {
        // Arrange – source with a caller calling a BoolTryPattern method
        var source = @"using System.Threading.Tasks;

class Cache
{
    public Task<bool> TryGetValue(string key) { return Task.FromResult(true); }
}

class Caller
{
    private Cache _cache = new Cache();

    public async Task Run()
    {
        if (_cache.TryGetValue(""k"")) { }
    }
}";
        var (root, semanticModel) = BuildSemanticModel(source, "/test/Source.cs");

        var calleeId = "Cache.TryGetValue(string)";
        var callerId = "Caller.Run()";

        var methods = new ConcurrentDictionary<string, IMethodNode>();
        methods[calleeId] = new MethodNode
        {
            CallGraphId = "test", Id = calleeId, Name = "TryGetValue",
            ContainingType = "Cache", ContainingNamespace = "",
            ReturnType = "Task<bool>",
            Parameters = new List<MethodParameter> { new() { Type = "string", Name = "key" } },
            FilePath = "/test/Source.cs", StartLine = 5, EndLine = 5
        };
        methods[callerId] = new MethodNode
        {
            CallGraphId = "test", Id = callerId, Name = "Run",
            ContainingType = "Caller", ContainingNamespace = "",
            ReturnType = "Task",
            Parameters = new List<MethodParameter>(),
            FilePath = "/test/Source.cs", StartLine = 13, EndLine = 15
        };

        var outParamMeta = new OutParameterMetadata
        {
            OriginalReturnType = "bool",
            TransformKind = OutParameterTransformKind.BoolTryPattern,
            OutParameterIndices = new List<int>(),
            OutParameterTypes = new List<string> { "string" },
            OutParameterNames = new List<string> { "value" },
            NewAsyncReturnType = "Task<AsyncOutResult<string>>"
        };

        var methodMetadata = new Dictionary<string, CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>>
        {
            [calleeId] = new()
            {
                First = new FloodingMethodMetadata { OriginalReturnType = "bool", FloodedById = "", Depth = 0, Reason = FloodReason.Root },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = outParamMeta
            },
            [callerId] = new()
            {
                First = new FloodingMethodMetadata { OriginalReturnType = "void", FloodedById = "", Depth = 1, Reason = FloodReason.Caller },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = OutParameterMetadata.None
            }
        };

        var baseGraph = new CallGraph("test", methods, new ConcurrentBag<IMethodCall>());
        var callGraph = new CallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            "test", baseGraph, methodMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());

        // Act
        var rewriter = new OutParameterCallSiteRewriter(semanticModel, callGraph);
        rewriter.Visit(root);

        // Assert
        rewriter.UsedBoolTryPattern.Should().BeTrue(
            "visiting a BoolTryPattern call site should set UsedBoolTryPattern to true");
    }

    [Fact]
    public void OutParameterCallSiteRewriter_TuplePattern_DoesNotSetUsedBoolTryPattern()
    {
        // Arrange – source with a non-bool out-param method (TuplePattern)
        var source = @"using System.Threading.Tasks;

class Processor
{
    public Task<int> Process() { return Task.FromResult(42); }
}

class Caller
{
    private Processor _p = new Processor();

    public async Task Run()
    {
        var r = _p.Process();
    }
}";
        var (root, semanticModel) = BuildSemanticModel(source, "/test/Source.cs");

        var calleeId = "Processor.Process()";
        var callerId = "Caller.Run()";

        var methods = new ConcurrentDictionary<string, IMethodNode>();
        methods[calleeId] = new MethodNode
        {
            CallGraphId = "test", Id = calleeId, Name = "Process",
            ContainingType = "Processor", ContainingNamespace = "",
            ReturnType = "Task<int>",
            Parameters = new List<MethodParameter>(),
            FilePath = "/test/Source.cs", StartLine = 5, EndLine = 5
        };
        methods[callerId] = new MethodNode
        {
            CallGraphId = "test", Id = callerId, Name = "Run",
            ContainingType = "Caller", ContainingNamespace = "",
            ReturnType = "Task",
            Parameters = new List<MethodParameter>(),
            FilePath = "/test/Source.cs", StartLine = 13, EndLine = 15
        };

        var outParamMeta = new OutParameterMetadata
        {
            OriginalReturnType = "int",
            TransformKind = OutParameterTransformKind.TuplePattern,
            OutParameterIndices = new List<int>(),
            OutParameterTypes = new List<string> { "string" },
            OutParameterNames = new List<string> { "message" },
            NewAsyncReturnType = "Task<(int Result, string message)>"
        };

        var methodMetadata = new Dictionary<string, CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>>
        {
            [calleeId] = new()
            {
                First = new FloodingMethodMetadata { OriginalReturnType = "int", FloodedById = "", Depth = 0, Reason = FloodReason.Root },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = outParamMeta
            },
            [callerId] = new()
            {
                First = new FloodingMethodMetadata { OriginalReturnType = "void", FloodedById = "", Depth = 1, Reason = FloodReason.Caller },
                Second = SyncWrapperMethodMetadata.None,
                Third = EntityFrameworkMethodMetadata.None,
                Fourth = OutParameterMetadata.None
            }
        };

        var baseGraph = new CallGraph("test", methods, new ConcurrentBag<IMethodCall>());
        var callGraph = new CallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata, OutParameterMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata>(
            "test", baseGraph, methodMetadata,
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>(),
            new Dictionary<string, EmptyGraphMetadata>());

        // Act
        var rewriter = new OutParameterCallSiteRewriter(semanticModel, callGraph);
        rewriter.Visit(root);

        // Assert
        rewriter.UsedBoolTryPattern.Should().BeFalse(
            "TuplePattern call sites do not use AsyncOutResult<T>, so UsedBoolTryPattern should remain false");
    }
}