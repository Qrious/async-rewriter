using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class InterfaceReplacerTests
{
    [Fact]
    public void Transform_ReplacesSimpleInterfaceInBaseList()
    {
        var source = @"
public class MyRepo : IRepository
{
    public string Get() => """";
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IRepository", AsyncInterfaceName = "IRepositoryAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("IRepositoryAsync");
        result.Should().NotContain(": IRepository\n");
    }

    [Fact]
    public void Transform_ReplacesGenericInterface()
    {
        var source = @"
public class Mapper : IMapInto<Dest, Source>
{
    public Dest Map(Source s) => default;
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IMapInto", AsyncInterfaceName = "IMapIntoAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("IMapIntoAsync<Dest, Source>");
    }

    [Fact]
    public void Transform_ReplacesFieldAndParameterTypes()
    {
        var source = @"
public class Service
{
    private readonly IRepository _repo;

    public Service(IRepository repo)
    {
        _repo = repo;
    }
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IRepository", AsyncInterfaceName = "IRepositoryAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("private readonly IRepositoryAsync _repo;");
        result.Should().Contain("public Service(IRepositoryAsync repo)");
    }

    [Fact]
    public void Transform_AddsRequiredUsingDirective()
    {
        var source = @"using System;

public class MyRepo : IRepository
{
}";
        var mappings = new List<InterfaceMapping>
        {
            new()
            {
                SyncInterfaceName = "IRepository",
                AsyncInterfaceName = "IRepositoryAsync",
                RequiredNamespaces = new List<string> { "MyApp.Async.Interfaces" }
            }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("using MyApp.Async.Interfaces;");
        result.Should().Contain("IRepositoryAsync");
    }

    [Fact]
    public void Transform_DoesNotDuplicateExistingUsing()
    {
        var source = @"using System;
using MyApp.Async.Interfaces;

public class MyRepo : IRepository
{
}";
        var mappings = new List<InterfaceMapping>
        {
            new()
            {
                SyncInterfaceName = "IRepository",
                AsyncInterfaceName = "IRepositoryAsync",
                RequiredNamespaces = new List<string> { "MyApp.Async.Interfaces" }
            }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        // Count occurrences — should appear exactly once
        var count = result!.Split("using MyApp.Async.Interfaces;").Length - 1;
        count.Should().Be(1);
    }

    [Fact]
    public void Transform_ReturnsNull_WhenNoMatchesFound()
    {
        var source = @"
public class Service
{
    public void DoWork() { }
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IRepository", AsyncInterfaceName = "IRepositoryAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().BeNull();
    }

    [Fact]
    public void Transform_ReplacesQualifiedName_BySimpleName()
    {
        var source = @"
public class MyRepo : IRepository
{
    public IRepository Clone() => this;
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "MyApp.Data.IRepository", AsyncInterfaceName = "MyApp.Data.IRepositoryAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("IRepositoryAsync");
    }

    [Fact]
    public void Transform_ReplacesMultipleInterfaces()
    {
        var source = @"
public class Service : IRepository, ILogger
{
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IRepository", AsyncInterfaceName = "IRepositoryAsync" },
            new() { SyncInterfaceName = "ILogger", AsyncInterfaceName = "IAsyncLogger" },
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("IRepositoryAsync");
        result.Should().Contain("IAsyncLogger");
    }

    [Fact]
    public void Transform_PreservesOtherCodeUnchanged()
    {
        var source = @"using System;

namespace MyApp;

public class MyRepo : IRepository
{
    public string Get(int id) => id.ToString();
}";
        var mappings = new List<InterfaceMapping>
        {
            new() { SyncInterfaceName = "IRepository", AsyncInterfaceName = "IRepositoryAsync" }
        };

        var result = InterfaceReplacer.Transform(source, mappings);

        result.Should().NotBeNull();
        result.Should().Contain("namespace MyApp;");
        result.Should().Contain("public string Get(int id) => id.ToString();");
        result.Should().Contain("using System;");
    }
}
