using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AsyncRewriter.Tests;

public class MissingAwaitRewriterTests
{
    private static string Rewrite(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            "__test__",
            syntaxTrees: [tree],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();

        var rewriter = new MissingAwaitRewriter(semanticModel);
        return rewriter.Visit(root)!.ToFullString();
    }

    [Fact]
    public void Awaits_task_returning_invocation_in_expression_statement()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    async Task M() {
        DoSomethingAsync();
    }
    Task DoSomethingAsync() => Task.CompletedTask;
}";
        var result = Rewrite(source);
        result.Should().Contain("await DoSomethingAsync()");
    }

    [Fact]
    public void Awaits_generic_task_returning_invocation()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    async Task M() {
        var x = GetValueAsync();
    }
    Task<int> GetValueAsync() => Task.FromResult(1);
}";
        var result = Rewrite(source);
        result.Should().Contain("await GetValueAsync()");
    }

    [Fact]
    public void Does_not_double_await_already_awaited_call()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    async Task M() {
        await DoSomethingAsync();
    }
    Task DoSomethingAsync() => Task.CompletedTask;
}";
        var result = Rewrite(source);
        result.Should().NotContain("await await");
    }

    [Fact]
    public void Does_not_rewrite_return_passthrough()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    Task M() {
        return DoSomethingAsync();
    }
    Task DoSomethingAsync() => Task.CompletedTask;
}";
        var result = Rewrite(source);
        // Direct return passthrough should remain untouched.
        result.Should().Contain("return DoSomethingAsync()");
        result.Should().NotContain("await DoSomethingAsync()");
    }

    [Fact]
    public void Does_not_rewrite_sync_method()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    void M() {
        var x = GetValue();
    }
    int GetValue() => 42;
}";
        var result = Rewrite(source);
        result.Should().NotContain("await");
    }

    [Fact]
    public void Parenthesizes_await_when_chain_continues()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    async Task M() {
        var name = GetItemAsync().Name;
    }
    Task<Item> GetItemAsync() => Task.FromResult(new Item());
    class Item { public string Name { get; set; } }
}";
        var result = Rewrite(source);
        result.Should().Contain("(await GetItemAsync()).Name");
    }

    [Fact]
    public void Adds_async_modifier_to_method_that_gains_await()
    {
        var source = @"
using System.Threading.Tasks;
class C {
    Task M() {
        DoSomethingAsync();
        return Task.CompletedTask;
    }
    Task DoSomethingAsync() => Task.CompletedTask;
}";
        var result = Rewrite(source);
        result.Should().Contain("async");
        result.Should().Contain("await DoSomethingAsync()");
    }

    [Fact]
    public void Adds_async_to_lambda_that_gains_await()
    {
        var source = @"
using System;
using System.Threading.Tasks;
class C {
    void M() {
        Action a = () => {
            DoSomethingAsync();
        };
    }
    Task DoSomethingAsync() => Task.CompletedTask;
}";
        var result = Rewrite(source);
        result.Should().Contain("async ()");
        result.Should().Contain("await DoSomethingAsync()");
    }
}
