using System.CommandLine;
using AsyncRewriter.Analyzer;
using AsyncRewriter.Analyzer.EntityFramework;
using AsyncRewriter.Analyzer.ServiceInterface;
using AsyncRewriter.Console.Commands;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Neo4j;
using AsyncRewriter.Transformation;
using Microsoft.Build.Locator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Console;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var serviceCollection = new ServiceCollection();
        ConfigureServices(serviceCollection);

        await using var services = serviceCollection.BuildServiceProvider(true);
        var logger = services.GetRequiredService<ILogger<Program>>();

        // Register MSBuild
        try
        {
            MSBuildLocator.RegisterDefaults();
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Warning: Could not register MSBuild: {Message}", ex.Message);
        }

        var rootCommand = new RootCommand("Async Rewriter - Analyze and transform C# codebases from sync to async");
        foreach (var command in services.GetServices<Command>())
        {
            rootCommand.AddCommand(command);
        }
        return await rootCommand.InvokeAsync(args);
    }

    private static void ConfigureServices(ServiceCollection serviceCollection)
    {
        serviceCollection.AddTransient<ICallGraphBuilder, CallGraphBuilder>();
        serviceCollection.AddTransient<IMethodCallExtractor, MethodCallExtractor>();
        serviceCollection.AddTransient<IMethodExtractor, MethodExtractor>();
        serviceCollection.AddSingleton<IMethodExtractorFactory, MethodExtractorFactory>();
        serviceCollection.AddSingleton<IMethodCallExtractorFactory, MethodCallExtractorFactory>();

        serviceCollection.AddTransient<IDirtyTaskMethodsExtractor, DirtyTaskMethodsExtractor>();
        serviceCollection.AddTransient<IAsyncCallGraphFlooder, AsyncCallGraphFlooder>();
        serviceCollection.AddTransient<IEntityFrameworkSyncCallExtractor, EntityFrameworkSyncCallExtractor>();
        serviceCollection.AddTransient<IAsyncInterfaceMethodExtractor, AsyncInterfaceMethodExtractor>();
        serviceCollection.AddSingleton<ICallGraphRepository, Neo4jCallGraphRepository>();
        serviceCollection.AddSingleton<IOutParameterAnalyzer, OutParameterAnalyzer>();
        serviceCollection.AddSingleton<FloodedCallGraphTransformer>();
        serviceCollection.AddSingleton<Command, BuildCallGraphCommand>();
        serviceCollection.AddSingleton<Command, FloodCallGraphCommand>();
        serviceCollection.AddSingleton<Command, ModifyCommand>();
        serviceCollection.AddSingleton<Command, RewriteLinqAsyncCommand>();
        serviceCollection.AddSingleton<Command, FixMissingAwaitsCommand>();

        serviceCollection.AddLogging(c => c.AddSimpleConsole());
    }
}
