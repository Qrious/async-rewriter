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

namespace AsyncRewriter.Transformation;

/// <summary>
/// Transforms synchronous code to async code using Roslyn
/// </summary>
public class AsyncTransformer : IAsyncTransformer
{
    private readonly IAsyncFloodingAnalyzer _floodingAnalyzer;

    public AsyncTransformer(IAsyncFloodingAnalyzer floodingAnalyzer)
    {
        _floodingAnalyzer = floodingAnalyzer;
    }

    public Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        CancellationToken cancellationToken = default)
    {
        return TransformProjectAsync(projectPath, callGraph, (_, _, _) => { }, cancellationToken);
    }

    public async Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        Action<string, int, int> progressCallback,
        CancellationToken cancellationToken = default)
    {
        var result = new TransformationResult
        {
            CallGraph = callGraph
        };

        try
        {
            var workspace = MSBuildWorkspace.Create();
            Solution solution;

            if (projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
            {
                solution = await workspace.OpenSolutionAsync(projectPath, cancellationToken: cancellationToken);

                if (!solution.Projects.Any())
                {
                    result.Success = false;
                    result.Errors.Add("Solution contains no projects");
                    return result;
                }
            }
            else
            {
                var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
                solution = project.Solution;
            }

            // Get transformation info
            var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph, cancellationToken);

            // Build complete set of all async method IDs (methods that are or will be async)
            var allAsyncMethodIds = new HashSet<string>();
            foreach (var transformation in transformations)
            {
                allAsyncMethodIds.Add(transformation.MethodId);
                foreach (var interfaceMethodId in transformation.ImplementsInterfaceMethods)
                {
                    allAsyncMethodIds.Add(interfaceMethodId);
                }
            }
            // Also include methods that are already async in the call graph
            foreach (var method in callGraph.Methods.Values)
            {
                if (method.IsAsync)
                {
                    allAsyncMethodIds.Add(method.Id);
                }
            }

            // Collect all files that need processing:
            // 1. Files with methods requiring async transformation
            // 2. Files with calls requiring await (even if containing method is already async)
            var filesToProcess = new HashSet<string>();

            foreach (var method in callGraph.Methods.Values)
            {
                if (method.RequiresAsyncTransformation &&
                    !string.IsNullOrEmpty(method.FilePath) &&
                    method.FilePath != "external")
                {
                    filesToProcess.Add(method.FilePath);
                }
            }

            foreach (var call in callGraph.Calls)
            {
                if (call.RequiresAwait &&
                    !string.IsNullOrEmpty(call.FilePath) &&
                    call.FilePath != "external")
                {
                    filesToProcess.Add(call.FilePath);
                }
            }

            var orderedFilesToProcess = filesToProcess.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();

            // Build a lookup of documents by file path
            var documentsByPath = solution.Projects
                .SelectMany(project => project.Documents)
                .Where(d => d.FilePath != null)
                .GroupBy(d => d.FilePath!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var totalFiles = orderedFilesToProcess.Count;
            var transformedFiles = 0;

            foreach (var filePath in orderedFilesToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var fileTransformations = transformations
                    .Where(t => callGraph.Methods.TryGetValue(t.MethodId, out var m) && m.FilePath == filePath)
                    .ToList();

                // Get methods to transform for this file
                var methodsToTransform = fileTransformations.Select(t => t.MethodId).ToHashSet();

                FileTransformation fileTransformation;

                // Try to use the project's document for proper semantic analysis
                if (documentsByPath.TryGetValue(filePath, out var document))
                {
                    var semanticModel = await document.GetSemanticModelAsync(cancellationToken);
                    var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
                    var originalContent = await File.ReadAllTextAsync(filePath, cancellationToken);

                    if (semanticModel != null && syntaxTree != null)
                    {
                        var root = await syntaxTree.GetRootAsync(cancellationToken);

                        // Create rewriter with the real semantic model
                        var rewriter = new AsyncMethodRewriter(
                            semanticModel,
                            methodsToTransform,
                            allAsyncMethodIds,
                            callGraph.SyncWrapperMethods,
                            callGraph.BaseTypeTransformations);

                        var newRoot = rewriter.Visit(root);

                        // Add using directive for System.Threading.Tasks if not present
                        var compilationUnit = newRoot as CompilationUnitSyntax;
                        if (compilationUnit != null)
                        {
                            var hasTaskUsing = compilationUnit.Usings
                                .Any(u => u.Name?.ToString() == "System.Threading.Tasks");

                            if (!hasTaskUsing)
                            {
                                var taskUsing = SyntaxFactory.UsingDirective(
                                    SyntaxFactory.ParseName("System.Threading.Tasks")
                                        .WithLeadingTrivia(SyntaxFactory.Space))
                                    .WithTrailingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine));

                                compilationUnit = compilationUnit.AddUsings(taskUsing);
                                newRoot = compilationUnit;
                            }
                        }

                        var transformedContent = newRoot?.ToFullString() ?? originalContent;

                        fileTransformation = new FileTransformation
                        {
                            FilePath = filePath,
                            OriginalContent = originalContent,
                            TransformedContent = transformedContent
                        };

                        foreach (var transformation in fileTransformations)
                        {
                            fileTransformation.MethodTransformations.Add(new MethodTransformation
                            {
                                MethodName = transformation.MethodId,
                                OriginalReturnType = transformation.OriginalReturnType,
                                NewReturnType = transformation.NewReturnType,
                                AwaitAddedAtLines = rewriter.AwaitAddedLines.ToList()
                            });
                        }
                    }
                    else
                    {
                        // Fallback to source-based transformation
                        fileTransformation = await TransformFileAsync(filePath, fileTransformations, callGraph.SyncWrapperMethods, allAsyncMethodIds, cancellationToken);
                    }
                }
                else
                {
                    // File not in project, use source-based transformation
                    fileTransformation = await TransformFileAsync(filePath, fileTransformations, callGraph.SyncWrapperMethods, allAsyncMethodIds, cancellationToken);
                }

                result.ModifiedFiles.Add(fileTransformation);

                result.TotalMethodsTransformed += fileTransformation.MethodTransformations.Count;
                result.TotalCallSitesTransformed += fileTransformation.MethodTransformations
                    .Sum(m => m.AwaitAddedAtLines.Count);

                transformedFiles++;
                progressCallback(filePath, transformedFiles, totalFiles);
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Errors.Add($"Transformation failed: {ex.Message}");
        }

        return result;
    }

    public async Task<FileTransformation> TransformFileAsync(
        string filePath,
        List<AsyncTransformationInfo> transformations,
        HashSet<string>? syncWrapperMethodIds = null,
        HashSet<string>? allAsyncMethodIds = null,
        CancellationToken cancellationToken = default)
    {
        var originalContent = await File.ReadAllTextAsync(filePath, cancellationToken);
        var transformedContent = await TransformSourceAsync(originalContent, transformations, syncWrapperMethodIds, allAsyncMethodIds, cancellationToken);

        var fileTransformation = new FileTransformation
        {
            FilePath = filePath,
            OriginalContent = originalContent,
            TransformedContent = transformedContent
        };

        // Parse to get method transformations details
        var syntaxTree = CSharpSyntaxTree.ParseText(transformedContent, cancellationToken: cancellationToken);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        foreach (var transformation in transformations)
        {
            fileTransformation.MethodTransformations.Add(new MethodTransformation
            {
                MethodName = transformation.MethodId,
                OriginalReturnType = transformation.OriginalReturnType,
                NewReturnType = transformation.NewReturnType
            });
        }

        return fileTransformation;
    }

    public Task<string> TransformSourceAsync(
        string sourceCode,
        List<AsyncTransformationInfo> transformations,
        HashSet<string>? syncWrapperMethodIds = null,
        HashSet<string>? allAsyncMethodIds = null,
        CancellationToken cancellationToken = default)
    {
        return TransformSourceAsync(sourceCode, transformations, syncWrapperMethodIds, allAsyncMethodIds, null, cancellationToken);
    }

    public async Task<string> TransformSourceAsync(
        string sourceCode,
        List<AsyncTransformationInfo> transformations,
        HashSet<string>? syncWrapperMethodIds,
        HashSet<string>? allAsyncMethodIds,
        Dictionary<string, List<BaseTypeTransformation>>? baseTypeTransformations,
        CancellationToken cancellationToken)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode, cancellationToken: cancellationToken);

        // Create a compilation for semantic analysis
        var compilation = CSharpCompilation.Create("TempAssembly")
            .AddReferences(
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location))
            .AddSyntaxTrees(syntaxTree);

        var semanticModel = compilation.GetSemanticModel(syntaxTree);
        var root = await syntaxTree.GetRootAsync(cancellationToken);

        // Get all method IDs that need transformation in this file
        var methodsToTransform = transformations.Select(t => t.MethodId).ToHashSet();

        // Use provided allAsyncMethodIds if available, otherwise compute from transformations
        HashSet<string> asyncMethodIds;
        if (allAsyncMethodIds != null)
        {
            asyncMethodIds = allAsyncMethodIds;
        }
        else
        {
            // Fallback: compute from per-file transformations (for backwards compatibility)
            asyncMethodIds = new HashSet<string>(methodsToTransform);
            foreach (var transformation in transformations)
            {
                foreach (var interfaceMethodId in transformation.ImplementsInterfaceMethods)
                {
                    asyncMethodIds.Add(interfaceMethodId);
                }
            }
        }

        // Create rewriter with sync wrapper method IDs for unwrapping
        var rewriter = new AsyncMethodRewriter(semanticModel, methodsToTransform, asyncMethodIds, syncWrapperMethodIds, baseTypeTransformations);

        // Apply transformation
        var newRoot = rewriter.Visit(root);

        // Add using directive for System.Threading.Tasks if not present
        var compilationUnit = newRoot as CompilationUnitSyntax;
        if (compilationUnit != null)
        {
            var hasTaskUsing = compilationUnit.Usings
                .Any(u => u.Name?.ToString() == "System.Threading.Tasks");

            if (!hasTaskUsing)
            {
                var taskUsing = SyntaxFactory.UsingDirective(
                    SyntaxFactory.ParseName("System.Threading.Tasks")
                        .WithLeadingTrivia(SyntaxFactory.Space))
                    .WithTrailingTrivia(SyntaxFactory.EndOfLine(Environment.NewLine));

                compilationUnit = compilationUnit.AddUsings(taskUsing);
                newRoot = compilationUnit;
            }
        }

        return newRoot?.ToFullString() ?? sourceCode;
    }
}
