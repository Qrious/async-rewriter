using AsyncRewriter.Analyzer;
using AsyncRewriter.Transformation;
using FluentAssertions;
using Xunit;

namespace AsyncRewriter.IntegrationTests;

public class InheritanceScenarioTests
{
    private readonly CallGraphAnalyzer _callGraphAnalyzer;
    private readonly AsyncFloodingAnalyzer _floodingAnalyzer;
    private readonly AsyncTransformer _transformer;

    public InheritanceScenarioTests()
    {
        _callGraphAnalyzer = new CallGraphAnalyzer();
        _floodingAnalyzer = new AsyncFloodingAnalyzer();
        _transformer = new AsyncTransformer(_floodingAnalyzer);
    }

    [Fact]
    public async Task Inheritance_BaseClassMethod_FloodsToOverriddenMethods()
    {
        // Arrange
        var source = @"
using System;

namespace InheritanceScenario
{
    public abstract class BaseRepository
    {
        public virtual string GetById(int id)
        {
            return LoadFromStorage(id);
        }

        protected string LoadFromStorage(int id)
        {
            Console.WriteLine($""Loading item {id}..."");
            return $""item-{id}"";
        }
    }

    public class UserRepository : BaseRepository
    {
        public override string GetById(int id)
        {
            Console.WriteLine(""Getting user..."");
            return base.GetById(id);
        }
    }

    public class ProductRepository : BaseRepository
    {
        public override string GetById(int id)
        {
            Console.WriteLine(""Getting product..."");
            return base.GetById(id);
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var loadMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "LoadFromStorage");
        var rootMethods = new HashSet<string> { loadMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert - Base method should be transformed
        var baseGetById = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetById" && m.ContainingType.Contains("BaseRepository"));
        baseGetById.Should().NotBeNull();
        baseGetById!.RequiresAsyncTransformation.Should().BeTrue();

        // Both overridden methods should be transformed
        var userGetById = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetById" && m.ContainingType.Contains("UserRepository"));
        userGetById.Should().NotBeNull();
        userGetById!.RequiresAsyncTransformation.Should().BeTrue();

        var productGetById = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetById" && m.ContainingType.Contains("ProductRepository"));
        productGetById.Should().NotBeNull();
        productGetById!.RequiresAsyncTransformation.Should().BeTrue();

        // Verify transformations
        // LoadFromStorage has no async calls, so it uses Task.FromResult instead of async
        transformedSource.Should().Contain("Task<string> LoadFromStorage(int id)");
        transformedSource.Should().Contain("Task.FromResult");
        transformedSource.Should().NotContain("async Task<string> LoadFromStorage");
    }

    [Fact]
    public async Task Inheritance_DerivedCallsBaseMethod_FloodsProperly()
    {
        // Arrange
        var source = @"
using System;

namespace InheritanceScenario
{
    public abstract class BaseRepository
    {
        protected string LoadFromStorage(int id)
        {
            Console.WriteLine($""Loading..."");
            return $""item-{id}"";
        }
    }

    public class UserRepository : BaseRepository
    {
        public string GetUserByEmail(string email)
        {
            return GetById(1);
        }

        private string GetById(int id)
        {
            return LoadFromStorage(id);
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var loadMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "LoadFromStorage");
        var rootMethods = new HashSet<string> { loadMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert
        var getUserByEmailMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "GetUserByEmail");
        getUserByEmailMethod.Should().NotBeNull();
        getUserByEmailMethod!.RequiresAsyncTransformation.Should().BeTrue();
        getUserByEmailMethod.AsyncReturnType.Should().Be("Task<string>");

        var getByIdMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "GetById");
        getByIdMethod.Should().NotBeNull();
        getByIdMethod!.RequiresAsyncTransformation.Should().BeTrue();
    }

    [Fact]
    public async Task Inheritance_MultipleInheritorsWithDifferentOverrides_TransformsCorrectly()
    {
        // Arrange
        var source = @"
using System;

namespace InheritanceScenario
{
    public abstract class BaseRepository
    {
        public virtual string GetById(int id)
        {
            return LoadFromStorage(id);
        }

        public virtual void Save(string data)
        {
            WriteToStorage(data);
        }

        protected string LoadFromStorage(int id)
        {
            return $""item-{id}"";
        }

        protected void WriteToStorage(string data)
        {
            Console.WriteLine($""Writing {data}..."");
        }
    }

    public class UserRepository : BaseRepository
    {
        public override string GetById(int id)
        {
            return base.GetById(id);
        }
    }

    public class ProductRepository : BaseRepository
    {
        public override void Save(string data)
        {
            base.Save(data);
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        // Root on LoadFromStorage
        var loadMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "LoadFromStorage");
        var rootMethods = new HashSet<string> { loadMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);
        var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph);
        var transformedSource = await _transformer.TransformSourceAsync(source, transformations.ToList());

        // Assert - UserRepository.GetById should be transformed
        var userGetById = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "GetById" && m.ContainingType.Contains("UserRepository"));
        userGetById.Should().NotBeNull();
        userGetById!.RequiresAsyncTransformation.Should().BeTrue();

        // ProductRepository.Save should NOT be transformed (different path)
        var productSave = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Save" && m.ContainingType.Contains("ProductRepository"));
        productSave.Should().NotBeNull();
        productSave!.RequiresAsyncTransformation.Should().BeFalse();

        // Verify UserRepository transformation
        transformedSource.Should().Contain("async Task<string> GetById(int id)");
    }

    [Fact]
    public async Task Inheritance_ChainedBaseCalls_FloodsEntireChain()
    {
        // Arrange
        var source = @"
using System;

namespace InheritanceScenario
{
    public class Level1
    {
        public virtual string Method1()
        {
            return DoWork();
        }

        protected string DoWork()
        {
            return ""work"";
        }
    }

    public class Level2 : Level1
    {
        public override string Method1()
        {
            return base.Method1();
        }
    }

    public class Level3 : Level2
    {
        public override string Method1()
        {
            return base.Method1();
        }
    }
}";

        // Act
        var callGraph = await _callGraphAnalyzer.AnalyzeSourceAsync(source);

        var doWorkMethod = callGraph.Methods.Values.FirstOrDefault(m => m.Name == "DoWork");
        var rootMethods = new HashSet<string> { doWorkMethod!.Id };

        await _floodingAnalyzer.AnalyzeFloodingAsync(callGraph, rootMethods);

        // Assert - All three Method1 implementations should be flooded
        var level1Method = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Method1" && m.ContainingType.Contains("Level1"));
        level1Method.Should().NotBeNull();
        level1Method!.RequiresAsyncTransformation.Should().BeTrue();

        var level2Method = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Method1" && m.ContainingType.Contains("Level2"));
        level2Method.Should().NotBeNull();
        level2Method!.RequiresAsyncTransformation.Should().BeTrue();

        var level3Method = callGraph.Methods.Values
            .FirstOrDefault(m => m.Name == "Method1" && m.ContainingType.Contains("Level3"));
        level3Method.Should().NotBeNull();
        level3Method!.RequiresAsyncTransformation.Should().BeTrue();
    }
}
