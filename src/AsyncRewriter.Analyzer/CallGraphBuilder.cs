using System;
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

namespace AsyncRewriter.Analyzer;

/// <summary>
/// Builds a method call graph from C# code using Roslyn
/// </summary>
public class CallGraphBuilder : ICallGraphBuilder
{
    private readonly IMethodsResolver _methodsResolver;
    private readonly IMethodCallsResolver _methodCallsResolver;

    public CallGraphBuilder()
        : this(new MethodsResolver(), new MethodCallsResolver())
    {
    }

    public CallGraphBuilder(IMethodsResolver methodsResolver, IMethodCallsResolver methodCallsResolver)
    {
        _methodsResolver = methodsResolver;
        _methodCallsResolver = methodCallsResolver;
    }

    public async Task<CallGraph> AnalyzeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var sourceCode = await File.ReadAllTextAsync(filePath, cancellationToken);
        return await AnalyzeSourceAsync(sourceCode, filePath, cancellationToken);
    }

    public Task<CallGraph> AnalyzeProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        return AnalyzeProjectAsync(projectPath, null, null, cancellationToken);
    }

    public Task<CallGraph> AnalyzeProjectAsync(
        string projectPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeProjectAsync(projectPath, externalSyncWrapperMethods, null, cancellationToken);
    }

    public async Task<CallGraph> AnalyzeProjectAsync(
        string projectPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        // If a solution file is provided, delegate to AnalyzeSolutionAsync
        if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            return await AnalyzeSolutionAsync(projectPath, externalSyncWrapperMethods, progressCallback, cancellationToken);
        }

        progressCallback?.Invoke("Creating MSBuild workspace...", 0, 0);
        var workspace = MSBuildWorkspace.Create();

        progressCallback?.Invoke($"Loading project {Path.GetFileName(projectPath)}...", 0, 0);
        var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = project.Name
        };

        ApplyExternalSyncWrapperMethods(callGraph, externalSyncWrapperMethods);

        progressCallback?.Invoke($"Compiling {project.Name}...", 0, 0);
        var compilation = await project.GetCompilationAsync(cancellationToken);
        if (compilation == null)
        {
            throw new InvalidOperationException("Failed to get compilation");
        }

        var trees = compilation.SyntaxTrees.ToList();
        var total = trees.Count;
        var processed = 0;

        progressCallback?.Invoke($"Building call graph (0/{total} files)...", 0, total);

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

                await AnalyzeSyntaxTreeAsync(root, semanticModel, syntaxTree.FilePath, callGraph, ct);

                var current = Interlocked.Increment(ref processed);
                progressCallback?.Invoke(syntaxTree.FilePath, current, total);
            });

        return callGraph;
    }

    public Task<CallGraph> AnalyzeSolutionAsync(string solutionPath, CancellationToken cancellationToken = default)
    {
        return AnalyzeSolutionAsync(solutionPath, null, null, cancellationToken);
    }

    public Task<CallGraph> AnalyzeSolutionAsync(
        string solutionPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        CancellationToken cancellationToken = default)
    {
        return AnalyzeSolutionAsync(solutionPath, externalSyncWrapperMethods, null, cancellationToken);
    }

    public async Task<CallGraph> AnalyzeSolutionAsync(
        string solutionPath,
        IEnumerable<string>? externalSyncWrapperMethods,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        progressCallback?.Invoke("Creating MSBuild workspace...", 0, 0);
        var workspace = MSBuildWorkspace.Create();

        progressCallback?.Invoke($"Loading solution {Path.GetFileName(solutionPath)}...", 0, 0);
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = Path.GetFileNameWithoutExtension(solutionPath)
        };

        ApplyExternalSyncWrapperMethods(callGraph, externalSyncWrapperMethods);

        // Process all projects in the solution
        var projectList = solution.Projects.ToList();
        var projectIndex = 0;

        foreach (var project in projectList)
        {
            cancellationToken.ThrowIfCancellationRequested();
            projectIndex++;

            progressCallback?.Invoke($"Compiling {project.Name} ({projectIndex}/{projectList.Count})...", 0, 0);
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation == null)
            {
                continue; // Skip projects that fail to compile
            }

            var trees = compilation.SyntaxTrees.ToList();
            var total = trees.Count;
            var processed = 0;

            progressCallback?.Invoke($"Analyzing {project.Name} (0/{total} files)...", 0, total);

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

                    await AnalyzeSyntaxTreeAsync(root, semanticModel, syntaxTree.FilePath, callGraph, ct);

                    var current = Interlocked.Increment(ref processed);
                    progressCallback?.Invoke(syntaxTree.FilePath, current, total);
                });
        }

        return callGraph;
    }

    public async Task<CallGraph> AnalyzeSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: fileName, cancellationToken: cancellationToken);

        // Get references from runtime directory to resolve BCL types
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location), // System.Linq
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Collections.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll"))
        };

        var compilation = CSharpCompilation.Create("Analysis")
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTree);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        var callGraph = new CallGraph
        {
            ProjectName = "InlineAnalysis"
        };

        await AnalyzeSyntaxTreeAsync(root, semanticModel, fileName, callGraph, cancellationToken);

        return callGraph;
    }

    public async Task<CallGraph> AnalyzeSourceAsync(
        string sourceCode,
        IEnumerable<string>? externalSyncWrapperMethods,
        string fileName = "source.cs",
        CancellationToken cancellationToken = default)
    {
        var callGraph = await AnalyzeSourceAsync(sourceCode, fileName, cancellationToken);
        ApplyExternalSyncWrapperMethods(callGraph, externalSyncWrapperMethods);
        return callGraph;
    }

    private void ApplyExternalSyncWrapperMethods(CallGraph callGraph, IEnumerable<string>? externalSyncWrapperMethods)
    {
        if (externalSyncWrapperMethods == null)
        {
            return;
        }

        foreach (var method in externalSyncWrapperMethods)
        {
            if (string.IsNullOrWhiteSpace(method))
            {
                continue;
            }

            var methodId = method.Trim();
            callGraph.SyncWrapperMethods.Add(methodId);

            if (callGraph.Methods.TryGetValue(methodId, out var methodNode))
            {
                callGraph.Methods[methodId] = methodNode with { IsSyncWrapper = true };
            }
        }
    }

    private async Task AnalyzeSyntaxTreeAsync(SyntaxNode root, SemanticModel semanticModel, string filePath, CallGraph callGraph, CancellationToken cancellationToken = default)
    {
        // First pass: resolve all method declarations
        var methods = await _methodsResolver.ResolveMethodsAsync(root, semanticModel, filePath, cancellationToken);

        foreach (var (id, method) in methods)
        {
            callGraph.Methods.AddOrUpdate(id, method, (key, existing) => method);
        }

        // Second pass: resolve all method calls
        var calls = await _methodCallsResolver.ResolveCallsAsync(root, semanticModel, filePath, callGraph.Methods, cancellationToken);

        // Add discovered external methods
        if (_methodCallsResolver is MethodCallsResolver resolver)
        {
            foreach (var (id, method) in resolver.DiscoveredExternalMethods)
            {
                var methodToAdd = callGraph.SyncWrapperMethods.Contains(id)
                    ? method with { IsSyncWrapper = true }
                    : method;
                callGraph.Methods.TryAdd(id, methodToAdd);
            }
        }

        foreach (var call in calls)
        {
            callGraph.Calls[call.Id] = call;
        }
    }

    public Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        return FindSyncWrapperMethodsAsync(projectPath, null, cancellationToken);
    }

    public async Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsAsync(
        string projectPath,
        Action<string, int, int>? progressCallback,
        CancellationToken cancellationToken = default)
    {
        var results = new List<SyncWrapperMethod>();

        // Report workspace creation
        progressCallback?.Invoke("Creating MSBuild workspace...", 0, 0);
        var workspace = MSBuildWorkspace.Create();

        // Handle solution files
        if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            progressCallback?.Invoke($"Loading solution {Path.GetFileName(projectPath)}...", 0, 0);
            var solution = await workspace.OpenSolutionAsync(projectPath, cancellationToken: cancellationToken);

            // Collect all syntax trees first to get total count
            var allTrees = new List<(Compilation compilation, SyntaxTree tree)>();
            var projectList = solution.Projects.ToList();
            var projectIndex = 0;

            foreach (var project in projectList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                projectIndex++;
                progressCallback?.Invoke($"Compiling {project.Name} ({projectIndex}/{projectList.Count})...", 0, 0);

                var compilation = await project.GetCompilationAsync(cancellationToken);
                if (compilation == null) continue;

                foreach (var syntaxTree in compilation.SyntaxTrees)
                {
                    allTrees.Add((compilation, syntaxTree));
                }
            }

            var totalFiles = allTrees.Count;
            var filesProcessed = 0;

            foreach (var (compilation, syntaxTree) in allTrees)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progressCallback?.Invoke(syntaxTree.FilePath, filesProcessed, totalFiles);

                var semanticModel = compilation.GetSemanticModel(syntaxTree);
                var root = await syntaxTree.GetRootAsync(cancellationToken);

                var syncWrappers = FindSyncWrappersInSyntaxTree(root, semanticModel, syntaxTree.FilePath);
                results.AddRange(syncWrappers);

                filesProcessed++;
            }

            progressCallback?.Invoke(string.Empty, totalFiles, totalFiles);
            return results;
        }

        // Handle single project files
        progressCallback?.Invoke($"Loading project {Path.GetFileName(projectPath)}...", 0, 0);
        var proj = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);

        progressCallback?.Invoke($"Compiling {proj.Name}...", 0, 0);
        var comp = await proj.GetCompilationAsync(cancellationToken);
        if (comp == null)
        {
            throw new InvalidOperationException("Failed to get compilation");
        }

        var trees = comp.SyntaxTrees.ToList();
        var total = trees.Count;
        var processed = 0;

        foreach (var syntaxTree in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progressCallback?.Invoke(syntaxTree.FilePath, processed, total);

            var semanticModel = comp.GetSemanticModel(syntaxTree);
            var root = await syntaxTree.GetRootAsync(cancellationToken);

            var syncWrappers = FindSyncWrappersInSyntaxTree(root, semanticModel, syntaxTree.FilePath);
            results.AddRange(syncWrappers);

            processed++;
        }

        progressCallback?.Invoke(string.Empty, total, total);
        return results;
    }

    public async Task<List<SyncWrapperMethod>> FindSyncWrapperMethodsInSourceAsync(string sourceCode, string fileName = "source.cs", CancellationToken cancellationToken = default)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, path: fileName, cancellationToken: cancellationToken);

        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new[]
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Func<>).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
        };

        var compilation = CSharpCompilation.Create("TempAssembly")
            .AddReferences(references)
            .AddSyntaxTrees(syntaxTree);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        return FindSyncWrappersInSyntaxTree(root, semanticModel, fileName);
    }

    private List<SyncWrapperMethod> FindSyncWrappersInSyntaxTree(SyntaxNode root, SemanticModel semanticModel, string filePath)
    {
        var results = new List<SyncWrapperMethod>();

        var methodDeclarations = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .ToList();

        foreach (var methodDecl in methodDeclarations)
        {
            var methodSymbol = semanticModel.GetDeclaredSymbol(methodDecl);
            if (methodSymbol == null) continue;

            var syncWrapperInfo = AnalyzeForSyncWrapperPattern(methodSymbol);
            if (syncWrapperInfo != null)
            {
                var lineSpan = methodDecl.GetLocation().GetLineSpan();
                results.Add(new SyncWrapperMethod
                {
                    MethodId = MethodsResolver.GetMethodId(methodSymbol),
                    Name = methodSymbol.Name,
                    ContainingType = methodSymbol.ContainingType?.ToDisplayString() ?? "",
                    FilePath = filePath,
                    StartLine = lineSpan.StartLinePosition.Line + 1,
                    ReturnType = methodSymbol.ReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    Signature = MethodsResolver.GetMethodSignature(methodSymbol),
                    PatternDescription = syncWrapperInfo
                });
            }
        }

        return results;
    }

    /// <summary>
    /// Analyzes a method to determine if it follows a sync-over-async wrapper pattern.
    /// Returns a description of the pattern if found, null otherwise.
    /// </summary>
    private string? AnalyzeForSyncWrapperPattern(IMethodSymbol methodSymbol)
    {
        // Look for parameters that are Func<Task> or Func<Task<TResult>>
        foreach (var parameter in methodSymbol.Parameters)
        {
            var paramType = parameter.Type;

            // Check if parameter is a Func type
            if (paramType is not INamedTypeSymbol namedType)
                continue;

            if (!namedType.Name.StartsWith("Func") || !namedType.IsGenericType)
                continue;

            var typeArgs = namedType.TypeArguments;
            if (typeArgs.Length == 0)
                continue;

            // Get the last type argument (return type of the Func)
            var funcReturnType = typeArgs[typeArgs.Length - 1];

            // Check if the Func returns Task or Task<T>
            if (!IsTaskType(funcReturnType, out var taskResultType))
                continue;

            // Now check if the method's return type matches the pattern
            var methodReturnType = methodSymbol.ReturnType;

            // Pattern 1: Func<Task> parameter with void return
            if (taskResultType == null && methodReturnType.SpecialType == SpecialType.System_Void)
            {
                return $"Method has Func<Task> parameter '{parameter.Name}' and returns void - sync wrapper pattern";
            }

            // Pattern 2: Func<Task<TResult>> parameter with TResult return
            if (taskResultType != null)
            {
                // Check if the return type matches the Task's result type
                if (SymbolEqualityComparer.Default.Equals(methodReturnType, taskResultType))
                {
                    return $"Method has Func<Task<{taskResultType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}>> parameter '{parameter.Name}' and returns {methodReturnType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)} - sync wrapper pattern";
                }

                // Also check for type parameter match (generic methods like Execute<TResult>(Func<Task<TResult>>))
                if (methodReturnType is ITypeParameterSymbol returnTypeParam &&
                    taskResultType is ITypeParameterSymbol taskTypeParam &&
                    returnTypeParam.Name == taskTypeParam.Name)
                {
                    return $"Method has Func<Task<{taskResultType.Name}>> parameter '{parameter.Name}' and returns {methodReturnType.Name} - generic sync wrapper pattern";
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Checks if a type is Task or Task&lt;T&gt; and extracts the result type if applicable
    /// </summary>
    private bool IsTaskType(ITypeSymbol type, out ITypeSymbol? resultType)
    {
        resultType = null;

        if (type is not INamedTypeSymbol namedType)
            return false;

        var fullName = namedType.ToDisplayString();

        // Check for Task<T>
        if (fullName.StartsWith("System.Threading.Tasks.Task<") ||
            (namedType.Name == "Task" && namedType.TypeArguments.Length == 1))
        {
            if (namedType.TypeArguments.Length == 1)
            {
                resultType = namedType.TypeArguments[0];
                return true;
            }
        }

        // Check for Task (non-generic)
        if (fullName == "System.Threading.Tasks.Task" ||
            (namedType.Name == "Task" && namedType.TypeArguments.Length == 0))
        {
            return true;
        }

        return false;
    }
}
