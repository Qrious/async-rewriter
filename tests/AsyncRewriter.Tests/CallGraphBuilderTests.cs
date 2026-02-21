using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AsyncRewriter.Tests;

public class CallGraphBuilderTests
{
    private static readonly string TestCallGraphId = "Test";

    private static string LoadTestSource([CallerMemberName] string testName = "")
        => File.ReadAllText(Path.Combine("TestData", $"{testName}.cs"));

    private static async
        Task<(ConcurrentDictionary<string, IMethodNode> Methods,
            ConcurrentBag<IMethodCall> Calls,
            ConcurrentBag<IInterfaceImplementation> InterfaceImplementations,
            ConcurrentBag<IMethodOverride> MethodOverrides)> AnalyzeSource(string sourceCode)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location), MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location), MetadataReference.CreateFromFile(typeof(Expression<>).Assembly.Location),
        };

        // Add runtime assembly references
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeRef = MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll"));

        var compilation = CSharpCompilation.Create("TestAssembly",
            new[]
            {
                syntaxTree
            },
            references.Append(runtimeRef),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync();

        var methods = new ConcurrentDictionary<string, IMethodNode>();
        var calls = new ConcurrentBag<IMethodCall>();
        var interfaceImplementations = new ConcurrentBag<IInterfaceImplementation>();
        var methodOverrides = new ConcurrentBag<IMethodOverride>();

        ConcurrentDictionary<string, IMethodNode> methodsInterface = methods;
        ConcurrentBag<IInterfaceImplementation> interfaceImplementationsInterface = new ConcurrentBag<IInterfaceImplementation>(interfaceImplementations);
        ConcurrentBag<IMethodOverride> methodOverridesInterface = new ConcurrentBag<IMethodOverride>(methodOverrides);

        var methodExtractor = new MethodExtractor();
        await methodExtractor.Extract(TestCallGraphId, root, semanticModel, "test.cs", methodsInterface, interfaceImplementationsInterface, methodOverridesInterface);

        ConcurrentBag<IMethodCall> callsInterface = new ConcurrentBag<IMethodCall>(calls);
        var callExtractor = new MethodCallExtractor();
        await callExtractor.Extract(TestCallGraphId, root, semanticModel, "test.cs", methodsInterface, callsInterface);

        return (methods, calls, interfaceImplementations, methodOverrides);
    }

    [Fact]
    public async Task SimpleMethod_CreatesMethodNode()
    {
        var source = LoadTestSource("SimpleMethod");

        var (methods, _, _, _) = await AnalyzeSource(source);

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

        var (methods, _, _, _) = await AnalyzeSource(source);

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

        var (methods, calls, _, _) = await AnalyzeSource(source);

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

        var (methods, calls, _, _) = await AnalyzeSource(source);

        methods.Should().HaveCount(3);
        calls.Should().HaveCount(2);

        calls.Should().Contain(c => c.CallerId.Contains("Method1()") && c.CalleeId.Contains("Method2()"));
        calls.Should().Contain(c => c.CallerId.Contains("Method2()") && c.CalleeId.Contains("Method3()"));
    }

    [Fact]
    public async Task MultipleCallsInSameMethod_CapturesAllCalls()
    {
        var source = LoadTestSource("MultipleCallsInSameMethod");

        var (methods, calls, _, _) = await AnalyzeSource(source);

        methods.Should().HaveCount(4);
        calls.Should().HaveCount(3);

        var callerCalls = calls.Where(c => c.CallerId.Contains("CallerMethod()"));
        callerCalls.Should().HaveCount(3);
    }

    [Fact]
    public async Task ExternalMethodCall_AddsExternalMethodNode()
    {
        var source = LoadTestSource("ExternalMethodCall");

        var (methods, calls, _, _) = await AnalyzeSource(source);

        methods.Should().HaveCount(2);
        var writeLineMethod = methods.Values.FirstOrDefault(m => m.Name == "WriteLine");
        writeLineMethod.Should().NotBeNull();
        writeLineMethod!.FilePath.Should().Be("external");
    }

    [Fact]
    public async Task RecursiveMethod_CreatesCallToSelf()
    {
        var source = LoadTestSource("RecursiveMethod");

        var (methods, calls, _, _) = await AnalyzeSource(source);

        methods.Should().ContainSingle();
        calls.Should().ContainSingle();

        var call = calls.First();
        call.CallerId.Should().Be(call.CalleeId);
    }

    [Fact]
    public async Task EmptyClass_CreatesEmptyCallGraph()
    {
        var source = LoadTestSource("EmptyClass");

        var (methods, calls, _, _) = await AnalyzeSource(source);

        methods.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task GenericReturnType_CapturesGenericType()
    {
        var source = LoadTestSource("GenericReturnType");

        var (methods, _, _, _) = await AnalyzeSource(source);

        var method = methods.Values.First();
        method.ReturnType.Should().Contain("List<string>");
    }

    [Fact]
    public async Task MethodWithLineNumbers_CapturesCorrectLineNumbers()
    {
        var source = LoadTestSource("MethodWithLineNumbers");

        var (methods, calls, _, _) = await AnalyzeSource(source);

        var method1 = methods.Values.First(m => m.Name == "Method1");
        method1.StartLine.Should().BeGreaterThan(0);
        method1.EndLine.Should().BeGreaterOrEqualTo(method1.StartLine);

        calls.First().LineNumber.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task LocalFunction_IsExtracted()
    {
        var source = LoadTestSource("LocalFunction");

        var (methods, _, _, _) = await AnalyzeSource(source);

        methods.Should().HaveCount(2);
        methods.Values.Should().Contain(m => m.Name == "OuterMethod");
        methods.Values.Should().Contain(m => m.Name == "LocalFunc");
    }

    [Fact]
    public async Task LocalFunction_IdContainsParentMethod()
    {
        var source = LoadTestSource("LocalFunction");

        var (methods, _, _, _) = await AnalyzeSource(source);

        var localFunc = methods.Values.First(m => m.Name == "LocalFunc");
        localFunc.Id.Should().Contain("OuterMethod()");
        localFunc.Id.Should().Contain("LocalFunc()");
    }

    [Fact]
    public async Task LocalFunction_CallFromParent_CreatesMethodCall()
    {
        var source = LoadTestSource("LocalFunction");

        var (methods, calls, _, _) = await AnalyzeSource(source);

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

        var (methods, calls, _, _) = await AnalyzeSource(source);

        // OuterMethod calls LocalFunc, LocalFunc calls WriteLine
        calls.Should().Contain(c => c.CallerId.Contains("LocalFunc()") && c.CalleeId.Contains("WriteLine"));
        calls.Should().Contain(c => c.CallerId.Contains("OuterMethod()") && c.CalleeId.Contains("LocalFunc()"));
    }

    [Fact]
    public async Task NestedLocalFunction_IdContainsFullChain()
    {
        var source = LoadTestSource("NestedLocalFunction");

        var (methods, _, _, _) = await AnalyzeSource(source);

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

        var (methods, _, _, _) = await AnalyzeSource(source);

        var localFunc = methods.Values.First(m => m.Name == "Add");
        localFunc.Parameters.Should().HaveCount(2);
        localFunc.Parameters.Should().Contain("int a");
        localFunc.Parameters.Should().Contain("int b");
        localFunc.ReturnType.Should().Be("int");
    }

    [Fact]
    public async Task LambdaCallsMethod()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        // Should have OuterMethod, InnerMethod, and the lambda
        methods.Values.Should().Contain(m => m.Name == "OuterMethod");
        methods.Values.Should().Contain(m => m.Name == "InnerMethod");
        methods.Values.Should().Contain(m => m.Id.Contains(">b__") && m.Id.Contains("<"));

        // Call from lambda to InnerMethod
        var lambdaMethod = methods.Values.First(m => m.Id.Contains(">b__") && m.Id.Contains("<"));
        calls.Should().Contain(c => c.CallerId == lambdaMethod.Id && c.CalleeId.Contains("InnerMethod()"));
    }

    [Fact]
    public async Task ParenthesizedLambdaWithParameters()
    {
        var source = LoadTestSource();

        var (methods, _, _, _) = await AnalyzeSource(source);

        var lambdaMethod = methods.Values.First(m => m.Id.Contains(">b__") && m.Id.Contains("<"));
        lambdaMethod.Parameters.Should().HaveCount(2);
        lambdaMethod.Parameters.Should().Contain("int a");
        lambdaMethod.Parameters.Should().Contain("int b");
    }

    [Fact]
    public async Task LambdaChainedCalls()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        // Should have OuterMethod, MiddleMethod, InnerMethod, and two lambdas
        methods.Values.Where(m => m.Id.Contains(">b__") && m.Id.Contains("<")).Should().HaveCount(2);

        // Lambda in OuterMethod calls MiddleMethod
        calls.Should().Contain(c => c.CallerId.Contains(">b__") && c.CalleeId.Contains("MiddleMethod()"));
        // Lambda in MiddleMethod calls InnerMethod
        calls.Should().Contain(c => c.CallerId.Contains(">b__") && c.CalleeId.Contains("InnerMethod()"));
    }

    [Fact]
    public async Task LambdaDirectInvocation()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        var lambdaMethod = methods.Values.First(m => m.Id.Contains(">b__"));

        // OuterMethod calls the lambda (contains it)
        calls.Should().Contain(c =>
            c.CallerId.Contains("OuterMethod()") && !c.CallerId.Contains(">b__")
                                                 && c.CalleeId == lambdaMethod.Id);

        // Lambda calls InnerMethod
        calls.Should().Contain(c =>
            c.CallerId == lambdaMethod.Id
            && c.CalleeId.Contains("InnerMethod()"));
    }

    [Fact]
    public async Task LambdaPassedToMethod()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        var lambdaMethod = methods.Values.First(m => m.Id.Contains(">b__"));

        // OuterMethod calls Executor.Run (passing the lambda)
        calls.Should().Contain(c =>
            c.CallerId.Contains("OuterMethod()") && !c.CallerId.Contains(">b__")
                                                 && c.CalleeId.Contains("Run("));

        // OuterMethod calls the lambda (contains it)
        calls.Should().Contain(c =>
            c.CallerId.Contains("OuterMethod()") && !c.CallerId.Contains(">b__")
                                                 && c.CalleeId == lambdaMethod.Id);

        // Lambda calls InnerMethod
        calls.Should().Contain(c =>
            c.CallerId == lambdaMethod.Id
            && c.CalleeId.Contains("InnerMethod()"));
    }

    [Fact]
    public async Task LinqSelectAndWhere()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        // FilterAndProject method and two lambdas (Where predicate, Select projection)
        methods.Values.Should().Contain(m => m.Name == "FilterAndProject");
        var lambdas = methods.Values.Where(m => m.Id.Contains(">b__")).ToList();
        lambdas.Should().HaveCount(2);

        // FilterAndProject calls Where and Select
        calls.Should().Contain(c =>
            c.CallerId.Contains("FilterAndProject(") && !c.CallerId.Contains(">b__")
                                                     && c.CalleeId.Contains("Where("));
        calls.Should().Contain(c =>
            c.CallerId.Contains("FilterAndProject(") && !c.CallerId.Contains(">b__")
                                                     && c.CalleeId.Contains("Select("));

        // FilterAndProject contains both lambdas
        foreach (var lambda in lambdas)
        {
            calls.Should().Contain(c =>
                c.CallerId.Contains("FilterAndProject(") && !c.CallerId.Contains(">b__")
                                                         && c.CalleeId == lambda.Id);
        }
    }

    [Fact]
    public async Task LambdaFromClassField()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        // The field initializer lambda is extracted as a method node
        var lambda = methods.Values.FirstOrDefault(m => m.Id.Contains(">b__"));
        lambda.Should().NotBeNull();

        // FilterNumbers calls Where
        calls.Should().Contain(c =>
            c.CallerId.Contains("FilterNumbers(") && !c.CallerId.Contains(">b__")
                                                  && c.CalleeId.Contains("Where("));
    }

    [Fact]
    public async Task LambdaPassedViaConstructor()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        var lambda = methods.Values.First(m => m.Id.Contains(">b__") && m.Id.Contains("<"));

        // Setup contains the lambda
        calls.Should().Contain(c =>
            c.CallerId.Contains("Setup()") && !c.CallerId.Contains(">b__")
                                           && c.CalleeId == lambda.Id);

        // Lambda calls DoWork
        calls.Should().Contain(c =>
            c.CallerId == lambda.Id
            && c.CalleeId.Contains("DoWork()"));

        // Executor.Execute invokes the lambda through the delegate field
        calls.Should().Contain(c =>
            c.CallerId.Contains("Executor.Execute()")
            && c.CalleeId == lambda.Id);
    }

    [Fact]
    public async Task LambdaGenericConstructorChained()
    {
        var source = LoadTestSource();

        var (methods, calls, _, _) = await AnalyzeSource(source);

        var lambda = methods.Values.First(m => m.Id.Contains(">b__"));

        // Test contains the lambda
        calls.Should().Contain(c =>
            c.CallerId.Contains("Test()") && !c.CallerId.Contains(">b__")
                                          && c.CalleeId == lambda.Id);

        // Test calls Execute
        calls.Should().Contain(c =>
            c.CallerId.Contains("Test()") && !c.CallerId.Contains(">b__")
                                          && c.CalleeId.Contains("Execute()"));

        // Execute calls the lambda through the delegate field
        calls.Should().Contain(c =>
            c.CallerId.Contains("Execute()")
            && c.CalleeId == lambda.Id);
    }

    /// <summary>
    /// Analyzes consumer source that references a separate library compilation,
    /// simulating a multi-project solution where types are defined in a different project.
    /// </summary>
    private static async Task<(ConcurrentDictionary<string, IMethodNode> Methods, ConcurrentBag<IMethodCall> Calls)> AnalyzeSourceWithReference(string consumerSource,
        string librarySource)
    {
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location), MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location), MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location), MetadataReference.CreateFromFile(typeof(Expression<>).Assembly.Location),
        };
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var runtimeRef = MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll"));
        var allRefs = references.Append(runtimeRef).Cast<MetadataReference>().ToArray();

        // Build the library compilation
        var libTree = CSharpSyntaxTree.ParseText(librarySource, path: "library.cs");
        var libCompilation = CSharpCompilation.Create("LibraryAssembly",
            new[]
            {
                libTree
            },
            allRefs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        // Build the consumer compilation referencing the library
        var consumerTree = CSharpSyntaxTree.ParseText(consumerSource, path: "consumer.cs");
        var consumerCompilation = CSharpCompilation.Create("ConsumerAssembly",
            new[]
            {
                consumerTree
            },
            allRefs.Append(libCompilation.ToMetadataReference()),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var methods = new ConcurrentDictionary<string, IMethodNode>();
        var calls = new ConcurrentBag<IMethodCall>();
        var implementations = new ConcurrentBag<IInterfaceImplementation>();
        var overrides = new ConcurrentBag<IMethodOverride>();

        // Build a cross-compilation resolver
        var resolver = new MultiCompilationSemanticModelResolver(libCompilation, consumerCompilation);

        // Extract methods from both compilations
        foreach (var (compilation, tree) in new[]
                 {
                     (libCompilation, libTree), (consumerCompilation, consumerTree)
                 })
        {
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync();
            await new MethodExtractor().Extract(TestCallGraphId, root, model, tree.FilePath, methods, implementations, overrides);
        }

        // Extract calls from both compilations with the cross-compilation resolver
        foreach (var (compilation, tree) in new[]
                 {
                     (libCompilation, libTree), (consumerCompilation, consumerTree)
                 })
        {
            var model = compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync();
            await new MethodCallExtractor().Extract(TestCallGraphId, root, model, tree.FilePath, methods, calls, resolver);
        }

        return (methods, calls);
    }

    [Fact]
    public async Task LambdaGenericConstructorChainedCrossProject()
    {
        var librarySource = File.ReadAllText(Path.Combine("TestData", "LambdaGenericConstructorChainedCrossProject_Library.cs"));
        var consumerSource = File.ReadAllText(Path.Combine("TestData", "LambdaGenericConstructorChainedCrossProject_Consumer.cs"));

        var (methods, calls) = await AnalyzeSourceWithReference(consumerSource, librarySource);

        var lambda = methods.Values.First(m => m.Id.Contains(">b__"));

        // Test contains the lambda
        calls.Should().Contain(c =>
            c.CallerId.Contains("Test()") && !c.CallerId.Contains(">b__")
                                          && c.CalleeId == lambda.Id);

        // Test calls Execute
        calls.Should().Contain(c =>
            c.CallerId.Contains("Test()") && !c.CallerId.Contains(">b__")
                                          && c.CalleeId.Contains("Execute()"));

        // Execute calls the lambda through the delegate field (cross-project)
        calls.Should().Contain(c =>
            c.CallerId.Contains("Execute()")
            && c.CalleeId == lambda.Id);
    }

    [Fact]
    public async Task InterfaceMethod_IsExtracted()
    {
        var source = LoadTestSource("InterfaceMethod");

        var (methods, _, _, _) = await AnalyzeSource(source);

        methods.Should().ContainSingle();
        var method = methods.Values.First();
        method.Name.Should().Be("DoWork");
        method.ContainingType.Should().Contain("IService");
    }

    [Fact]
    public async Task InterfaceImplementation_CreatesImplementsRecords()
    {
        var source = LoadTestSource("InterfaceImplementation");

        var (methods, _, implementations, _) = await AnalyzeSource(source);

        // Should have interface methods and implementation methods
        methods.Values.Should().Contain(m => m.Name == "DoWork" && m.ContainingType.Contains("IService"));
        methods.Values.Should().Contain(m => m.Name == "DoWork" && m.ContainingType.Contains("ServiceImpl"));
        methods.Values.Should().Contain(m => m.Name == "Calculate" && m.ContainingType.Contains("IService"));
        methods.Values.Should().Contain(m => m.Name == "Calculate" && m.ContainingType.Contains("ServiceImpl"));

        // Should have two InterfaceImplementation records
        implementations.Should().HaveCount(2);

        implementations.Should().Contain(i =>
            i.ImplementingMethodId.Contains("ServiceImpl") && i.ImplementingMethodId.Contains("DoWork")
                                                           && i.InterfaceMethodId.Contains("IService") && i.InterfaceMethodId.Contains("DoWork"));

        implementations.Should().Contain(i =>
            i.ImplementingMethodId.Contains("ServiceImpl") && i.ImplementingMethodId.Contains("Calculate")
                                                           && i.InterfaceMethodId.Contains("IService") && i.InterfaceMethodId.Contains("Calculate"));
    }

    [Fact]
    public async Task SimpleOverride_CreatesOverrideRecord()
    {
        var source = LoadTestSource("SimpleOverride");

        var (methods, _, _, overrides) = await AnalyzeSource(source);

        methods.Values.Should().Contain(m => m.Name == "DoWork" && m.ContainingType.Contains("BaseClass"));
        methods.Values.Should().Contain(m => m.Name == "DoWork" && m.ContainingType.Contains("DerivedClass"));

        overrides.Should().ContainSingle();
        overrides.Should().Contain(o =>
            o.OverridingMethodId.Contains("DerivedClass") && o.OverridingMethodId.Contains("DoWork")
                                                          && o.BaseMethodId.Contains("BaseClass") && o.BaseMethodId.Contains("DoWork"));
    }

    [Fact]
    public async Task AbstractOverride_CreatesOverrideRecord()
    {
        var source = LoadTestSource("AbstractOverride");

        var (methods, _, _, overrides) = await AnalyzeSource(source);

        methods.Values.Should().Contain(m => m.Name == "Calculate" && m.ContainingType.Contains("AbstractBase"));
        methods.Values.Should().Contain(m => m.Name == "Calculate" && m.ContainingType.Contains("ConcreteClass"));

        overrides.Should().ContainSingle();
        overrides.Should().Contain(o =>
            o.OverridingMethodId.Contains("ConcreteClass") && o.OverridingMethodId.Contains("Calculate")
                                                           && o.BaseMethodId.Contains("AbstractBase") && o.BaseMethodId.Contains("Calculate"));
    }

    [Fact]
    public async Task ExpressionTreeLambda_DoesNotCreateCallEdges()
    {
        var source = LoadTestSource("ExpressionTreeLambda");

        var (methods, calls, _, _) = await AnalyzeSource(source);

        // The expression tree lambda should NOT create a call edge to GetValue
        // (simulates FakeItEasy A.CallTo, Moq Setup, etc.)
        calls.Should().NotContain(c =>
            c.CallerId.Contains("TestMethod") && c.CallerId.Contains(">b__")
                                              && c.CalleeId.Contains("GetValue"));

        // The regular (non-expression) lambda SHOULD create a call edge to GetValue
        var regularLambda = methods.Values.FirstOrDefault(m =>
            m.Id.Contains(">b__") && calls.Any(c =>
                c.CallerId.Contains("RegularLambdaMethod") && !c.CallerId.Contains(">b__")
                                                           && c.CalleeId == m.Id));
        regularLambda.Should().NotBeNull("regular lambda should be recorded");
        calls.Should().Contain(c =>
            c.CallerId == regularLambda!.Id && c.CalleeId.Contains("GetValue"));
    }

    [Fact]
    public async Task MultiLevelOverride_CreatesOverrideChain()
    {
        var source = LoadTestSource("MultiLevelOverride");

        var (methods, _, _, overrides) = await AnalyzeSource(source);

        methods.Values.Should().Contain(m => m.Name == "Process" && m.ContainingType.Contains("GrandParent"));
        methods.Values.Should().Contain(m => m.Name == "Process" && m.ContainingType.Contains("Parent"));
        methods.Values.Should().Contain(m => m.Name == "Process" && m.ContainingType.Contains("Child"));

        // Parent overrides GrandParent
        overrides.Should().Contain(o =>
            o.OverridingMethodId.Contains("Parent") && o.BaseMethodId.Contains("GrandParent"));

        // Child overrides Parent (direct) and GrandParent (transitive via OverriddenMethod chain)
        overrides.Should().Contain(o =>
            o.OverridingMethodId.Contains("Child") && o.BaseMethodId.Contains("Parent"));
        overrides.Should().Contain(o =>
            o.OverridingMethodId.Contains("Child") && o.BaseMethodId.Contains("GrandParent"));
    }
}

file class MultiCompilationSemanticModelResolver : ISemanticModelResolver
{
    private readonly Compilation[] _compilations;

    public MultiCompilationSemanticModelResolver(params Compilation[] compilations)
    {
        _compilations = compilations;
    }

    public SemanticModel? Resolve(SyntaxTree syntaxTree)
    {
        foreach (var compilation in _compilations)
        {
            if (compilation.ContainsSyntaxTree(syntaxTree))
            {
                return compilation.GetSemanticModel(syntaxTree);
            }
        }

        return null;
    }
}