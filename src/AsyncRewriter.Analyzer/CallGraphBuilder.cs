using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AsyncRewriter.Core.Interfaces;
using AsyncRewriter.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Builds a method call graph from C# code using Roslyn
/// </summary>
public class CallGraphBuilder : ICallGraphBuilder
{
    private readonly IMethodExtractorFactory _methodExtractorFactory;
    private readonly IMethodCallExtractorFactory _methodCallExtractorFactory;
    private readonly ILogger<CallGraphBuilder> _logger;
    private readonly ConcurrentDictionary<string, MethodNode> _methods = new();
    private readonly ConcurrentBag<MethodCall> _calls = new();
    private readonly ConcurrentBag<InterfaceImplementation> _implementations = new();
    private readonly ConcurrentBag<MethodOverride> _overrides = new();


    public CallGraphBuilder(IMethodExtractorFactory methodExtractorFactory, IMethodCallExtractorFactory methodCallExtractorFactory, ILogger<CallGraphBuilder> logger)
    {
        _methodExtractorFactory = methodExtractorFactory;
        _methodCallExtractorFactory = methodCallExtractorFactory;
        _logger = logger;
    }
    
    public async Task<CallGraph> Build(
        string solutionPath,
        CancellationToken cancellationToken = default)
    {
        var callGraphId = Guid.NewGuid();
        _logger.LogInformation("Creating MSBuild workspace...");
        var workspace = MSBuildWorkspace.Create();

        _logger.LogInformation($"Loading solution {Path.GetFileName(solutionPath)}...");
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
        
       await Build(solution, (Methods: _methods, Implementations: _implementations, Overrides: _overrides), async (root, semanticModel, filePath, context, ct) =>
           await (await _methodExtractorFactory.Create()).Extract(callGraphId, root, semanticModel, filePath, context.Methods, context.Implementations, context.Overrides, ct), cancellationToken);
       
       // Build a resolver that can find SemanticModels across all projects in the solution
       var resolver = await SolutionSemanticModelResolver.CreateAsync(solution, cancellationToken);

       await Build(solution, (Methods: _methods, Calls: _calls), async (root, semanticModel, filePath, context, ct) =>
           await (await _methodCallExtractorFactory.Create()).Extract(callGraphId, root, semanticModel, filePath, context.Methods, context.Calls, resolver, ct), cancellationToken);
       
       return new CallGraph(_calls, _implementations, _overrides)
       {
           Methods = _methods,
       };
    }
    
    private async Task Build<T>(Solution solution, T context, Func<SyntaxNode, SemanticModel, string, T, CancellationToken , Task> builder, CancellationToken cancellationToken = default)
    {
        // Process all projects in the solution
        var projectList = solution.Projects.ToList();
        var projectIndex = 0;

        foreach (var project in projectList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectIndex++;

            _logger.LogInformation("Compiling {ProjectName} ({ProjectIndex}/{ProjectListCount})...", project.Name, projectIndex, projectList.Count);
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue; // Skip projects that fail to compile
            }

            var trees = compilation.SyntaxTrees.ToList();
            var total = trees.Count;
            var processed = 0;
            
            // Process syntax trees in parallel
            await Parallel.ForEachAsync(
                trees,
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                },
                async (syntaxTree, ct) =>
                {
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    var root = await syntaxTree.GetRootAsync(ct);

                    await builder(root, semanticModel, syntaxTree.FilePath, context, ct);

                    var current = Interlocked.Increment(ref processed);
                    _logger.LogTrace("Analyzing {ProjectName} ({ProjectIndex}/{Total} files)", project.Name, current, total);
                });
        }
    }
}
