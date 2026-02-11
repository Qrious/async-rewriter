using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AsyncRewriter.Tests;

public class CallGraphBuilderTests
{
    private static readonly Guid TestCallGraphId = Guid.NewGuid();

    private static string LoadTestSource([CallerMemberName] string testName = "")
        => File.ReadAllText(Path.Combine("TestData", $"{testName}.cs"));

    private static async Task<(ConcurrentDictionary<string, MethodNode> Methods, ConcurrentBag<MethodCall> Calls)> AnalyzeSource(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
        };

        // Add runtime assembly references
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeRef = MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll"));

        var compilation = CSharpCompilation.Create("TestAssembly",
            new[] { syntaxTree },
            references.Append(runtimeRef),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();

        var methods = new ConcurrentDictionary<string, MethodNode>();
        var calls = new ConcurrentBag<MethodCall>();

        var methodExtractor = new MethodExtractor();
        await methodExtractor.Extract(TestCallGraphId, root, semanticModel, "test.cs", methods);

        var callExtractor = new MethodCallExtractor();
        await callExtractor.Extract(TestCallGraphId, root, semanticModel, "test.cs", methods, calls);

        return (methods, calls);
    }

    [Fact]
    public async Task SimpleMethod_CreatesMethodNode()
    {
        var source = LoadTestSource("SimpleMethod");

        var (methods, _) = await AnalyzeSource(source);

        methods.Should().ContainSingle();
        var method = methods.Values.First();
        method.Name.Should().Be("TestMethod");
        method.ContainingType.Should().Be("TestNamespace.TestClass");
        method.ReturnType.Should().Be("void");
    }

    [Fact]
    public async Task MethodWithParameters_CapturesParameters()
    {
        var source = LoadTestSource("MethodWithParameters");

        var (methods, _) = await AnalyzeSource(source);

        var method = methods.Values.First();
        method.Parameters.Should().HaveCount(2);
        method.Parameters.Should().Contain("int x");
        method.Parameters.Should().Contain("string name");
        method.ReturnType.Should().Be("int");
    }

    [Fact]
    public async Task MethodCallsAnotherMethod_CreatesMethodCall()
    {
        var source = LoadTestSource("MethodCallsAnotherMethod");

        var (methods, calls) = await AnalyzeSource(source);

        methods.Should().HaveCount(2);
        calls.Should().ContainSingle();

        var call = calls.First();
        call.CallerId.Should().Contain("CallerMethod()");
        call.CalleeId.Should().Contain("CalleeMethod()");
    }

    [Fact]
    public async Task ChainedMethodCalls_CreatesMultipleCalls()
    {
        var source = LoadTestSource("ChainedMethodCalls");

        var (methods, calls) = await AnalyzeSource(source);

        methods.Should().HaveCount(3);
        calls.Should().HaveCount(2);

        calls.Should().Contain(c => c.CallerId.Contains("Method1()") && c.CalleeId.Contains("Method2()"));
        calls.Should().Contain(c => c.CallerId.Contains("Method2()") && c.CalleeId.Contains("Method3()"));
    }

