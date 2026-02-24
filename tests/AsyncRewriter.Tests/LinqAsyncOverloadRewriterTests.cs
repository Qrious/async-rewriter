using AsyncRewriter.Transformation;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;

namespace AsyncRewriter.Tests;

public class LinqAsyncOverloadRewriterTests
{
    private static string Rewrite(string source, string ns = "MyProject.Linq")
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        var rewriter = new LinqAsyncOverloadRewriter(ns);
        var result = rewriter.Visit(root);
        return result!.ToFullString();
    }

    [Fact]
    public void Rewrites_Select_with_async_lambda()
    {
        var source = @"
using System.Linq;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(async x => await FooAsync(x));
    }
}";
        var result = Rewrite(source);

        result.Should().Contain("items.SelectAsync(async x => await FooAsync(x))");
        result.Should().Contain("await items.SelectAsync");
        result.Should().Contain("using MyProject.Linq");
    }

    [Fact]
    public void Rewrites_Where_with_async_lambda()
    {
        var source = @"
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Where(async x => await IsActiveAsync(x));
    }
}";
        var result = Rewrite(source);

        result.Should().Contain("await items.WhereAsync(async x => await IsActiveAsync(x))");
    }

    [Fact]
    public void Does_not_rewrite_sync_lambda()
    {
        var source = @"
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

    [Fact]
    public void Does_not_rewrite_already_async_method()
    {
        var source = @"
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.SelectAsync(async x => await FooAsync(x));
    }
}";
        var result = Rewrite(source);

        // Should not double-rename to SelectAsyncAsync.
        result.Should().NotContain("SelectAsyncAsync");
    }

    [Fact]
    public void Parenthesizes_await_when_chain_continues()
    {
        var source = @"
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(async x => await FooAsync(x)).ToList();
    }
}";
        var result = Rewrite(source);

        result.Should().Contain("(await items.SelectAsync(async x => await FooAsync(x))).ToList()");
    }

    [Fact]
    public void Handles_multi_async_chain()
    {
        var source = @"
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Where(async x => await IsActiveAsync(x)).Select(async x => await MapAsync(x)).ToList();
    }
}";
        var result = Rewrite(source);

        // Inner Where becomes (await ...WhereAsync(...))
        // Outer Select uses that as receiver and becomes (await (...).SelectAsync(...))
        result.Should().Contain("WhereAsync");
        result.Should().Contain("SelectAsync");
        result.Should().Contain(".ToList()");
        // The chain should end with .ToList() on the outer await
        result.Should().Contain(").ToList()");
    }

    [Fact]
    public void Does_not_double_await()
    {
        var source = @"
class C {
    async Task M(IEnumerable<int> items) {
        var result = await items.Select(async x => await FooAsync(x));
    }
}";
        var result = Rewrite(source);

        // Should not produce "await await".
        result.Should().NotContain("await await");
        result.Should().Contain("await items.SelectAsync");
    }

    [Fact]
    public void Adds_using_directive_only_when_rewritten()
    {
        var source = @"
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
using MyProject.Linq;
class C {
    async Task M(IEnumerable<int> items) {
        var result = items.Select(async x => await FooAsync(x));
    }
}";
        var result = Rewrite(source);

        // Count occurrences of the using directive — should be exactly 1.
        var count = result.Split("using MyProject.Linq").Length - 1;
        count.Should().Be(1);
    }
}
