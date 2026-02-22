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
/// For each method present in <see cref="ICallGraphWithMetadata{TM,TC,TI,TO}.MethodMetadata"/>
/// the transformer:
/// <list type="bullet">
///   <item>Changes the return type (<c>void</c> → <c>Task</c>, <c>T</c> → <c>Task&lt;T&gt;</c>)</item>
///   <item>Adds <c>async</c> and <c>await</c> where needed</item>
///   <item>Skips <c>async</c>/<c>await</c> for sync wrapper methods (identified via
///         <see cref="SyncWrapperMethodMetadata.IsSyncWrapper"/>)</item>
/// </list>
/// The composite metadata type is
/// <c>CompositeMetadata&lt;FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata&gt;</c>:
/// <list type="bullet">
///   <item><c>First</c> — flooding info (original return type, depth, reason)</item>
///   <item><c>Second</c> — sync wrapper flag</item>
///   <item><c>Third</c> — Entity Framework caller flag</item>
/// </list>
/// </summary>
public class FloodedCallGraphTransformer
{
    private static readonly CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata> Empty =
        new()
        {
            First = new FloodingMethodMetadata { OriginalReturnType = "" },
            Second = SyncWrapperMethodMetadata.None,
            Third = EntityFrameworkMethodMetadata.None,
        };

    public async Task<IReadOnlyList<FileTransformation>> TransformAsync(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph,
        CancellationToken cancellationToken = default)
    {
        var floodedMethodIds = new HashSet<string>(callGraph.MethodMetadata.Keys);
        var byFile = GroupFloodedMethodsByFile(callGraph, floodedMethodIds);

        var results = new List<FileTransformation>();
        foreach (var (filePath, methodInfos) in byFile)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(filePath))
            {
                continue;
            }

            var source = await File.ReadAllTextAsync(filePath, cancellationToken);
            var transformed = TransformSource(source, methodInfos, floodedMethodIds, cancellationToken);

            if (transformed != source)
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

        return results;
    }

    private static Dictionary<string, List<MethodTransformEntry>> GroupFloodedMethodsByFile(
        ICallGraphWithMetadata<
            CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata>,
            EmptyGraphMetadata, EmptyGraphMetadata, EmptyGraphMetadata> callGraph,
        HashSet<string> floodedMethodIds)
    {
        var byFile = new Dictionary<string, List<MethodTransformEntry>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (methodId, composite) in callGraph.MethodMetadata)
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

            var callsToAwait = callGraph.Calls
                .Where(c => c.CallerId == methodId && floodedMethodIds.Contains(c.CalleeId))
                .ToList();

            if (!byFile.TryGetValue(filePath, out var list))
            {
                list = new List<MethodTransformEntry>();
                byFile[filePath] = list;
            }

            list.Add(new MethodTransformEntry(method, composite, callsToAwait));
        }

        return byFile;
    }

    private static string TransformSource(
        string sourceCode,
        List<MethodTransformEntry> entries,
        HashSet<string> floodedMethodIds,
        CancellationToken cancellationToken)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot(cancellationToken);

        var methodsByStartLine = new Dictionary<int, MethodTransformInfo>();
        var callSitesByLine = new Dictionary<int, CallSiteInfo>();
        var syncWrapperMethodIds = new HashSet<string>();

        foreach (var entry in entries)
        {
            var method = entry.Method;
            var floodingMeta = entry.Composite.First;
            var syncMeta = entry.Composite.Second;

            var originalReturnType = floodingMeta.OriginalReturnType;
            var newReturnType = originalReturnType == "void" ? "Task" : $"Task<{originalReturnType}>";

            methodsByStartLine[method.StartLine] = new MethodTransformInfo
            {
                MethodId = method.Id,
                MethodName = method.Name,
                ContainingType = method.ContainingType,
                OriginalReturnType = originalReturnType,
                NewReturnType = newReturnType,
                StartLine = method.StartLine,
                EndLine = method.EndLine,
            };

            if (syncMeta.IsSyncWrapper)
            {
                syncWrapperMethodIds.Add(method.Id);
            }

            foreach (var call in entry.CallsToAwait)
            {
                if (!callSitesByLine.ContainsKey(call.LineNumber))
                {
                    callSitesByLine[call.LineNumber] = new CallSiteInfo
                    {
                        CalleeMethodId = call.CalleeId,
                        LineNumber = call.LineNumber,
                        CalleeMethodName = ExtractMethodNameFromMethodId(call.CalleeId),
                    };
                }
            }
        }

        var rewriter = new AsyncMethodRewriter(methodsByStartLine, callSitesByLine, syncWrapperMethodIds);
        var newRoot = rewriter.Visit(root);

        if (!rewriter.AnyMethodTransformed)
        {
            return sourceCode;
        }

        newRoot = EnsureUsingDirective(newRoot, "System.Threading.Tasks");
        return newRoot.ToFullString();
    }

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

    private static string? ExtractMethodNameFromMethodId(string? methodId)
    {
        if (string.IsNullOrEmpty(methodId))
        {
            return null;
        }

        var parenIdx = methodId.IndexOf('(');
        if (parenIdx < 0)
        {
            parenIdx = methodId.Length;
        }

        var lastDot = methodId.LastIndexOf('.', parenIdx - 1);
        var nameStart = lastDot >= 0 ? lastDot + 1 : 0;

        if (nameStart >= parenIdx)
        {
            return null;
        }

        var name = methodId.Substring(nameStart, parenIdx - nameStart);
        var angleIdx = name.IndexOf('<');
        return angleIdx >= 0 ? name.Substring(0, angleIdx) : name;
    }

    private record MethodTransformEntry(
        IMethodNode Method,
        CompositeMetadata<FloodingMethodMetadata, SyncWrapperMethodMetadata, EntityFrameworkMethodMetadata> Composite,
        List<IMethodCall> CallsToAwait);
}
