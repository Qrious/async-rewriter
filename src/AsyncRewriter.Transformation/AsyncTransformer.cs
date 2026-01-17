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

    public async Task<TransformationResult> TransformProjectAsync(
        string projectPath,
        CallGraph callGraph,
        CancellationToken cancellationToken = default)
    {
        var result = new TransformationResult
        {
            CallGraph = callGraph
        };

        try
        {
            var workspace = MSBuildWorkspace.Create();
            var project = await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
            var compilation = await project.GetCompilationAsync(cancellationToken);

            if (compilation == null)
            {
                result.Success = false;
                result.Errors.Add("Failed to get compilation");
                return result;
            }

            // Get transformation info
            var transformations = await _floodingAnalyzer.GetTransformationInfoAsync(callGraph, cancellationToken);

            // Group by file
            var methodsByFile = callGraph.Methods.Values
                .Where(m => m.RequiresAsyncTransformation)
                .GroupBy(m => m.FilePath);

            foreach (var fileGroup in methodsByFile)
            {
                var filePath = fileGroup.Key;
                if (string.IsNullOrEmpty(filePath) || filePath == "external")
                    continue;

                var fileTransformations = transformations
                    .Where(t => callGraph.Methods.TryGetValue(t.MethodId, out var m) && m.FilePath == filePath)
                    .ToList();

                var fileTransformation = await TransformFileAsync(filePath, fileTransformations, cancellationToken);
                result.ModifiedFiles.Add(fileTransformation);

                result.TotalMethodsTransformed += fileTransformation.MethodTransformations.Count;
                result.TotalCallSitesTransformed += fileTransformation.MethodTransformations
                    .Sum(m => m.AwaitAddedAtLines.Count);
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
        CancellationToken cancellationToken = default)
    {
        var originalContent = await File.ReadAllTextAsync(filePath, cancellationToken);
        var transformedContent = await TransformSourceAsync(originalContent, transformations, cancellationToken);

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

    public async Task<string> TransformSourceAsync(
        string sourceCode,
        List<AsyncTransformationInfo> transformations,
        CancellationToken cancellationToken = default)
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

        // Get all method IDs that need transformation
        var methodsToTransform = transformations.Select(t => t.MethodId).ToHashSet();

        // Get all method IDs that are or will be async (including interface methods they implement)
        var asyncMethodIds = new HashSet<string>(methodsToTransform);
        foreach (var transformation in transformations)
        {
            foreach (var interfaceMethodId in transformation.ImplementsInterfaceMethods)
            {
                asyncMethodIds.Add(interfaceMethodId);
            }
        }

        // Create rewriter
        var rewriter = new AsyncMethodRewriter(semanticModel, methodsToTransform, asyncMethodIds);

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
                        .WithLeadingTrivia(SyntaxFactory.Space));

                compilationUnit = compilationUnit.AddUsings(taskUsing);
                newRoot = compilationUnit;
            }
        }

        return newRoot?.ToFullString() ?? sourceCode;
    }
}
