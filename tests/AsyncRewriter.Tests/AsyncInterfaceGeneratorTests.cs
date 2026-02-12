using AsyncRewriter.Core.Models;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.Tests;

public class AsyncInterfaceGeneratorTests
{
    private static MethodNode MakeMethod(string id, string name, string returnType, List<string>? parameters = null)
        => new()
        {
            CallGraphId = "g",
            Id = id,
            Name = name,
            ContainingType = "TestClass",
            ContainingNamespace = "TestNamespace",
            ReturnType = returnType,
            Parameters = parameters ?? new List<string>(),
            FilePath = "Test.cs",
            StartLine = 1,
            EndLine = 10,
        };

    [Fact]
    public void GenerateAsyncInterface_ProducesCorrectCode()
    {
        var methods = new List<ProblematicMethod>
        {
            new("IRepo.Get",
                MakeMethod("IRepo.Get", "Get", "string"),
                MakeMethod("Repo.Get", "Get", "string"),
                MakeMethod("Repo.Get", "Get", "Task<string>")),
            new("IRepo.Save",
                MakeMethod("IRepo.Save", "Save", "void", new List<string> { "string item" }),
                MakeMethod("Repo.Save", "Save", "void", new List<string> { "string item" }),
                MakeMethod("Repo.Save", "Save", "Task", new List<string> { "string item" })),
        };

        var result = AsyncInterfaceGenerator.GenerateAsyncInterface("IRepoAsync", "MyApp.Interfaces", methods);

        result.Should().Contain("namespace MyApp.Interfaces;");
        result.Should().Contain("public interface IRepoAsync");
        result.Should().Contain("Task<string> Get();");
        result.Should().Contain("Task Save(string item);");
        result.Should().Contain("using System.Threading.Tasks;");
    }
}
