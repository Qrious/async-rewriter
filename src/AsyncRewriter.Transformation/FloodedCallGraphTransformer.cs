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
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace AsyncRewriter.Transformation;

/// <summary>
/// Transforms C# source files based on a flooded call graph carrying composite metadata.
/// Uses a Roslyn <see cref="SemanticModel"/> (provided via <see cref="IDocumentSemanticModelProvider"/>)
/// to identify methods by symbol rather than by source line, delegating the rewriting to
/// <see cref="SemanticCallGraphRewriter"/>.
/// <para>
/// The composite metadata type is
/// <c>CompositeMetadata&lt;FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata&gt;</c>:
/// <list type="bullet">
///   <item><c>First</c> — flooding info (original return type, depth, reason)</item>
///   <item><c>Second</c> — sync wrapper flag</item>
///   <item><c>Third</c> — Entity Framework caller flag</item>
/// </list>
/// </para>
/// </summary>
public class FloodedCallGraphTransformer
{
    public async Task<IReadOnlyList<FileTransformation>> TransformAsync(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph,
        IDocumentSemanticModelProvider documentProvider,
        CancellationToken cancellationToken = default)
    {
        var floodedMethodIds = new HashSet<string>(callGraph.MethodMetadata.Keys);

        // Build: callerMethodId → set of callee method IDs that need await
        var awaitableCalleesByCallerId = BuildAwaitableCalleeMap(callGraph, floodedMethodIds);

        // Build: methodId → transform info (for all flooded methods)
        var methodsById = BuildMethodTransformInfos(callGraph);

        // Build: set of sync-wrapper method IDs
        var syncWrapperMethodIds = new HashSet<string>(
            callGraph.MethodMetadata
                .Where(kvp => kvp.Value.Second.IsSyncWrapper)
                .Select(kvp => kvp.Key));

        // Group flooded methods by file
        var byFile = GroupFloodedMethodsByFile(callGraph, floodedMethodIds);

        var results = new List<FileTransformation>();
        foreach (var (filePath, _) in byFile)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var document = await documentProvider.GetForFileAsync(filePath, cancellationToken);
            if (document == null)
            {
                // Fall back to disk-based parsing when the file is not in the solution
                // (e.g. generated files or out-of-solution paths).
                await TransformFromDisk(
                    filePath, methodsById, awaitableCalleesByCallerId,
                    syncWrapperMethodIds, results, cancellationToken);
                continue;
            }

            var (root, semanticModel) = document.Value;
            var transformed = TransformWithSemanticModel(
                root, semanticModel, methodsById, awaitableCalleesByCallerId, syncWrapperMethodIds);

            if (transformed != null)
            {
                results.Add(new FileTransformation
                {
                    FilePath = filePath,
                    OriginalContent = root.ToFullString(),
                    TransformedContent = transformed,
                    MethodTransformations = new List<MethodTransformation>()
                });
            }
        }

        return results;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Core rewriting helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static string? TransformWithSemanticModel(
        SyntaxNode root,
        SemanticModel semanticModel,
        IReadOnlyDictionary<string, MethodTransformInfo> methodsById,
        IReadOnlyDictionary<string, IReadOnlySet<string>> awaitableCalleesByCallerId,
        IReadOnlySet<string> syncWrapperMethodIds)
    {
        var rewriter = new SemanticCallGraphRewriter(
            semanticModel, methodsById, awaitableCalleesByCallerId, syncWrapperMethodIds);

        var newRoot = rewriter.Visit(root);

        if (!rewriter.AnyMethodTransformed)
        {
            return null;
        }

        newRoot = EnsureUsingDirective(newRoot, "System.Threading.Tasks");
        return newRoot.ToFullString();
    }

    private static async Task TransformFromDisk(
        string filePath,
        IReadOnlyDictionary<string, MethodTransformInfo> methodsById,
        IReadOnlyDictionary<string, IReadOnlySet<string>> awaitableCalleesByCallerId,
        IReadOnlySet<string> syncWrapperMethodIds,
        List<FileTransformation> results,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return;
        }

        var source = await File.ReadAllTextAsync(filePath, cancellationToken);
        var tree = CSharpSyntaxTree.ParseText(source, cancellationToken: cancellationToken);
        var root = await tree.GetRootAsync(cancellationToken);

        // Without a semantic model we cannot do symbol-based matching.
        // Build a minimal compilation so we at least have type information.
        var compilation = CSharpCompilation.Create(
            assemblyName: "__fallback__",
            syntaxTrees: new[] { tree },
            references: new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) });

        var semanticModel = compilation.GetSemanticModel(tree);

        var transformed = TransformWithSemanticModel(
            root, semanticModel, methodsById, awaitableCalleesByCallerId, syncWrapperMethodIds);

        if (transformed != null)
        {
            results.Add(new FileTransformation
            {
                FilePath = filePath,
                OriginalContent = source,
                TransformedContent = transformed,
                MethodTransformations = new List<MethodTransformation>()
            });
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Data-preparation helpers
    // ──────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, IReadOnlySet<string>> BuildAwaitableCalleeMap(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph,
        HashSet<string> floodedMethodIds)
    {
        return callGraph.Calls
            .Where(c => floodedMethodIds.Contains(c.CallerId) && floodedMethodIds.Contains(c.CalleeId))
            .GroupBy(c => c.CallerId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlySet<string>)g.Select(c => c.CalleeId).ToHashSet());
    }

    private static Dictionary<string, MethodTransformInfo> BuildMethodTransformInfos(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph)
    {
        var result = new Dictionary<string, MethodTransformInfo>(StringComparer.Ordinal);

        foreach (var (methodId, composite) in callGraph.MethodMetadata)
        {
            if (!callGraph.Methods.TryGetValue(methodId, out var method))
            {
                continue;
            }

            if (string.IsNullOrEmpty(method.FilePath) || method.FilePath == "external")
            {
                continue;
            }

            var originalReturnType = composite.First.OriginalReturnType;
            var newReturnType = originalReturnType == "void"
                ? "Task"
                : $"Task<{originalReturnType}>";

            result[methodId] = new MethodTransformInfo
            {
                MethodId = method.Id,
                MethodName = method.Name,
                ContainingType = method.ContainingType,
                OriginalReturnType = originalReturnType,
                NewReturnType = newReturnType,
                StartLine = method.StartLine,
                EndLine = method.EndLine,
            };
        }

        return result;
    }

    private static Dictionary<string, List<string>> GroupFloodedMethodsByFile(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph,
        HashSet<string> floodedMethodIds)
    {
        var byFile = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var methodId in floodedMethodIds)
        {
            if (!callGraph.Methods.TryGetValue(methodId, out var method))
            {
                continue;
            }

            var filePath = method.FilePath;
            if (string.IsNullOrEmpty(filePath) || filePath == "external")
            {
                continue;
            }

            if (!byFile.TryGetValue(filePath, out var ids))
            {
                ids = new List<string>();
                byFile[filePath] = ids;
            }

            ids.Add(methodId);
        }

        return byFile;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Using-directive helper
    // ──────────────────────────────────────────────────────────────────────────

    private static SyntaxNode EnsureUsingDirective(SyntaxNode root, string namespaceName)
    {
        if (root is not CompilationUnitSyntax compilationUnit)
        {
            return root;
        }

        if (compilationUnit.Usings.Any(u => u.Name?.ToString() == namespaceName))
        {
            return root;
        }

        var usingDirective = UsingDirective(ParseName(namespaceName).WithLeadingTrivia(Space))
            .WithTrailingTrivia(LineFeed);

        return compilationUnit.AddUsings(usingDirective);
    }
}