    [Fact]
    public async Task MultipleCallsInSameMethod_CapturesAllCalls()
    {
        var source = LoadTestSource("MultipleCallsInSameMethod");

        var (methods, calls) = await AnalyzeSource(source);

        methods.Should().HaveCount(4);
        calls.Should().HaveCount(3);

        var callerCalls = calls.Where(c => c.CallerId.Contains("CallerMethod()"));
        callerCalls.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExternalMethodCall_AddsExternalMethodNode()
    {
        var source = LoadTestSource("ExternalMethodCall");

        var (methods, calls) = await AnalyzeSource(source);

        methods.Should().HaveCount(2);
        var writeLineMethod = methods.Values.FirstOrDefault(m => m.Name == "WriteLine");
        writeLineMethod.Should().NotBeNull();
        writeLineMethod!.FilePath.Should().Be("external");
    }

    [Fact]
    public async Task RecursiveMethod_CreatesCallToSelf()
    {
        var source = LoadTestSource("RecursiveMethod");

        var (methods, calls) = await AnalyzeSource(source);

        methods.Should().ContainSingle();
        calls.Should().ContainSingle();

        var call = calls.First();
        call.CallerId.Should().Be(call.CalleeId);
    }

    [Fact]
    public async Task EmptyClass_CreatesEmptyCallGraph()
    {
        var source = LoadTestSource("EmptyClass");

        var (methods, calls) = await AnalyzeSource(source);

        methods.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GenericReturnType_CapturesGenericType()
    {
        var source = LoadTestSource("GenericReturnType");

        var (methods, _) = await AnalyzeSource(source);

        var method = methods.Values.First();
        method.ReturnType.Should().Contain("List<string>");
    }

    [Fact]
    public async Task MethodWithLineNumbers_CapturesCorrectLineNumbers()
    {
        var source = LoadTestSource("MethodWithLineNumbers");

        var (methods, calls) = await AnalyzeSource(source);

        var method1 = methods.Values.First(m => m.Name == "Method1");
        method1.StartLine.Should().BeGreaterThan(0);
        method1.EndLine.Should().BeGreaterOrEqualTo(method1.StartLine);

        calls.First().LineNumber.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LocalFunction_IsExtracted()
    {
        var source = LoadTestSource("LocalFunction");

        var (methods, _) = await AnalyzeSource(source);

        methods.Should().HaveCount(2);
        methods.Values.Should().Contain(m => m.Name == "OuterMethod");
        methods.Values.Should().Contain(m => m.Name == "LocalFunc");
    }

    [Fact]
    public async Task LocalFunction_IdContainsParentMethod()
    {
        var source = LoadTestSource("LocalFunction");

        var (methods, _) = await AnalyzeSource(source);

        var localFunc = methods.Values.First(m => m.Name == "LocalFunc");
        localFunc.Id.Should().Contain("OuterMethod()");
        localFunc.Id.Should().Contain("LocalFunc()");
    }

    [Fact]
    public async Task LocalFunction_CallFromParent_CreatesMethodCall()
    {
        var source = LoadTestSource("LocalFunction");

        var (methods, calls) = await AnalyzeSource(source);

        calls.Should().ContainSingle();
        var call = calls.First();
        call.CallerId.Should().Contain("OuterMethod()");
        call.CallerId.Should().NotContain("LocalFunc");
        call.CalleeId.Should().Contain("LocalFunc()");
    }

    [Fact]
    public async Task LocalFunction_CallsExternalMethod_CreatesMethodCall()
    {
        var source = LoadTestSource("LocalFunctionCallsExternal");

        var (methods, calls) = await AnalyzeSource(source);

        // OuterMethod calls LocalFunc, LocalFunc calls WriteLine
        calls.Should().Contain(c => c.CallerId.Contains("LocalFunc()") && c.CalleeId.Contains("WriteLine"));
        calls.Should().Contain(c => c.CallerId.Contains("OuterMethod()") && c.CalleeId.Contains("LocalFunc()"));
    }

    [Fact]
    public async Task NestedLocalFunction_IdContainsFullChain()
    {
        var source = LoadTestSource("NestedLocalFunction");

        var (methods, _) = await AnalyzeSource(source);

        methods.Should().HaveCount(3);

        var inner = methods.Values.First(m => m.Name == "Inner");
        inner.Id.Should().Contain("OuterMethod()");
        inner.Id.Should().Contain("Middle()");
        inner.Id.Should().Contain("Inner()");
    }

    [Fact]
    public async Task LocalFunctionWithParameters_CapturesParameters()
    {
        var source = LoadTestSource("LocalFunctionWithParameters");

        var (methods, _) = await AnalyzeSource(source);

        var localFunc = methods.Values.First(m => m.Name == "Add");
        localFunc.Parameters.Should().HaveCount(2);
        localFunc.Parameters.Should().Contain("int a");
        localFunc.Parameters.Should().Contain("int b");
        localFunc.ReturnType.Should().Be("int");
    }

    [Fact]
    public async Task InterfaceMethod_IsExtracted()
    {
        var source = LoadTestSource("InterfaceMethod");

        var (methods, _) = await AnalyzeSource(source);

        methods.Should().ContainSingle();
        var method = methods.Values.First();
        method.Name.Should().Be("DoWork");
        method.ContainingType.Should().Contain("IService");
    }
}
