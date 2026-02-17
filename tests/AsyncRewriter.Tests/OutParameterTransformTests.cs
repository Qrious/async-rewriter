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

public class OutParameterTransformTests
{
    private readonly AsyncTransformer _transformer = new();

    private static void AssertCompiles(string source, string? extraStubs = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var trees = new List<SyntaxTree> { syntaxTree, CSharpSyntaxTree.ParseText(StubTypes) };
        if (extraStubs != null)
            trees.Add(CSharpSyntaxTree.ParseText(extraStubs));

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
                parameters: new List<string> { "string key", "string value" },
                paramRefKinds: new List<string?> { null, "out" },
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
                parameters: new List<string> { "string message" },
                paramRefKinds: new List<string?> { "out" },
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
                parameters: new List<string> { "string key", "string name", "int age" },
                paramRefKinds: new List<string?> { null, "out", "out" },
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
                parameters: new List<string> { "string key", "string value" },
                paramRefKinds: new List<string?> { null, "out" },
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
                parameters: new List<string> { "string key", "string value" },
                paramRefKinds: new List<string?> { null, "out" },
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
    public void OutParameterAnalyzer_DetectsOutParameterMethods()
    {
        var originalMethods = new ConcurrentDictionary<string, MethodNode>();
        originalMethods["Svc.TryGet(string, Foo)"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "Svc.TryGet(string, Foo)",
            Name = "TryGet",
            ContainingType = "Svc",
            ContainingNamespace = "",
            ReturnType = "bool",
            Parameters = new List<string> { "string key", "Foo value" },
            ParameterRefKinds = new List<string?> { null, "out" },
            FilePath = "/test/Svc.cs",
            StartLine = 1,
            EndLine = 5
        };

        var asyncMethods = new ConcurrentDictionary<string, MethodNode>();
        asyncMethods["Svc.TryGet(string, Foo)"] = originalMethods["Svc.TryGet(string, Foo)"] with
        {
            ReturnType = "Task<bool>"
        };

        var originalGraph = CreateCallGraphWithMethods(originalMethods);
        var asyncGraph = CreateCallGraphWithMethods(asyncMethods);

        var results = Analyzer.OutParameterAnalyzer.DetectOutParameterMethods(originalGraph, asyncGraph);

        results.Should().HaveCount(1);
        results[0].TransformKind.Should().Be(OutParameterTransformKind.BoolTryPattern);
        results[0].OutParameterIndices.Should().Equal(1);
        results[0].OutParameterTypes.Should().Equal("Foo");
        results[0].OutParameterNames.Should().Equal("value");
        results[0].NewAsyncReturnType.Should().Be("Task<AsyncOutResult<Foo>>");
    }

    [Fact]
    public void OutParameterAnalyzer_DetectsTuplePattern()
    {
        var originalMethods = new ConcurrentDictionary<string, MethodNode>();
        originalMethods["Svc.Process(string)"] = new MethodNode
        {
            CallGraphId = "test",
            Id = "Svc.Process(string)",
            Name = "Process",
            ContainingType = "Svc",
            ContainingNamespace = "",
            ReturnType = "int",
            Parameters = new List<string> { "string result" },
            ParameterRefKinds = new List<string?> { "out" },
            FilePath = "/test/Svc.cs",
            StartLine = 1,
            EndLine = 5
        };

        var asyncMethods = new ConcurrentDictionary<string, MethodNode>();
        asyncMethods["Svc.Process(string)"] = originalMethods["Svc.Process(string)"] with
        {
            ReturnType = "Task<int>"
        };

        var originalGraph = CreateCallGraphWithMethods(originalMethods);
        var asyncGraph = CreateCallGraphWithMethods(asyncMethods);

        var results = Analyzer.OutParameterAnalyzer.DetectOutParameterMethods(originalGraph, asyncGraph);

        results.Should().HaveCount(1);
        results[0].TransformKind.Should().Be(OutParameterTransformKind.TuplePattern);
        results[0].NewAsyncReturnType.Should().Be("Task<(int Result, string result)>");
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
                references.Add(MetadataReference.CreateFromFile(assembly));
        }

        var compilation = CSharpCompilation.Create("Test",
            new[] { tree }, references,
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
            Parameters = new List<string> { "string key", "string value" },
            ParameterRefKinds = new List<string?> { null, "out" },
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
            Parameters = new List<string> { "string key" },
            ParameterRefKinds = null,
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
            Parameters = new List<string> { "string key" },
            ParameterRefKinds = new List<string?> { "ref" },
            FilePath = "/test.cs",
            StartLine = 1,
            EndLine = 3
        };

        node.HasOutParameters.Should().BeFalse();
    }

    private static CallGraph CreateCallGraphWithMethods(ConcurrentDictionary<string, MethodNode> methods)
    {
        var calls = new ConcurrentBag<MethodCall>();
        var graph = new CallGraph(calls);
        foreach (var (k, v) in methods)
            graph.Methods[k] = v;
        return graph;
    }

    private static CallGraph CreateFloodedCallGraphWithOutParam(
        string tempFile,
        string methodId,
        string methodName,
        string containingType,
        string returnType,
        List<string> parameters,
        List<string?> paramRefKinds,
        int startLine,
        int endLine)
    {
        var methods = new ConcurrentDictionary<string, MethodNode>();
        methods[methodId] = new MethodNode
        {
            CallGraphId = "test",
            Id = methodId,
            Name = methodName,
            ContainingType = containingType,
            ContainingNamespace = "",
            ReturnType = returnType,
            Parameters = parameters,
            ParameterRefKinds = paramRefKinds,
            FilePath = tempFile,
            StartLine = startLine,
            EndLine = endLine
        };

        var calls = new ConcurrentBag<MethodCall>();
        var graph = new CallGraph(calls) { ProjectName = "test-async" };
        foreach (var (k, v) in methods)
            graph.Methods[k] = v;
        return graph;
    }
}
