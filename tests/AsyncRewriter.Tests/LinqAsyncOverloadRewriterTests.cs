using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace AsyncRewriter.Tests;

public class LinqAsyncOverloadRewriterTests
{
    private static string Rewrite(string source, string ns = "MyProject.Linq")
    {
        var tree = CSharpSyntaxTree.ParseText(source);

        var compilation = CSharpCompilation.Create(
            "__test__",
            syntaxTrees: [tree],
            references:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            ],
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetRoot();
        var rewriter = new LinqAsyncOverloadRewriter(ns, semanticModel);
        return rewriter.Visit(root)!.ToFullString();
    }

    // ── explicit async lambda ─────────────────────────────────────────────────

    [Fact]
    public void Rewrites_Select_with_explicit_async_lambda()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(async x => await FooAsync(x));
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().Contain("SelectAsync(async x => await FooAsync(x))");
        result.Should().Contain("await items.SelectAsync");
        result.Should().Contain("using MyProject.Linq");
    }

    // ── implicit Task-returning lambda (no async keyword) ────────────────────

    [Fact]
    public void Rewrites_Select_with_implicit_task_returning_lambda()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(x => FooAsync(x));
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().Contain("SelectAsync(x => FooAsync(x))");
        result.Should().Contain("await items.SelectAsync");
    }

    // ── method group ─────────────────────────────────────────────────────────

    [Fact]
    public void Rewrites_Select_with_task_returning_method_group()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(FooAsync);
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().Contain("SelectAsync(FooAsync)");
        result.Should().Contain("await items.SelectAsync");
    }

    // ── Where ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rewrites_Where_with_task_returning_lambda()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Where(x => IsActiveAsync(x));
    }
    Task<bool> IsActiveAsync(int x) => Task.FromResult(true);
}";
        var result = Rewrite(source);

        result.Should().Contain("await items.WhereAsync(x => IsActiveAsync(x))");
    }

    // ── sync lambda — must not be rewritten ──────────────────────────────────

    [Fact]
    public void Does_not_rewrite_sync_lambda()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    void M(IEnumerable<int> items) {
        var result = items.Select(x => x * 2);
    }
}";
        var result = Rewrite(source);

        result.Should().Contain("items.Select(x => x * 2)");
        result.Should().NotContain("SelectAsync");
        result.Should().NotContain("await");
    }

    // ── already async variant — must not double-rename ───────────────────────

    [Fact]
    public void Does_not_rename_already_async_method()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.SelectAsync(async x => await FooAsync(x));
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().NotContain("SelectAsyncAsync");
    }

    // ── chain parenthesization ────────────────────────────────────────────────

    [Fact]
    public void Parenthesizes_await_when_chain_continues()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(x => FooAsync(x)).ToList();
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().Contain("(await items.SelectAsync(x => FooAsync(x))).ToList()");
    }

    // ── multi-async chain ─────────────────────────────────────────────────────

    [Fact]
    public void Handles_multi_async_chain()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Where(x => IsActiveAsync(x)).Select(x => FooAsync(x)).ToList();
    }
    Task<bool> IsActiveAsync(int x) => Task.FromResult(true);
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().Contain("WhereAsync");
        result.Should().Contain("SelectAsync");
        result.Should().Contain(").ToList()");
    }

    // ── no double-await ───────────────────────────────────────────────────────

    [Fact]
    public void Does_not_double_await_already_awaited_call()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
class C {
    async Task M(IEnumerable<int> items) {
        var result = await items.Select(x => FooAsync(x));
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        result.Should().NotContain("await await");
        result.Should().Contain("await items.SelectAsync");
    }

    // ── using directive ───────────────────────────────────────────────────────

    [Fact]
    public void Adds_using_directive_only_when_rewritten()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
class C {
    void M(IEnumerable<int> items) {
        var result = items.Select(x => x * 2);
    }
}";
        var result = Rewrite(source);

        result.Should().NotContain("using MyProject.Linq");
    }

    [Fact]
    public void Does_not_duplicate_existing_using()
    {
        var source = @"
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyProject.Linq;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(x => FooAsync(x));
    }
    Task<int> FooAsync(int x) => Task.FromResult(x);
}";
        var result = Rewrite(source);

        var count = result.Split("using MyProject.Linq").Length - 1;
        count.Should().Be(1);
    }
}
